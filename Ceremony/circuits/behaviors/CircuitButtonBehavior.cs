using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace circuits
{
    /// <summary>
    /// Animated momentary button. Click turns on (fast forward animation), then
    /// automatically turns off with a slow reverse animation whose duration matches
    /// <c>resetDelayMs</c>.
    /// Optional <c>toggleMode</c>: first click fully depresses, next click fully releases
    /// at the same speed as the forward animation — no auto-reverse, no delay.
    /// </summary>
    public class CircuitButtonBehavior : CircuitBehavior
    {
        private int resetDelayMs;
        private bool toggleMode;
        private float offFps;

        public CircuitButtonBehavior(BlockEntity be) : base(be) { }

        protected override System.Collections.Generic.IEnumerable<PortSpec> BuildPorts()
        {
            yield break;
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor descriptor)
        {
            descriptor.Title = "Button Settings";
            descriptor.AddNumber("resetDelayMs", "Hold Duration (ms)", 1000, min: 100, max: 3600000);
            descriptor.AddToggle("toggleMode", "Toggle Mode", false);
        }

        protected override System.Collections.Generic.Dictionary<string, object> GetCurrentSettings() => new()
        {
            ["resetDelayMs"] = resetDelayMs,
            ["toggleMode"] = toggleMode
        };

        protected override void ApplySettings(System.Collections.Generic.Dictionary<string, string> values)
        {
            if (values.TryGetValue("resetDelayMs", out var s) && int.TryParse(s, out int v))
            {
                resetDelayMs = Math.Max(100, v);
                RecalcOffFps();
            }
            if (values.TryGetValue("toggleMode", out var tm))
                toggleMode = bool.TryParse(tm, out bool b) && b;

            Blockentity.MarkDirty(true);
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            if (resetDelayMs <= 0)
                resetDelayMs = properties?["resetDelayMs"].AsInt(1000) ?? 1000;

            RecalcOffFps();
        }

        private void RecalcOffFps()
        {
            if (HasAnimation && AnimStates.Length > 0)
            {
                int frames = AnimStates[0].Def.OffFrames;
                if (frames <= 0) frames = AnimStates[0].Def.OnFrames;
                if (frames <= 0) frames = 20;

                float seconds = resetDelayMs / 1000f;
                offFps = Math.Max(0.5f, frames / Math.Max(0.1f, seconds));
            }
            else
            {
                offFps = 30f;
            }
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
                if (toggleMode)
                    BeginTransition(0, !AnimStates[0].AnimTarget);
                else if (!AnimStates[0].AnimTarget)
                    BeginTransition(0, true);
            }

            handling = EnumHandling.PreventDefault;
            return true;
        }

        protected override float GetPlaybackFps(AnimStateRuntime s, bool targetValue)
        {
            // In toggle mode both directions play at the forward speed
            if (toggleMode) return s.Def.AnimFps;
            // Normal mode: slow reverse driven by resetDelayMs
            if (!targetValue && resetDelayMs > 0) return offFps;
            return s.Def.AnimFps;
        }

        protected override void OnAnimStateCommitted(int index, bool value)
        {
            // Auto-reverse only in normal (non-toggle) mode
            if (index == 0 && value && !toggleMode)
                BeginTransition(0, false);
        }

        protected override string FormatAnimStateInfo(AnimStateRuntime s)
        {
            if (!toggleMode && s.Value && s.IsAnimating && !s.AnimTarget && s.TransitionDurationMs > 0)
            {
                long elapsed = Api.World.ElapsedMilliseconds - s.TransitionStartMs;
                long remaining = Math.Max(0, s.TransitionDurationMs - elapsed);
                return $"ON for {FormatDuration(remaining)}";
            }

            if (!s.Value && s.IsAnimating && s.AnimTarget)
                return "switching ON...";

            return s.Value ? "ON" : "OFF";
        }

        public override void WriteSettingsToTree(ITreeAttribute tree)
        {
            base.WriteSettingsToTree(tree);
            tree.SetInt("button.resetDelayMs", resetDelayMs);
            tree.SetBool("button.toggleMode", toggleMode);
        }

        public override void ReadSettingsFromTree(ITreeAttribute tree)
        {
            base.ReadSettingsFromTree(tree);
            int saved = tree.GetInt("button.resetDelayMs", 0);
            if (saved > 0) resetDelayMs = saved;
            toggleMode = tree.GetBool("button.toggleMode", false);
            RecalcOffFps();
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            int saved = tree.GetInt("button.resetDelayMs", 0);
            if (saved > 0) resetDelayMs = saved;
            toggleMode = tree.GetBool("button.toggleMode", false);
            RecalcOffFps(); // ensure animation speed reflects synced resetDelayMs
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetInt("button.resetDelayMs", resetDelayMs);
            tree.SetBool("button.toggleMode", toggleMode);
        }
    }
}
