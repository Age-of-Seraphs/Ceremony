using HarmonyLib;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

#nullable disable

namespace circuits
{
    public class CircuitDoorBehavior : CircuitBehavior
    {
        public const string PortIdOpen = "door.open";

        private readonly Dictionary<PortKey, bool> inputSignals = [];
        private CircuitsModSystem mgr;

        public CircuitDoorBehavior(BlockEntity be) : base(be) { }

        public override void Initialize(ICoreAPI api, Vintagestory.API.Datastructures.JsonObject properties)
        {
            base.Initialize(api, properties);
            mgr = api.ModLoader.GetModSystem<CircuitsModSystem>();
        }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdOpen,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Door Open/Closed"
                },
                new global::System.Func<object, PortKey, bool>(OnSetOpen)
            );
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor descriptor)
        {
            descriptor.Title = "Door Settings";
            descriptor.AddItemSlot("keyItem", "Key Item");
        }

        // ── Lock / key check (called by Harmony patch) ───────────

        private static readonly AssetLocation WandCode = new("circuits:circuitwand");
        private static readonly AssetLocation WrenchCode = new("circuits:circuitwrench");

        public bool HandleManualToggle(IPlayer byPlayer)
        {
            var heldCode = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack?.Collectible?.Code;
            if (heldCode != null && (heldCode.Equals(WandCode) || heldCode.Equals(WrenchCode)))
                return false;

            if (Api.Side == EnumAppSide.Server && IsLinked())
                return false;

            var requiredStack = GetItemSlotStack("keyItem");
            if (requiredStack == null)
                return true;

            var held = byPlayer?.InventoryManager?.ActiveHotbarSlot?.Itemstack;
            bool match = held != null && requiredStack.Equals(Api.World, held);

            if (!match)
            {
                if (byPlayer is IServerPlayer sp)
                {
                    string type = requiredStack.Class == EnumItemClass.Block ? "block" : "item";
                    string code = requiredStack.Collectible.Code.ToString();
                    sp.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup, $"Requires: <itemstack floattype=\"none\" type=\"{type}\" code=\"{code}\" rsize=\"1.0\" offx=\"0\" offy=\"0\">{requiredStack.GetName()}</itemstack>", EnumChatType.Notification);
                }
            }

            return match;
        }

        private bool IsLinked()
        {
            if (mgr == null) return false;
            foreach (var link in mgr.GetLinks())
            {
                if (link.To.NodeID == NodeID && link.To.PortID == PortIdOpen)
                    return true;
            }
            return false;
        }

        private bool OnSetOpen(object value, PortKey from)
        {
            if (value is not bool v) return false;

            inputSignals[from] = v;
            bool open = AnyTrue(inputSignals);

            var door = Blockentity.GetBehavior<BEBehaviorDoor>();
            if (door == null) return false;

            if (door.Opened == open) return true;

            door.ToggleDoorState(null, open);
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == PortIdOpen && inputSignals.Remove(from))
                OnSetOpen(AnyTrue(inputSignals), from);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            var keyStack = GetItemSlotStack("keyItem");
            if (keyStack != null)
            {
                string type = keyStack.Class == EnumItemClass.Block ? "block" : "item";
                string code = keyStack.Collectible.Code.ToString();
                dsc.AppendLine($"Requires: {keyStack.GetName()}");
            }
        }

        [HarmonyPatch(typeof(BEBehaviorDoor), "ToggleDoorState")]
        static class Patch_DoorToggle
        {
            static bool Prefix(BEBehaviorDoor __instance, IPlayer byPlayer)
            {
                if (byPlayer == null) return true;

                var circuitbehavior = __instance.Blockentity?.GetBehavior<CircuitDoorBehavior>();
                if (circuitbehavior == null) return true;

                return circuitbehavior.HandleManualToggle(byPlayer);
            }
        }
    }
}
