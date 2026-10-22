using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// One independent "read head" into the shared frame buffer: a policy, its own ODE state δ(t),
    /// and the resulting displayed time s(t) = t − δ(t).
    ///
    /// The engine's main view uses one channel. Because channels only read the buffer, any number
    /// can run side by side at almost no cost. <see cref="TimelineGallery"/> uses this to show several
    /// remapping functions applied to the same live scene at once.
    /// </summary>
    public sealed class RemapChannel
    {
        public readonly string Name;
        public readonly RemapDynamics Dynamics = new RemapDynamics();
        public readonly TelemetryHistory History = new TelemetryHistory(1200);

        public RemapPolicy Policy { get; private set; }

        /// <summary>Displayed source time s(t), in <see cref="CameraClock"/> seconds.</summary>
        public double DisplayTime { get; private set; }
        public double Delay => Dynamics.Delay;
        /// <summary>Injection and recovery actually applied this frame (after limits).</summary>
        public float U { get; private set; }
        public float Alpha { get; private set; }
        /// <summary>Measured playback rate ds/dt (smoothed). Includes the effect of clamping.</summary>
        public float Rate { get; private set; } = 1f;
        public string Phase { get; private set; } = "live";

        /// <summary>
        /// When on, injection fades out as δ approaches the buffer budget, so slow motion eases into a
        /// steady lag rather than hitting a hard wall. Without this, holding slow motion past
        /// B / (1 − speed) seconds silently turns into plain delay.
        /// </summary>
        public bool SoftBudget = true;

        private bool _snapping;
        private bool _hasPrev;
        private double _prevDisplayTime;

        public RemapChannel(string name, RemapPolicy policy)
        {
            Name = name;
            SetPolicy(policy);
        }

        public void SetPolicy(RemapPolicy policy)
        {
            Policy = policy ?? new FunctionPolicy();
            Policy.Reset();
            // The current delay is kept on purpose, so the new policy (or its recovery) takes over smoothly.
        }

        /// <param name="ctx">Shared context for this frame; <see cref="RemapContext.Delay"/> is filled in here.</param>
        /// <param name="bypass">Force live (used for quick A/B comparison against real time).</param>
        public void Step(RemapContext ctx, bool bypass = false)
        {
            Dynamics.MaxDelay = Mathf.Max(0f, ctx.MaxDelay);
            ctx.Delay = Dynamics.Delay;

            if (ctx.Inputs.SnapToLive) _snapping = true;

            // Always evaluate the policy so its timers and state machines keep running during overrides.
            RemapCommand cmd = Policy.Evaluate(ctx);

            if (bypass || _snapping)
            {
                cmd = new RemapCommand(0f, RemapDynamics.AlphaForSettleTime(0.2f), bypass ? "bypass" : "snap to live");
                if (_snapping && Dynamics.Delay < 0.005) _snapping = false;
            }

            // u up to 5 allows rewind at up to 4×; α up to 100 is an effectively instant catch-up.
            float u = Mathf.Clamp(float.IsNaN(cmd.U) ? 0f : cmd.U, 0f, 5f);
            float a = Mathf.Clamp(float.IsNaN(cmd.Alpha) ? 0f : cmd.Alpha, 0f, 100f);
            if (SoftBudget) u = ApplySoftBudget(u, a, Dynamics.Delay, Dynamics.MaxDelay);

            Dynamics.Step(ctx.Dt, u, a);
            DisplayTime = ctx.Time - Dynamics.Delay;

            if (_hasPrev && ctx.Dt > 1e-5f)
            {
                float instantaneous = (float)((DisplayTime - _prevDisplayTime) / ctx.Dt);
                float k = 1f - Mathf.Exp(-ctx.Dt / 0.06f);
                Rate = Mathf.Lerp(Rate, instantaneous, k);
            }
            _prevDisplayTime = DisplayTime;
            _hasPrev = true;

            U = u;
            Alpha = a;
            Phase = cmd.Phase ?? "";
            History.Add((float)ctx.Time, (float)Dynamics.Delay, Rate, u, a);
        }

        private static float ApplySoftBudget(float u, float alpha, double delay, double maxDelay)
        {
            float growth = alpha * (float)delay;      // u above this makes δ grow
            if (u <= growth || maxDelay <= 1e-3) return u;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp((float)maxDelay * 0.75f, (float)maxDelay, (float)delay));
            // Near the limit, hold δ constant: r = 1 for slow motion, and r = 0 (pause) when rewinding.
            float hold = u > 1f ? Mathf.Max(1f, growth) : growth;
            return Mathf.Lerp(u, hold, k);
        }
    }

    /// <summary>Fixed-size ring of recent telemetry samples, used for the live graphs.</summary>
    public sealed class TelemetryHistory
    {
        public readonly float[] Time, Delay, Rate, U, Alpha;
        private int _head, _count;

        public TelemetryHistory(int capacity)
        {
            Time = new float[capacity]; Delay = new float[capacity]; Rate = new float[capacity];
            U = new float[capacity]; Alpha = new float[capacity];
        }

        public int Count => _count;
        public int Capacity => Time.Length;

        public void Add(float t, float delay, float rate, float u, float alpha)
        {
            Time[_head] = t; Delay[_head] = delay; Rate[_head] = rate; U[_head] = u; Alpha[_head] = alpha;
            _head = (_head + 1) % Time.Length;
            _count = Mathf.Min(_count + 1, Time.Length);
        }

        /// <summary>Physical index of the i-th sample, 0 = oldest.</summary>
        public int IndexOf(int i) => (_head - _count + i + Time.Length * 2) % Time.Length;

        public float LatestTime => _count == 0 ? 0f : Time[IndexOf(_count - 1)];
    }
}
