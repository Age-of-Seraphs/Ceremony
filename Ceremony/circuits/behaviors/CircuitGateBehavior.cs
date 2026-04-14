using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace circuits
{
    public class CircuitGateBehavior : CircuitBehavior
    {
        public const string PortIdIn = "gate.in";
        public const string PortIdOut = "gate.out";

        private readonly Dictionary<PortKey, bool> inputSignals = new();

        public CircuitGateBehavior(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Out"
            });

            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdIn,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Input"
                },
                (val, from) => OnSetInput(val, from),
                requireTrueForBool: false
            );
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            if (api.Side == EnumAppSide.Server) Emit();
        }

        private bool OnSetInput(object value, PortKey from)
        {
            if (value is not bool v) return false;
            inputSignals[from] = v;
            Emit();
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == PortIdIn && inputSignals.Remove(from))
                Emit();
        }

        private void Emit()
        {
            if (Api.Side != EnumAppSide.Server) return;
            bool result = EvalGate(GetGateType(), inputSignals);
            TryEmitBool(PortIdOut, result, out _);
        }

        private string GetGateType()
        {
            var blk = Api.World.BlockAccessor.GetBlock(Pos);
            if (blk?.Variant == null) return "and";
            return blk.Variant.TryGetValue("type", out var t) && t != null ? t.ToLowerInvariant() : "and";
        }

        private static bool EvalGate(string type, Dictionary<PortKey, bool> signals) => type switch
        {
            "and" => signals.Count == 0 || signals.Values.All(v => v),
            "or" => signals.Values.Any(v => v),
            "xor" => signals.Count > 0 && signals.Values.Count(v => v) % 2 == 1,
            "not" => !signals.Values.Any(v => v),
            _ => signals.Count == 0 || signals.Values.All(v => v)
        };
    }
}
