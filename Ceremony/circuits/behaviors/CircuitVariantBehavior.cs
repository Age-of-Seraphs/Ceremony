using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace circuits
{
    public class CircuitVariantBehavior : CircuitBehavior
    {
        private const string DefaultInId = "variant.set";
        private const string DefaultOutId = "variant.state";

        private readonly Dictionary<PortKey, bool> inputSignals = new();
        private long pollTickId;
        private bool lastPolled;

        public CircuitVariantBehavior(BlockEntity be) : base(be) { }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            if (api.Side != EnumAppSide.Server) return;

            lastPolled = ReadStateVariant();
            TryEmitBool(DefaultOutId, lastPolled, out _);

            pollTickId = Blockentity.RegisterGameTickListener(OnPollTick, 100);
        }

        public override void OnBlockRemoved()
        {
            if (Api?.Side == EnumAppSide.Server && pollTickId != 0)
                Api.World.UnregisterGameTickListener(pollTickId);
            base.OnBlockRemoved();
        }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = DefaultOutId,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Variant State"
            });

            yield return new PortSpec(
                new PortDef
                {
                    PortID = DefaultInId,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Set Variant"
                },
                new global::System.Func<object, PortKey, bool>(OnSetVariant)
            );
        }

        private bool OnSetVariant(object value, PortKey from)
        {
            if (value is not bool v) return false;

            inputSignals[from] = v;
            bool on = AnyTrue(inputSignals);

            SetStateVariant(on);
            lastPolled = on;
            TryEmitBool(DefaultOutId, on, out _);
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == DefaultInId && inputSignals.Remove(from))
                OnSetVariant(AnyTrue(inputSignals), from);
        }

        private void OnPollTick(float dt)
        {
            bool cur = ReadStateVariant();
            if (cur != lastPolled)
            {
                lastPolled = cur;
                TryEmitBool(DefaultOutId, cur, out _);
            }
        }
    }
}
