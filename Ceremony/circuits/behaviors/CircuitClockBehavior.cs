using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

#nullable disable

namespace circuits
{
    public class CircuitClockBehavior : CircuitBehavior
    {
        public const string PortIdOut = "clock.out";
        public const string PortIdCtrl = "clock.ctrl";

        private int intervalMs;
        private int pulseMs;
        private long tickListenerId;
        private bool currentOutput;

        private float accumulatorMs;
        private float pulseRemainingMs;

        private readonly Dictionary<PortKey, bool> ctrlSignals = new();

        public CircuitClockBehavior(BlockEntity be) : base(be) { }

        protected override IEnumerable<PortSpec> BuildPorts()
        {
            yield return new PortSpec(new PortDef
            {
                PortID = PortIdOut,
                Dir = PortDir.Out,
                Type = SignalType.Bool,
                DisplayName = "Clock Pulse"
            });

            yield return new PortSpec(
                new PortDef
                {
                    PortID = PortIdCtrl,
                    Dir = PortDir.In,
                    Type = SignalType.Bool,
                    MaxInputs = int.MaxValue,
                    DisplayName = "Halt"
                },
                new global::System.Func<object, PortKey, bool>(OnCtrlSignal)
            );
        }

        protected override void ConfigureSettings(NodeSettingsDescriptor descriptor)
        {
            descriptor.Title = "Circuit Clock";
            descriptor.AddNumber("intervalMs", "Interval (ms)", 1000, min: 50, max: 3600000);
            descriptor.AddNumber("pulseMs", "Pulse (ms)", 1000, min: 50, max: 3600000);
        }

        protected override Dictionary<string, object> GetCurrentSettings() => new()
        {
            ["intervalMs"] = intervalMs,
            ["pulseMs"] = pulseMs
        };

        protected override void ApplySettings(Dictionary<string, string> values)
        {
            if (values.TryGetValue("intervalMs", out var iv) && int.TryParse(iv, out int newInterval))
                intervalMs = Math.Max(50, newInterval);

            if (values.TryGetValue("pulseMs", out var pv) && int.TryParse(pv, out int newPulse))
                pulseMs = Math.Max(50, newPulse);

            Blockentity.MarkDirty(true);
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            int defaultInterval = properties?["intervalMs"].AsInt(1000) ?? 1000;
            int defaultPulse = properties?["pulseMs"].AsInt(defaultInterval) ?? defaultInterval;

            if (intervalMs <= 0) intervalMs = defaultInterval;
            if (pulseMs <= 0) pulseMs = defaultPulse;
            if (intervalMs < 50) intervalMs = 50;
            if (pulseMs < 50) pulseMs = 50;

            if (api.Side != EnumAppSide.Server) return;

            if (currentOutput)
            {
                SetStateVariant(true);
                TryEmitBool(PortIdOut, true, out _);
            }
            else
            {
                TryEmitBool(PortIdOut, false, out _);
            }

            tickListenerId = Blockentity.RegisterGameTickListener(OnTick, 50);
        }

        public override void OnBlockRemoved()
        {
            CleanupServerListeners();
            base.OnBlockRemoved();
        }

        public override void OnBlockUnloaded()
        {
            CleanupServerListeners();
            base.OnBlockUnloaded();
        }

        private bool OnCtrlSignal(object value, PortKey from)
        {
            if (value is not bool v) return false;
            ctrlSignals[from] = v;
            EvaluateEnabled();
            return true;
        }

        public override void OnSourceDisconnected(string inPortId, PortKey from)
        {
            if (inPortId == PortIdCtrl && ctrlSignals.Remove(from))
                EvaluateEnabled();
        }

        private bool IsEnabled()
        {
            // A HIGH ctrl signal halts the clock; no ctrl (or all LOW) = running
            return ctrlSignals.Count == 0 || !AnyTrue(ctrlSignals);
        }

        private void EvaluateEnabled()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            // When the clock is halted we keep the current output as-is.
            // The tick loop already checks IsEnabled() so it will stop pulsing.
            Blockentity.MarkDirty(true);
        }

        private void CleanupServerListeners()
        {
            if (Api?.Side != EnumAppSide.Server) return;
            if (tickListenerId != 0)
            {
                Api.World.UnregisterGameTickListener(tickListenerId);
                tickListenerId = 0;
            }
        }

        private void OnTick(float dt)
        {
            if (!IsEnabled()) return;

            float dtMs = dt * 1000f;

            if (currentOutput)
            {
                pulseRemainingMs -= dtMs;
                if (pulseRemainingMs <= 0) EndPulse();
            }
            else
            {
                accumulatorMs += dtMs;
                if (accumulatorMs >= intervalMs)
                {
                    accumulatorMs = 0;
                    StartPulse();
                }
            }
        }

        private void StartPulse()
        {
            currentOutput = true;
            pulseRemainingMs = pulseMs;
            SetStateVariant(true);
            TryEmitBool(PortIdOut, true, out _);
            Blockentity.MarkDirty(true);
        }

        private void EndPulse()
        {
            currentOutput = false;
            pulseRemainingMs = 0;
            SetStateVariant(false);
            TryEmitBool(PortIdOut, false, out _);
            Blockentity.MarkDirty(true);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            base.GetBlockInfo(forPlayer, dsc);
            if (ctrlSignals.Count > 0)
                dsc.AppendLine(AnyTrue(ctrlSignals) ? "Control: Halted" : "Control: Running");
            dsc.AppendLine($"Interval: {intervalMs}ms  Pulse: {pulseMs}ms");
            dsc.AppendLine(currentOutput ? "State: ON" : "State: OFF");
        }

        public override void WriteSettingsToTree(ITreeAttribute tree)
        {
            base.WriteSettingsToTree(tree);
            tree.SetInt("clock.intervalMs", intervalMs);
            tree.SetInt("clock.pulseMs", pulseMs);
        }

        public override void ReadSettingsFromTree(ITreeAttribute tree)
        {
            base.ReadSettingsFromTree(tree);
            int iv = tree.GetInt("clock.intervalMs", 0);
            if (iv > 0) intervalMs = iv;
            int pv = tree.GetInt("clock.pulseMs", 0);
            if (pv > 0) pulseMs = pv;
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);
            currentOutput = tree.GetBool("clock.output");
            accumulatorMs = tree.GetFloat("clock.accumulatorMs");
            pulseRemainingMs = tree.GetFloat("clock.pulseRemainingMs");
            intervalMs = tree.GetInt("clock.intervalMs", 0);
            pulseMs = tree.GetInt("clock.pulseMs", 0);
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetBool("clock.output", currentOutput);
            tree.SetFloat("clock.accumulatorMs", accumulatorMs);
            tree.SetFloat("clock.pulseRemainingMs", pulseRemainingMs);
            tree.SetInt("clock.intervalMs", intervalMs);
            tree.SetInt("clock.pulseMs", pulseMs);
        }
    }
}
