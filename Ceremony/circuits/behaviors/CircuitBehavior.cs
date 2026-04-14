using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

#nullable disable

namespace circuits
{
    // ── Settings infrastructure ──────────────────────────────────

    public enum SettingType { Number, Toggle, Text, Select, Slider, ItemSlot }

    public sealed class NodeSettingField
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required SettingType Type { get; init; }
        public object Default { get; init; }
        public float Min { get; init; } = float.MinValue;
        public float Max { get; init; } = float.MaxValue;
        public string[] OptionKeys { get; init; }    // Select type: internal values
        public string[] OptionLabels { get; init; }  // Select type: display names
        public int SlotCount { get; init; } = 1;
    }

    public sealed class NodeSettingsDescriptor
    {
        public string Title { get; set; } = "Node Settings";
        public List<NodeSettingField> Fields { get; } = new();

        public NodeSettingsDescriptor AddNumber(string key, string label, float defaultVal, float min = 0, float max = float.MaxValue)
        {
            Fields.Add(new NodeSettingField { Key = key, Label = label, Type = SettingType.Number, Default = defaultVal, Min = min, Max = max });
            return this;
        }

        public NodeSettingsDescriptor AddToggle(string key, string label, bool defaultVal)
        {
            Fields.Add(new NodeSettingField { Key = key, Label = label, Type = SettingType.Toggle, Default = defaultVal });
            return this;
        }

        public NodeSettingsDescriptor AddText(string key, string label, string defaultVal = "")
        {
            Fields.Add(new NodeSettingField { Key = key, Label = label, Type = SettingType.Text, Default = defaultVal });
            return this;
        }

        public NodeSettingsDescriptor AddSelect(string key, string label, string[] optionKeys, string[] optionLabels, string defaultKey = null)
        {
            Fields.Add(new NodeSettingField { Key = key, Label = label, Type = SettingType.Select, Default = defaultKey ?? optionKeys[0], OptionKeys = optionKeys, OptionLabels = optionLabels });
            return this;
        }

        public NodeSettingsDescriptor AddSlider(string key, string label, int defaultVal, int min = 0, int max = 100)
        {
            Fields.Add(new NodeSettingField { Key = key, Label = label, Type = SettingType.Slider, Default = defaultVal, Min = min, Max = max });
            return this;
        }
        public NodeSettingsDescriptor AddItemSlot(string key, string label, int slotCount = 1)
        {
            Fields.Add(new NodeSettingField { Key = key, Label = label, Type = SettingType.ItemSlot, SlotCount = slotCount });
            return this;
        }
    }

    // ── Unified circuit node base class ──────────────────────────

    /// <summary>
    /// Single base class for every circuit node block entity behavior.
    /// Merges node identity (GUID, render offset, manager registration),
    /// port registry &amp; signal routing, optional animation state machine,
    /// generic settings dialog, variant helpers, and persistence.
    ///
    /// <para><b>Subclass contract:</b></para>
    /// <list type="bullet">
    ///   <item><see cref="BuildPorts"/> — declare ports with signal handlers</item>
    ///   <item><see cref="ConfigureSettings"/> (optional) — add fields for the generic dialog</item>
    ///   <item><see cref="OnInteract"/> (optional) — click-driven behavior</item>
    ///   <item><see cref="GetCurrentSettings"/> (optional) — supply current values for the dialog</item>
    ///   <item><see cref="ApplySettings"/> (optional) — react to dialog changes</item>
    /// </list>
    /// </summary>
    public abstract class CircuitBehavior : BlockEntityBehavior, INodePortsProvider, ISignalReceiver, ICircuitInteractable
    {
        // ═══════════════════════════════════════════════════════════
        //  Node identity (was BEBehaviorCircuitNode)
        // ═══════════════════════════════════════════════════════════

        private const string AttrNodeId = "circuits:nodeid";

        private CircuitsModSystem mgr;
        private Guid nodeId = Guid.Empty;
        private Vec3f renderOffset;

        public Guid NodeID => nodeId;
        public Vec3f RenderOffset => renderOffset;

        protected CircuitBehavior(BlockEntity be) : base(be) { }

        // ═══════════════════════════════════════════════════════════
        //  Port system (was BEBehaviorBasePorts)
        // ═══════════════════════════════════════════════════════════

        private Dictionary<string, PortSpec> _byId;
        private Dictionary<string, (string id, string name)> _portOverrides;
        private Dictionary<string, string> _actualToLogical;

        protected abstract IEnumerable<PortSpec> BuildPorts();

        // ═══════════════════════════════════════════════════════════
        //  Animation (optional — was BEBCNetAnimatable)
        // ═══════════════════════════════════════════════════════════

        private const int PacketIdTargetSync = 2010;
        private const float DefaultAnimFps = 30f;

        protected sealed class AnimStateDef
        {
            public int Index;
            public string PortId;
            public string PortName;
            public string OnAnim;
            public string OffAnim;
            public float AnimFps;
            public int OnFrames;
            public int OffFrames;
            public int CommitOnFrame = -1;
            public int CommitOffFrame = -1;
        }

        protected sealed class AnimStateRuntime
        {
            public AnimStateDef Def;
            public bool Value;
            public bool AnimTarget;
            public bool IsAnimating;
            public long CommitCallbackId;
            public long TransitionStartMs;
            public int TransitionDurationMs;
            public bool NeedsSnap;
            public bool AwaitingAnimState;
            public AnimationMetaData OnMeta;
            public AnimationMetaData OffMeta;
        }

        protected AnimStateRuntime[] AnimStates;
        private Dictionary<int, bool> _cachedAnimValues;

        protected BlockEntityAnimationUtil AnimUtil
            => Blockentity.GetBehavior<BEBehaviorAnimatable>()?.animUtil;

        protected bool HasAnimation => AnimStates != null && AnimStates.Length > 0;

        // ═══════════════════════════════════════════════════════════
        //  Settings dialog
        // ═══════════════════════════════════════════════════════════

        public const int PacketIdNodeSettings = 2020;

        private NodeSettingsDescriptor settingsDescriptor;
        private GuiDialogNodeSettings clientDialog;

        private Dictionary<string, ItemStack> _itemSlotStacks;
        private Dictionary<string, ItemStack> _cachedItemSlotStacks;

        protected bool HasSettings => settingsDescriptor != null && settingsDescriptor.Fields.Count > 0;

        // ═══════════════════════════════════════════════════════════
        //  Initialize
        // ═══════════════════════════════════════════════════════════

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            // --- Port overrides from JSON ---
            ReadPortOverrides(properties);

            base.Initialize(api, properties);

            // --- Node identity ---
            if (nodeId == Guid.Empty)
            {
                nodeId = Guid.NewGuid();
                Blockentity.MarkDirty();
            }

            ReadNodeOffset(properties);

            // --- Port cache ---
            InvalidatePortCache();

            // --- Animation (optional) ---
            ParseAnimStates(properties);
            if (HasAnimation)
            {
                if (_cachedAnimValues != null)
                {
                    foreach (var s in AnimStates)
                        if (_cachedAnimValues.TryGetValue(s.Def.Index, out var v))
                            s.Value = v;
                    _cachedAnimValues = null;
                }
                foreach (var s in AnimStates)
                    s.AnimTarget = s.Value;

                InitAnimator(api);
                foreach (var s in AnimStates)
                {
                    RequestAnimStart(s);
                    s.NeedsSnap = true;
                }

                if (api.Side == EnumAppSide.Client)
                {
                    Blockentity.RegisterGameTickListener(OnClientAnimTick, 20);
                    api.World.BlockAccessor.MarkBlockDirty(Pos);
                }

                if (api.Side == EnumAppSide.Server)
                {
                    foreach (var s in AnimStates)
                        TryEmitBool(s.Def.PortId, s.Value, out _);
                }
            }

            // --- Settings dialog ---
            settingsDescriptor = new NodeSettingsDescriptor();
            ConfigureSettings(settingsDescriptor);
            if (settingsDescriptor.Fields.Count == 0)
                settingsDescriptor = null;

            // --- Item slot settings ---
            InitItemSlotStacks();

            // --- Server registration ---
            if (api.Side == EnumAppSide.Server)
            {
                mgr ??= api.ModLoader.GetModSystem<CircuitsModSystem>();
                RegisterWithManager();
            }

            // Re-register ports (handles overrides applied before registration)
            ReRegisterPorts();
        }

        // ═══════════════════════════════════════════════════════════
        //  Node offset
        // ═══════════════════════════════════════════════════════════

        private void ReadNodeOffset(JsonObject properties)
        {
            if (properties?["nodeOffset"] != null)
            {
                float x = properties["nodeOffset"]["x"].AsFloat(0f);
                float y = properties["nodeOffset"]["y"].AsFloat(0f);
                float z = properties["nodeOffset"]["z"].AsFloat(0f);
                renderOffset = new Vec3f(x, y, z);
            }
            else
            {
                renderOffset = new Vec3f(0, 0, 0);
            }

            var block = Blockentity.Block;
            if (block?.Shape != null)
            {
                float rx = block.Shape.rotateX;
                float ry = block.Shape.rotateY;
                float rz = block.Shape.rotateZ;
                if (rx != 0 || ry != 0 || rz != 0)
                    renderOffset = RotateVec(renderOffset, rx, ry, rz);
            }
        }

        private static Vec3f RotateVec(Vec3f v, float degX, float degY, float degZ)
        {
            if (v.X == 0 && v.Y == 0 && v.Z == 0) return v;

            float x = v.X, y = v.Y, z = v.Z;

            if (degX != 0)
            {
                float r = degX * GameMath.DEG2RAD;
                float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
                float ny = y * c - z * s;
                float nz = y * s + z * c;
                y = ny; z = nz;
            }

            if (degY != 0)
            {
                float r = degY * GameMath.DEG2RAD;
                float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
                float nx = x * c + z * s;
                float nz = -x * s + z * c;
                x = nx; z = nz;
            }

            if (degZ != 0)
            {
                float r = degZ * GameMath.DEG2RAD;
                float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
                float nx = x * c - y * s;
                float ny = x * s + y * c;
                x = nx; y = ny;
            }

            return new Vec3f(x, y, z);
        }

        // ═══════════════════════════════════════════════════════════
        //  Port override from JSON
        // ═══════════════════════════════════════════════════════════

        private void ReadPortOverrides(JsonObject properties)
        {
            _portOverrides = null;
            _actualToLogical = null;

            var arr = properties?["ports"]?.AsArray();
            if (arr == null || arr.Length == 0) return;

            _portOverrides = new Dictionary<string, (string, string)>(arr.Length, StringComparer.Ordinal);
            _actualToLogical = new Dictionary<string, string>(arr.Length, StringComparer.Ordinal);

            for (int i = 0; i < arr.Length; i++)
            {
                var entry = arr[i];
                string defaultId = entry["default"]?.AsString(null);
                if (string.IsNullOrEmpty(defaultId)) continue;

                string newId = entry["id"]?.AsString(null);
                string newName = entry["name"]?.AsString(null);
                if (newId == null && newName == null) continue;

                _portOverrides[defaultId] = (newId, newName);
                if (newId != null && newId != defaultId)
                    _actualToLogical[newId] = defaultId;
            }

            if (_portOverrides.Count == 0)
            {
                _portOverrides = null;
                _actualToLogical = null;
            }
            else if (_actualToLogical.Count == 0)
            {
                _actualToLogical = null;
            }
        }

        private string NormalizeToLogical(string portId)
        {
            if (_actualToLogical != null && _actualToLogical.TryGetValue(portId, out var logical))
                return logical;
            return portId;
        }

        // ═══════════════════════════════════════════════════════════
        //  Shared helpers
        // ═══════════════════════════════════════════════════════════

        protected static bool AnyTrue(Dictionary<PortKey, bool> signals)
        {
            foreach (var v in signals.Values)
                if (v) return true;
            return false;
        }

        // ── Item-slot setting helpers ────────────────────────────────

        public ItemStack GetItemSlotStack(string key)
        {
            if (_itemSlotStacks != null && _itemSlotStacks.TryGetValue(key, out var stack))
                return stack;
            return null;
        }

        protected void SetItemSlotStack(string key, ItemStack stack)
        {
            _itemSlotStacks ??= new();
            _itemSlotStacks[key] = stack;
            Blockentity.MarkDirty(true);
        }

        /// <summary>
        /// Returns true if the player's active hotbar slot contains the same
        /// item/block as the item-slot setting identified by <paramref name="settingKey"/>.
        /// Compares item type AND attributes (e.g. a specific key identity).
        /// If no item-slot value has been configured, returns <c>true</c> (no restriction).
        /// </summary>
        protected bool IsHeldItemMatch(IPlayer player, string settingKey)
        {
            var required = GetItemSlotStack(settingKey);
            if (required == null) return true;
            var held = player?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            if (held == null) return false;
            return required.Equals(Api.World, held);
        }

        internal static string SerializeItemStack(ItemStack stack)
        {
            if (stack == null) return "";
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            var tree = new TreeAttribute();
            tree.SetItemstack("s", stack);
            tree.ToBytes(bw);
            return Convert.ToBase64String(ms.ToArray());
        }

        internal static ItemStack DeserializeItemStack(string s, IWorldAccessor world)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                var bytes = Convert.FromBase64String(s);
                using var ms = new MemoryStream(bytes);
                using var br = new BinaryReader(ms);
                var tree = new TreeAttribute();
                tree.FromBytes(br);
                var stack = tree.GetItemstack("s");
                stack?.ResolveBlockOrItem(world);
                return stack;
            }
            catch { return null; }
        }

        private void InitItemSlotStacks()
        {
            if (settingsDescriptor == null) return;

            bool hasSlots = false;
            foreach (var field in settingsDescriptor.Fields)
            {
                if (field.Type != SettingType.ItemSlot) continue;
                _itemSlotStacks ??= new();
                if (!_itemSlotStacks.ContainsKey(field.Key))
                    _itemSlotStacks[field.Key] = null;
                hasSlots = true;
            }

            if (hasSlots && _cachedItemSlotStacks != null)
            {
                foreach (var kvp in _cachedItemSlotStacks)
                {
                    if (_itemSlotStacks.ContainsKey(kvp.Key))
                        _itemSlotStacks[kvp.Key] = kvp.Value;
                }
                _cachedItemSlotStacks = null;
            }
        }

        private void ExtractItemSlotValues(Dictionary<string, string> values)
        {
            if (_itemSlotStacks == null || settingsDescriptor == null) return;

            foreach (var field in settingsDescriptor.Fields)
            {
                if (field.Type != SettingType.ItemSlot) continue;
                if (values.TryGetValue(field.Key, out var ser))
                    _itemSlotStacks[field.Key] = DeserializeItemStack(ser, Api.World);
            }
        }

        protected bool ReadStateVariant()
        {
            var blk = Api.World.BlockAccessor.GetBlock(Pos);
            return blk?.Variant?["state"] == "on";
        }

        protected bool SetStateVariant(bool on)
        {
            var ba = Api.World.BlockAccessor;
            var cur = ba.GetBlock(Pos);
            if (cur?.Variant == null || !cur.Variant.ContainsKey("state")) return false;

            var desired = on ? "on" : "off";
            if (cur.Variant.TryGetValue("state", out var curState) && curState == desired) return false;

            var newCode = cur.CodeWithVariant("state", desired);
            var newBlock = ba.GetBlock(newCode);
            if (newBlock == null) return false;

            ba.ExchangeBlock(newBlock.BlockId, Pos);
            ba.MarkBlockDirty(Pos);
            ReRegisterNode();
            return true;
        }

        protected void ReRegisterNode()
        {
            if (Api?.Side == EnumAppSide.Server)
            {
                mgr ??= Api.ModLoader.GetModSystem<CircuitsModSystem>();
                mgr?.RegisterOrUpdateNode(NodeID, Pos, renderOffset);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Port enumeration (INodePortsProvider)
        // ═══════════════════════════════════════════════════════════

        public IEnumerable<PortDef> GetPorts()
        {
            EnsureBuilt();
            foreach (var kv in _byId)
            {
                if (kv.Value.Def.PortID != kv.Key) continue;
                yield return kv.Value.Def;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  ISignalReceiver (explicit — normalizes to logical IDs)
        // ═══════════════════════════════════════════════════════════

        bool ISignalReceiver.OnSignal(string inPortId, SignalType type, object value, PortKey from)
            => OnSignal(NormalizeToLogical(inPortId), type, value, from);

        void ISignalReceiver.OnSourceDisconnected(string inPortId, PortKey from)
            => OnSourceDisconnected(NormalizeToLogical(inPortId), from);

        public bool OnSignal(string inPortId, SignalType type, object value, PortKey from)
        {
            EnsureBuilt();

            if (!_byId.TryGetValue(inPortId, out var spec)) return false;
            if (spec.Def.Dir != PortDir.In) return false;
            if (type != spec.Def.Type) return false;
            if (spec.OnSignalCore == null) return false;

            if (spec.RequireTrueForBool && spec.Def.Type == SignalType.Bool)
            {
                if (value is not bool b || !b) return true;
            }

            return spec.OnSignalCore.Invoke(value, from);
        }

        public virtual void OnSourceDisconnected(string inPortId, PortKey from)
        {
            EnsureBuilt();
            if (!_byId.TryGetValue(inPortId, out var spec)) return;
            if (spec.Def.Dir != PortDir.In) return;
            if (spec.Def.Type == SignalType.Event) return;
            if (spec.OnSignalCore == null) return;

            object defaultVal = spec.Def.Type switch
            {
                SignalType.Bool => (object)false,
                SignalType.Int => (object)0,
                SignalType.Float => (object)0f,
                SignalType.String => (object)"",
                _ => null
            };
            if (defaultVal != null)
                spec.OnSignalCore.Invoke(defaultVal, from);
        }

        // ═══════════════════════════════════════════════════════════
        //  Emit helpers
        // ═══════════════════════════════════════════════════════════

        protected bool TryEmitBool(string outPortId, bool value, out string reason)
            => TryEmit(outPortId, SignalType.Bool, value, out reason);

        protected bool TryEmitEvent(string outPortId, out string reason)
            => TryEmit(outPortId, SignalType.Event, null, out reason);

        protected bool TryEmit(string outPortId, SignalType type, object value, out string reason)
        {
            if (Api?.Side != EnumAppSide.Server)
            {
                reason = "Emit can only happen server-side.";
                return false;
            }

            EnsureBuilt();

            if (!_byId.TryGetValue(outPortId, out var spec))
            {
                reason = "Unknown port.";
                return false;
            }

            var def = spec.Def;
            if (def.Dir != PortDir.Out) { reason = "Port is not an output."; return false; }
            if (def.Type != type) { reason = $"Port type mismatch ({def.Type} vs {type})."; return false; }

            mgr ??= Api.ModLoader.GetModSystem<CircuitsModSystem>();
            return mgr.TryEmit(NodeID, def.PortID, type, value, out reason);
        }

        // ═══════════════════════════════════════════════════════════
        //  Port cache
        // ═══════════════════════════════════════════════════════════

        private void EnsureBuilt()
        {
            if (_byId != null) return;

            _byId = new Dictionary<string, PortSpec>(StringComparer.Ordinal);

            // Collect animation output ports
            if (HasAnimation)
            {
                foreach (var s in AnimStates)
                {
                    var animPortDef = new PortDef
                    {
                        PortID = s.Def.PortId,
                        Dir = PortDir.Out,
                        Type = SignalType.Bool,
                        DisplayName = s.Def.PortName
                    };
                    AddPortSpec(new PortSpec(animPortDef));
                }
            }

            foreach (var spec in BuildPorts())
                AddPortSpec(spec);
        }

        private void AddPortSpec(PortSpec spec)
        {
            var logicalId = spec.Def.PortID;
            if (string.IsNullOrEmpty(logicalId))
                throw new Exception($"{GetType().Name} has empty PortID");

            string actualId = logicalId;
            string displayName = spec.Def.DisplayName;
            if (_portOverrides != null && _portOverrides.TryGetValue(logicalId, out var ov))
            {
                actualId = ov.id ?? logicalId;
                displayName = ov.name ?? displayName;
            }

            PortDef def;
            if (actualId != logicalId || displayName != spec.Def.DisplayName)
            {
                def = new PortDef
                {
                    PortID = actualId,
                    Dir = spec.Def.Dir,
                    Type = spec.Def.Type,
                    MaxInputs = spec.Def.MaxInputs,
                    MaxOutputs = spec.Def.MaxOutputs,
                    DisplayName = displayName
                };
            }
            else
            {
                def = spec.Def;
            }

            var actualSpec = new PortSpec(def, spec.OnSignalCore, spec.RequireTrueForBool);

            if (_byId.ContainsKey(actualId))
                return; // animation port already registered, subclass yields same ID — skip
            _byId[actualId] = actualSpec;

            if (actualId != logicalId && !_byId.ContainsKey(logicalId))
                _byId[logicalId] = actualSpec;
        }

        protected void InvalidatePortCache() => _byId = null;

        protected void ReRegisterPorts()
        {
            InvalidatePortCache();

            if (Api?.Side != EnumAppSide.Server) return;

            mgr ??= Api.ModLoader.GetModSystem<CircuitsModSystem>();
            mgr?.RegisterPorts(NodeID, GetPorts());
        }

        // ═══════════════════════════════════════════════════════════
        //  Manager registration
        // ═══════════════════════════════════════════════════════════

        private void RegisterWithManager()
        {
            mgr.RegisterOrUpdateNode(nodeId, Pos, renderOffset);

            var ports = new List<PortDef>();
            foreach (var p in GetPorts())
                ports.Add(p);

            mgr.RegisterPorts(nodeId, ports);
        }

        // ═══════════════════════════════════════════════════════════
        //  Animation — parsing
        // ═══════════════════════════════════════════════════════════

        private void ParseAnimStates(JsonObject properties)
        {
            var arr = properties?["states"]?.AsArray();
            if (arr == null || arr.Length == 0)
            {
                AnimStates = null;
                return;
            }

            AnimStates = new AnimStateRuntime[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                var j = arr[i];
                var def = new AnimStateDef
                {
                    Index = j["index"].AsInt(i),
                    PortId = j["portId"].AsString($"anim.state{i}"),
                    PortName = j["portName"].AsString($"State {i}"),
                    OnAnim = j["onAnim"].AsString(null),
                    OffAnim = j["offAnim"].AsString(null),
                    AnimFps = j["animFps"].AsFloat(DefaultAnimFps),
                    CommitOnFrame = j["commitOnFrame"].AsInt(-1),
                    CommitOffFrame = j["commitOffFrame"].AsInt(-1)
                };
                AnimStates[i] = CreateRuntime(def);
            }
        }

        private static AnimStateRuntime CreateRuntime(AnimStateDef def)
        {
            var rt = new AnimStateRuntime { Def = def };
            rt.OnMeta = new AnimationMetaData
            {
                Animation = def.OnAnim, Code = def.OnAnim,
                AnimationSpeed = 0.0001f, EaseInSpeed = 999f, EaseOutSpeed = 999f
            };
            if (def.OffAnim != null)
            {
                rt.OffMeta = new AnimationMetaData
                {
                    Animation = def.OffAnim, Code = def.OffAnim,
                    AnimationSpeed = 0.0001f, EaseInSpeed = 999f, EaseOutSpeed = 999f
                };
            }
            return rt;
        }

        // ═══════════════════════════════════════════════════════════
        //  Animation — init
        // ═══════════════════════════════════════════════════════════

        private void InitAnimator(ICoreAPI api)
        {
            var block = Blockentity.Block;
            if (block?.Shape?.Base == null) return;

            var shapeLoc = block.Shape.Base.Clone()
                .WithPathPrefixOnce("shapes/")
                .WithPathAppendixOnce(".json");

            Shape shape = Shape.TryGet(api, shapeLoc);
            if (shape == null) return;

            var rot = new Vec3f(block.Shape.rotateX, block.Shape.rotateY, block.Shape.rotateZ);

            if (api.Side == EnumAppSide.Client)
            {
                AnimUtil.InitializeAnimator("cnetanim-" + Pos, shape, null, rot);

                float rx = rot.X, ry = rot.Y, rz = rot.Z;
                if ((rx != 0 || rz != 0) &&
                    AnimUtil.renderer is AnimatableRenderer arenderer)
                {
                    float[] ct = Mat4f.Create();
                    Mat4f.Translate(ct, ct, 0.5f, 0.5f, 0.5f);
                    Mat4f.RotateX(ct, ct, rx * GameMath.DEG2RAD);
                    Mat4f.RotateY(ct, ct, ry * GameMath.DEG2RAD);
                    Mat4f.RotateZ(ct, ct, rz * GameMath.DEG2RAD);
                    Mat4f.Translate(ct, ct, -0.5f, -0.5f, -0.5f);
                    arenderer.CustomTransform = ct;
                }
            }
            else
            {
                shape.InitForAnimations(api.Logger, shapeLoc.ToString());
                AnimUtil.InitializeAnimatorServer("cnetanim-" + Pos, shape);
            }

            PopulateFrameCounts(shape);
        }

        private void PopulateFrameCounts(Shape shape)
        {
            foreach (var s in AnimStates)
            {
                s.Def.OnFrames = GetShapeFrameCount(shape, s.Def.OnAnim);
                s.Def.OffFrames = s.Def.OffAnim != null
                    ? GetShapeFrameCount(shape, s.Def.OffAnim)
                    : s.Def.OnFrames;
            }
        }

        private static int GetShapeFrameCount(Shape shape, string code)
        {
            if (shape?.Animations == null || code == null) return 20;
            foreach (var a in shape.Animations)
                if (string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase))
                    return Math.Max(1, a.QuantityFrames - 1);
            return 20;
        }

        // ═══════════════════════════════════════════════════════════
        //  Animation — playback helpers
        // ═══════════════════════════════════════════════════════════

        private void RequestAnimStart(AnimStateRuntime s)
        {
            var util = AnimUtil;
            if (util == null) return;

            s.OnMeta.AnimationSpeed = 0.0001f;
            util.StartAnimation(s.OnMeta);

            if (s.OffMeta != null)
            {
                s.OffMeta.AnimationSpeed = 0.0001f;
                util.StartAnimation(s.OffMeta);
            }

            s.AwaitingAnimState = true;
        }

        protected void BeginTransition(int index, bool target)
        {
            if (Api.Side != EnumAppSide.Server) return;
            if (index < 0 || !HasAnimation || index >= AnimStates.Length) return;

            var s = AnimStates[index];
            if (s.IsAnimating) return;
            if (s.AnimTarget == target && s.Value == target) return;

            s.AnimTarget = target;
            s.IsAnimating = true;

            float fps = GetPlaybackFps(s, target);
            int totalFrames = target ? s.Def.OnFrames : s.Def.OffFrames;
            int commitFrame = target ? s.Def.CommitOnFrame : s.Def.CommitOffFrame;
            int effectiveFrames = (commitFrame >= 0 && commitFrame <= totalFrames)
                ? commitFrame : totalFrames;
            int durationMs = Math.Max(50, (int)(effectiveFrames / fps * 1000f));

            int fullDurationMs = Math.Max(50, (int)(totalFrames / fps * 1000f));
            s.TransitionStartMs = Api.World.ElapsedMilliseconds;
            s.TransitionDurationMs = fullDurationMs;

            BroadcastTargetSync();

            CancelCommitCallback(s);
            s.CommitCallbackId = Api.World.RegisterCallback(_ =>
            {
                s.CommitCallbackId = 0;
                CommitState(index);
            }, durationMs);
        }

        private void CommitState(int index)
        {
            var s = AnimStates[index];
            s.Value = s.AnimTarget;
            s.IsAnimating = false;

            TryEmitBool(s.Def.PortId, s.Value, out _);
            Blockentity.MarkDirty(true);

            OnAnimStateCommitted(index, s.Value);
        }

        protected virtual void OnAnimStateCommitted(int index, bool value) { }

        private void CancelCommitCallback(AnimStateRuntime s)
        {
            if (s.CommitCallbackId != 0)
            {
                Api.World.UnregisterCallback(s.CommitCallbackId);
                s.CommitCallbackId = 0;
            }
        }

        protected virtual float GetPlaybackFps(AnimStateRuntime s, bool targetValue)
            => s.Def.AnimFps;

        // ═══════════════════════════════════════════════════════════
        //  Animation — client tick
        // ═══════════════════════════════════════════════════════════

        private void OnClientAnimTick(float dt)
        {
            var util = AnimUtil;
            if (util?.animator == null) return;

            foreach (var s in AnimStates)
            {
                if (s.Def.OffAnim != null)
                    TickSeparateAnims(s, dt, util);
                else
                    TickSingleAnim(s, dt, util);
            }
        }

        private void TickSingleAnim(AnimStateRuntime s, float dt, BlockEntityAnimationUtil util)
        {
            var anim = util.animator.GetAnimationState(s.Def.OnAnim);
            if (anim == null)
            {
                if (!s.AwaitingAnimState) RequestAnimStart(s);
                return;
            }
            s.AwaitingAnimState = false;

            if (!anim.Running)
            {
                RequestAnimStart(s);
                s.NeedsSnap = true;
                return;
            }

            int maxFrame = anim.Animation.QuantityFrames - 1;
            anim.EasingFactor = 1f;
            anim.BlendedWeight = 1f;

            if (s.NeedsSnap)
            {
                anim.CurrentFrame = s.Value ? maxFrame : 0;
                s.NeedsSnap = false;
                s.OnMeta.AnimationSpeed = 0f;
                return;
            }

            float fps = GetPlaybackFps(s, s.AnimTarget);
            if (s.AnimTarget)
            {
                if (anim.CurrentFrame < maxFrame)
                    anim.CurrentFrame = Math.Min(anim.CurrentFrame + fps * dt, maxFrame);
            }
            else
            {
                if (anim.CurrentFrame > 0f)
                    anim.CurrentFrame = Math.Max(anim.CurrentFrame - fps * dt, 0f);
            }
        }

        private void TickSeparateAnims(AnimStateRuntime s, float dt, BlockEntityAnimationUtil util)
        {
            var onAnim = util.animator.GetAnimationState(s.Def.OnAnim);
            var offAnim = util.animator.GetAnimationState(s.Def.OffAnim);

            if (onAnim == null || offAnim == null)
            {
                if (!s.AwaitingAnimState) RequestAnimStart(s);
                return;
            }
            s.AwaitingAnimState = false;

            if (!onAnim.Running || !offAnim.Running)
            {
                RequestAnimStart(s);
                s.NeedsSnap = true;
                return;
            }

            int onMax = onAnim.Animation.QuantityFrames - 1;
            int offMax = offAnim.Animation.QuantityFrames - 1;

            onAnim.EasingFactor = 1f;
            offAnim.EasingFactor = 1f;

            if (s.NeedsSnap)
            {
                if (s.Value)
                {
                    onAnim.CurrentFrame = onMax; onAnim.BlendedWeight = 1f;
                    offAnim.CurrentFrame = 0;    offAnim.BlendedWeight = 0f;
                }
                else
                {
                    offAnim.CurrentFrame = offMax; offAnim.BlendedWeight = 1f;
                    onAnim.CurrentFrame = 0;       onAnim.BlendedWeight = 0f;
                }
                s.NeedsSnap = false;
                s.OnMeta.AnimationSpeed = 0f;
                s.OffMeta.AnimationSpeed = 0f;
                return;
            }

            float fps = GetPlaybackFps(s, s.AnimTarget);

            if (s.AnimTarget)
            {
                onAnim.BlendedWeight = 1f;
                offAnim.BlendedWeight = 0f;
                offAnim.CurrentFrame = 0;
                if (onAnim.CurrentFrame < onMax)
                    onAnim.CurrentFrame = Math.Min(onAnim.CurrentFrame + fps * dt, onMax);
            }
            else
            {
                offAnim.BlendedWeight = 1f;
                onAnim.BlendedWeight = 0f;
                onAnim.CurrentFrame = 0;
                if (offAnim.CurrentFrame < offMax)
                    offAnim.CurrentFrame = Math.Min(offAnim.CurrentFrame + fps * dt, offMax);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Animation — network sync
        // ═══════════════════════════════════════════════════════════

        private void BroadcastTargetSync()
        {
            if (Api is not ICoreServerAPI sapi || !HasAnimation) return;

            byte[] data = new byte[AnimStates.Length * 5];
            for (int i = 0; i < AnimStates.Length; i++)
            {
                data[i * 5] = (byte)(AnimStates[i].AnimTarget ? 1 : 0);
                int dur = AnimStates[i].TransitionDurationMs;
                data[i * 5 + 1] = (byte)(dur & 0xFF);
                data[i * 5 + 2] = (byte)((dur >> 8) & 0xFF);
                data[i * 5 + 3] = (byte)((dur >> 16) & 0xFF);
                data[i * 5 + 4] = (byte)((dur >> 24) & 0xFF);
            }

            sapi.Network.BroadcastBlockEntityPacket(Pos, PacketIdTargetSync, data);
        }

        public override void OnReceivedServerPacket(int packetid, byte[] data)
        {
            if (packetid == PacketIdTargetSync && data != null && HasAnimation)
            {
                int stride = 5;
                int count = Math.Min(data.Length / stride, AnimStates.Length);
                for (int i = 0; i < count; i++)
                {
                    AnimStates[i].AnimTarget = data[i * stride] != 0;
                    AnimStates[i].TransitionDurationMs =
                        data[i * stride + 1] |
                        (data[i * stride + 2] << 8) |
                        (data[i * stride + 3] << 16) |
                        (data[i * stride + 4] << 24);
                    AnimStates[i].TransitionStartMs = Api.World.ElapsedMilliseconds;
                    AnimStates[i].IsAnimating = true;
                }
            }

            base.OnReceivedServerPacket(packetid, data);
        }

        // ═══════════════════════════════════════════════════════════
        //  Interaction (ICircuitInteractable)
        // ═══════════════════════════════════════════════════════════

        private static readonly AssetLocation WrenchCode = new("circuits:circuitwrench");

        protected static bool IsHoldingWrench(IPlayer byPlayer)
        {
            var code = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code;
            return code != null && code.Equals(WrenchCode);
        }

        public virtual bool OnBlockInteractStart(
            IWorldAccessor world, IPlayer byPlayer,
            BlockSelection blockSel, ref EnumHandling handling)
        {
            if (IsHoldingWrench(byPlayer) && HasSettings)
            {
                if (Api.Side == EnumAppSide.Client)
                    OpenSettingsDialog();
                handling = EnumHandling.PreventDefault;
                return true;
            }

            return OnInteract(world, byPlayer, blockSel, ref handling);
        }

        protected virtual bool OnInteract(
            IWorldAccessor world, IPlayer byPlayer,
            BlockSelection blockSel, ref EnumHandling handling)
        {
            if (HasAnimation)
            {
                if (Api.Side != EnumAppSide.Server)
                {
                    handling = EnumHandling.PreventDefault;
                    return true;
                }

                if (AnimStates.Length > 0 && !AnimStates[0].IsAnimating)
                    BeginTransition(0, !AnimStates[0].AnimTarget);

                handling = EnumHandling.PreventDefault;
                return true;
            }

            handling = EnumHandling.PassThrough;
            return false;
        }

        // ═══════════════════════════════════════════════════════════
        //  Settings dialog
        // ═══════════════════════════════════════════════════════════

        protected virtual void ConfigureSettings(NodeSettingsDescriptor descriptor) { }

        protected virtual Dictionary<string, object> GetCurrentSettings() => new();

        protected virtual void ApplySettings(Dictionary<string, string> values) { }

        private void OpenSettingsDialog()
        {
            if (clientDialog != null)
            {
                clientDialog.TryClose();
                return;
            }

            var capi = (ICoreClientAPI)Api;
            var settings = GetCurrentSettings();
            if (_itemSlotStacks != null)
            {
                foreach (var kvp in _itemSlotStacks)
                    settings[kvp.Key] = kvp.Value;
            }
            clientDialog = new GuiDialogNodeSettings(Pos, settingsDescriptor, settings, capi,
                onSaved: values =>
                {
                    ExtractItemSlotValues(values);
                    ApplySettings(values);
                });
            clientDialog.TryOpen();
            clientDialog.OnClosed += () =>
            {
                clientDialog?.Dispose();
                clientDialog = null;
            };
        }

        private void DisposeDialog()
        {
            clientDialog?.TryClose();
            clientDialog?.Dispose();
            clientDialog = null;
        }

        public override void OnReceivedClientPacket(IPlayer fromPlayer, int packetid, byte[] data)
        {
            if (packetid == PacketIdNodeSettings)
            {
                var packet = SerializerUtil.Deserialize<EditNodeSettingsPacket>(data);
                if (packet?.Values != null)
                {
                    ExtractItemSlotValues(packet.Values);
                    ApplySettings(packet.Values);
                    Blockentity.MarkDirty(true);
                }
                return;
            }

            base.OnReceivedClientPacket(fromPlayer, packetid, data);
        }

        // ═══════════════════════════════════════════════════════════
        //  Persistence
        // ═══════════════════════════════════════════════════════════

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            // Node ID
            string nid = tree.GetString(AttrNodeId);
            if (!string.IsNullOrEmpty(nid)) Guid.TryParse(nid, out nodeId);

            // Animation states (updated on server sync; initial load handled in Initialize)
            if (AnimStates != null)
            {
                foreach (var s in AnimStates)
                    s.Value = tree.GetBool($"anim:s{s.Def.Index}");
            }
            else
            {
                // AnimStates not yet parsed; snapshot values for Initialize
                _cachedAnimValues = new Dictionary<int, bool>();
                for (int i = 0; i < 16; i++)
                {
                    string key = $"anim:s{i}";
                    if (tree.HasAttribute(key))
                        _cachedAnimValues[i] = tree.GetBool(key);
                }
                if (_cachedAnimValues.Count == 0)
                    _cachedAnimValues = null;
            }

            // Item slot stacks
            var slotKeysStr = tree.GetString("circuit:slotkeys");
            if (!string.IsNullOrEmpty(slotKeysStr))
            {
                var keys = slotKeysStr.Split(',');
                if (_itemSlotStacks != null)
                {
                    foreach (var key in keys)
                    {
                        if (!_itemSlotStacks.ContainsKey(key)) continue;
                        var stack = tree.GetItemstack("circuit:slot:" + key);
                        stack?.ResolveBlockOrItem(worldAccessForResolve);
                        _itemSlotStacks[key] = stack;
                    }
                }
                else
                {
                    _cachedItemSlotStacks ??= new();
                    foreach (var key in keys)
                    {
                        var stack = tree.GetItemstack("circuit:slot:" + key);
                        stack?.ResolveBlockOrItem(worldAccessForResolve);
                        _cachedItemSlotStacks[key] = stack;
                    }
                }
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            // Node ID
            tree.SetString(AttrNodeId, nodeId.ToString("N"));

            // Animation states
            if (AnimStates != null)
            {
                foreach (var s in AnimStates)
                    tree.SetBool($"anim:s{s.Def.Index}", s.Value);
            }

            // Item slot stacks
            if (_itemSlotStacks != null && _itemSlotStacks.Count > 0)
            {
                tree.SetString("circuit:slotkeys", string.Join(",", _itemSlotStacks.Keys));
                foreach (var kvp in _itemSlotStacks)
                    tree.SetItemstack("circuit:slot:" + kvp.Key, kvp.Value);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  Block info
        // ═══════════════════════════════════════════════════════════

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            if (Api is ICoreClientAPI capi && capi.Settings.Bool["extendedDebugInfo"])
                dsc.AppendLine($"NodeID: {nodeId}");

            if (HasAnimation)
            {
                foreach (var s in AnimStates)
                {
                    string label = AnimStates.Length == 1 ? "State" : s.Def.PortName;
                    dsc.AppendLine($"{label}: {FormatAnimStateInfo(s)}");
                }
            }
        }

        protected virtual string FormatAnimStateInfo(AnimStateRuntime s)
            => s.Value ? "ON" : "OFF";

        // ═══════════════════════════════════════════════════════════
        //  Tesselation (suppress static mesh for animated blocks)
        // ═══════════════════════════════════════════════════════════

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (HasAnimation)
                return true;

            return base.OnTesselation(mesher, tessThreadTesselator);
        }

        // ═══════════════════════════════════════════════════════════
        //  Carry-data: settings-only serialization for drop / pick
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Write only the user-configurable settings (the ones exposed via
        /// the settings GUI) into <paramref name="tree"/>.
        /// Override in subclasses that have settings.
        /// </summary>
        public virtual void WriteSettingsToTree(ITreeAttribute tree)
        {
            if (AnimStates != null)
            {
                foreach (var s in AnimStates)
                    tree.SetBool($"anim:s{s.Def.Index}", s.Value);
            }

            if (_itemSlotStacks != null && _itemSlotStacks.Count > 0)
            {
                tree.SetString("circuit:slotkeys", string.Join(",", _itemSlotStacks.Keys));
                foreach (var kvp in _itemSlotStacks)
                    tree.SetItemstack("circuit:slot:" + kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Read back settings written by <see cref="WriteSettingsToTree"/>.
        /// Override in subclasses that have settings.
        /// </summary>
        public virtual void ReadSettingsFromTree(ITreeAttribute tree)
        {
            if (AnimStates != null)
            {
                foreach (var s in AnimStates)
                {
                    s.Value = tree.GetBool($"anim:s{s.Def.Index}");
                    s.AnimTarget = s.Value;
                    s.NeedsSnap = true;
                }
            }

            if (_itemSlotStacks != null)
            {
                var slotKeysStr = tree.GetString("circuit:slotkeys");
                if (!string.IsNullOrEmpty(slotKeysStr))
                {
                    foreach (var key in slotKeysStr.Split(','))
                    {
                        if (!_itemSlotStacks.ContainsKey(key)) continue;
                        var stack = tree.GetItemstack("circuit:slot:" + key);
                        stack?.ResolveBlockOrItem(Api.World);
                        _itemSlotStacks[key] = stack;
                    }
                }
            }
        }

        public override void OnBlockPlaced(ItemStack byItemStack = null)
        {
            base.OnBlockPlaced(byItemStack);

            if (byItemStack?.Attributes == null) return;
            var stored = byItemStack.Attributes["circuits:bedata"] as ITreeAttribute;
            if (stored == null) return;

            ReadSettingsFromTree(stored);

            if (HasAnimation && Api?.Side == EnumAppSide.Server)
            {
                foreach (var s in AnimStates)
                    TryEmitBool(s.Def.PortId, s.Value, out _);
            }

            Blockentity.MarkDirty(true);
        }

        // ═══════════════════════════════════════════════════════════
        //  Cleanup
        // ═══════════════════════════════════════════════════════════

        public override void OnBlockRemoved()
        {
            if (Api?.Side == EnumAppSide.Server)
            {
                mgr ??= Api.ModLoader.GetModSystem<CircuitsModSystem>();
                mgr.RemoveAllLinksForNode(NodeID);
                mgr.UnregisterNode(nodeId);

                if (HasAnimation)
                {
                    foreach (var s in AnimStates)
                        CancelCommitCallback(s);
                }
            }

            DisposeDialog();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            DisposeDialog();
            base.OnBlockUnloaded();
        }

        // ═══════════════════════════════════════════════════════════
        //  Utility: format durations
        // ═══════════════════════════════════════════════════════════

        protected static string FormatDuration(long ms)
        {
            if (ms <= 0) return "0s";

            long totalSeconds = (ms + 999) / 1000;
            if (totalSeconds < 60)
                return $"{totalSeconds}s";

            long minutes = totalSeconds / 60;
            long seconds = totalSeconds % 60;

            if (minutes < 60)
                return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";

            long hours = minutes / 60;
            minutes %= 60;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        // ═══════════════════════════════════════════════════════════
        //  PortSpec
        // ═══════════════════════════════════════════════════════════

        protected readonly struct PortSpec
        {
            public readonly PortDef Def;
            public readonly global::System.Func<object, PortKey, bool> OnSignalCore;
            public readonly bool RequireTrueForBool;

            public PortSpec(
                PortDef def,
                global::System.Func<object, PortKey, bool> onSignalCore = null,
                bool requireTrueForBool = false)
            {
                Def = def;
                OnSignalCore = onSignalCore;
                RequireTrueForBool = requireTrueForBool;
            }
        }
    }
}
