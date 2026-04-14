using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace circuits
{
    public class CircuitSRLatchBehavior : CircuitBehavior
    {
        public const string PortIdData = "latch.in";
        public const string PortIdCtrl = "latch.ctrl";
        public const string PortIdOut = "latch.out";

        private const int DelayMs = 250;

        private readonly Dictionary<PortKey, bool> dataSignals = new();
        private readonly Dictionary<PortKey, bool> ctrlSignals = new();

        private bool locked;
        private bool outputState;
        private long pendingCallbackId;
        private bool pendingValue;
        private bool pendingOn;
        private long pendingStartMs;

        public CircuitSRLatchBehavior(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Latch Output"
            });

            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdData,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Data Input"
                },
                new global::System.Func<object, PortKey, bool>(OnDataInput)
            );

            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdCtrl,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Lock Control"
                },
                new global::System.Func<object, PortKey, bool>(OnCtrlInput)
            );
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            if (api.Side != EnumAppSide.Server) return;

            locked = ReadLockVariant();
            outputState = ReadStateVariant();

            TryEmitBool(PortIdOut, outputState, out _);

            if (pendingOn)
            {
                long elapsed = api.World.ElapsedMilliseconds - pendingStartMs;
                int remaining = System.Math.Max(1, DelayMs - (int)elapsed);
                SchedulePending(remaining);
            }
        }

        public override void OnBlockRemoved()
        {
            if (Api?.Side == EnumAppSide.Server)
                CancelPending();
            base.OnBlockRemoved();
        }

        private bool ReadLockVariant()
        {
            var blk = Api.World.BlockAccessor.GetBlock(Pos);
            return blk?.Variant?["latch"] == "locked";
        }

        private bool OnDataInput(object value, PortKey from)
        {
            if (value is not bool v) return false;

            dataSignals[from] = v;
            bool effectiveData = AnyTrue(dataSignals);

            if (locked) return true;

            if (effectiveData == outputState)
            {
                CancelPending();
                return true;
            }

            CancelPending();
            pendingValue = effectiveData;
            pendingOn = true;
            pendingStartMs = Api.World.ElapsedMilliseconds;
            Blockentity.MarkDirty(true);
            SchedulePending(DelayMs);
            return true;
        }

        private bool OnCtrlInput(object value, PortKey from)
        {
            if (value is not bool v) return false;

            ctrlSignals[from] = v;
            bool effectiveLock = AnyTrue(ctrlSignals);

            if (effectiveLock != locked)
            {
                locked = effectiveLock;
                SetVariants(locked, outputState);

                if (!locked)
                {
                    bool effectiveData = AnyTrue(dataSignals);
                    if (effectiveData != outputState)
                    {
                        CancelPending();
                        pendingValue = effectiveData;
                        pendingOn = true;
                        pendingStartMs = Api.World.ElapsedMilliseconds;
                        Blockentity.MarkDirty(true);
                        SchedulePending(DelayMs);
                    }
                }
                else
                {
                    CancelPending();
                }
            }

            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == PortIdData && dataSignals.Remove(from))
                OnDataInput(AnyTrue(dataSignals), from);
            else if (inPortId == PortIdCtrl && ctrlSignals.Remove(from))
                OnCtrlInput(AnyTrue(ctrlSignals), from);
        }

        private void SchedulePending(int ms)
        {
            pendingCallbackId = Api.World.RegisterCallback(_ =>
            {
                pendingCallbackId = 0;
                pendingOn = false;
                SetOutput(pendingValue);
                Blockentity.MarkDirty(true);
            }, ms);
        }

        private void CancelPending()
        {
            if (pendingCallbackId != 0)
            {
                Api.World.UnregisterCallback(pendingCallbackId);
                pendingCallbackId = 0;
            }
            if (pendingOn)
            {
                pendingOn = false;
                Blockentity.MarkDirty(true);
            }
        }

        private void SetOutput(bool on)
        {
            outputState = on;
            SetVariants(locked, on);
            TryEmitBool(PortIdOut, on, out _);
        }

        private void SetVariants(bool lockState, bool onState)
        {
            var ba = Api.World.BlockAccessor;
            var cur = ba.GetBlock(Pos);
            if (cur == null) return;

            var code1 = cur.CodeWithVariant("latch", lockState ? "locked" : "free");
            var block1 = ba.GetBlock(code1);
            if (block1 == null) return;

            var code2 = block1.CodeWithVariant("state", onState ? "on" : "off");
            var block2 = ba.GetBlock(code2);
            if (block2 == null) return;

            if (block2.BlockId == cur.BlockId) return;

            ba.ExchangeBlock(block2.BlockId, Pos);
            ba.MarkBlockDirty(Pos);
            ReRegisterNode();
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);

            dsc.AppendLine(locked ? "Lock: LOCKED" : "Lock: FREE");

            if (pendingOn)
            {
                long elapsed = Api.World.ElapsedMilliseconds - pendingStartMs;
                long remaining = System.Math.Max(0, DelayMs - elapsed);
                dsc.AppendLine($"State: Pending ({remaining}ms)");
            }
            else
            {
                dsc.AppendLine(outputState ? "State: ON" : "State: OFF");
            }
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            pendingOn = tree.GetBool("latch.pending");
            pendingStartMs = tree.GetLong("latch.pendingStartMs");
            pendingValue = tree.GetBool("latch.pendingValue");
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("latch.pending", pendingOn);
            tree.SetLong("latch.pendingStartMs", pendingStartMs);
            tree.SetBool("latch.pendingValue", pendingValue);
        }
    }
}
