using Vintagestory.API.Common;

#nullable disable

namespace circuits
{
    /// <summary>
    /// Animated lever. Click toggles state 0.
    /// The circuit value only commits when the animation reaches its endpoint.
    /// Animation states are defined entirely in JSON.
    /// </summary>
    public class CircuitLeverBehavior : CircuitBehavior
    {
        public CircuitLeverBehavior(BlockEntity be) : base(be) { }

        protected override System.Collections.Generic.IEnumerable<PortSpec> BuildPorts()
        {
            // Animation output ports are auto-registered by the base class
            // from the "states" JSON array. No additional ports needed.
            yield break;
        }

        protected override bool OnInteract(
            IWorldAccessor world, IPlayer byPlayer,
            BlockSelection blockSel, ref EnumHandling handling)
        {
            if (Api.Side != EnumAppSide.Server)
            {
                handling = EnumHandling.PreventDefault;
                return true;
            }

            if (HasAnimation && AnimStates.Length > 0 && !AnimStates[0].IsAnimating)
            {
                BeginTransition(0, !AnimStates[0].AnimTarget);

                Api.World.PlaySoundAt(
                    new AssetLocation("game:sounds/effect/woodswitch"),
                    Pos.X, Pos.Y, Pos.Z,
                    null, randomizePitch: true, 16f);
            }

            handling = EnumHandling.PreventDefault;
            return true;
        }
    }
}
