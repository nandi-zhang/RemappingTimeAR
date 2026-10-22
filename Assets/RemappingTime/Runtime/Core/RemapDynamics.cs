using System;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// The time-remapping model from "Remapping Time in Augmented Reality" (UIST '26), Sec. 3.
    ///
    /// The system keeps one state variable, the temporal offset δ(t) = t − s(t): how far (in seconds)
    /// the displayed moment s(t) lags behind physical time t. It evolves as a first-order linear ODE
    ///
    ///     dδ/dt = u(t) − α(t) · δ(t)                               (paper Eq. 4)
    ///
    /// where u ≥ 0 is the <b>injection rate</b> (how fast delay is added) and α ≥ 0 is the
    /// <b>recovery gain</b> (how strongly delay is pulled back to zero). The resulting playback rate of
    /// the displayed world is
    ///
    ///     r(t) = ds/dt = 1 − u(t) + α(t) · δ(t)                    (paper Eq. 5)
    ///
    /// so r = 1 is real time, r &lt; 1 is slow motion, r = 0 is a freeze, r &gt; 1 is fast-forward
    /// (catching up), and r &lt; 0 (u &gt; 1 + αδ) is rewind.
    ///
    /// <para><b>Integration.</b> The paper describes forward-Euler integration (Eq. 6). Here we use the
    /// exact solution of the ODE for inputs held constant over one step. It matches Euler when α·dt is
    /// small, and it stays stable for any α and dt. That matters because designers can set large α
    /// (snappy catch-up), and frame hitches can produce a large dt.</para>
    ///
    /// <para><b>Bounds.</b> δ is clamped to [<see cref="MinDelay"/>, <see cref="MaxDelay"/>]. The lower bound
    /// is 0 because frames from the future do not exist in a capture buffer (forward sampling needs
    /// a separate source of future frames; see paper Sec. 6.2). The upper bound is the buffer budget B.</para>
    /// </summary>
    public sealed class RemapDynamics
    {
        /// <summary>Current temporal offset δ in seconds (0 = live).</summary>
        public double Delay { get; private set; }

        /// <summary>Lower clamp for δ. Keep at 0 unless you provide future frames.</summary>
        public double MinDelay { get; set; } = 0.0;

        /// <summary>Upper clamp for δ, normally the frame buffer budget B (seconds).</summary>
        public double MaxDelay { get; set; } = 2.0;

        public void Reset(double delay = 0.0)
        {
            Delay = Clamp(delay);
        }

        /// <summary>
        /// Advances δ by <paramref name="dt"/> seconds with injection <paramref name="u"/> and recovery
        /// <paramref name="alpha"/> held constant over the step. Returns the new δ.
        /// </summary>
        public double Step(double dt, float u, float alpha)
        {
            if (dt <= 0.0) return Delay;

            double d = Delay;
            if (alpha > 1e-6f)
            {
                // Exact solution: δ(t+dt) = δ* + (δ − δ*) · e^(−α dt), with steady state δ* = u/α.
                double steady = u / alpha;
                d = steady + (d - steady) * Math.Exp(-alpha * dt);
            }
            else
            {
                // No recovery: delay integrates linearly.
                d += u * dt;
            }

            Delay = Clamp(d);
            return Delay;
        }

        /// <summary>Jumps δ to a value immediately (e.g. to start an instant replay).</summary>
        public void ForceDelay(double delay) => Delay = Clamp(delay);

        private double Clamp(double d) => Math.Max(MinDelay, Math.Min(MaxDelay, d));

        // ───────────────────────────────────────────────────────────────────
        // Helpers for translating designer intent into (u, α).
        // These are what the in-headset and web tuners use so that people can
        // think in "playback speed" and "seconds to catch up" not raw gains.
        // ───────────────────────────────────────────────────────────────────

        /// <summary>Instantaneous playback rate r = 1 − u + αδ (paper Eq. 5).</summary>
        public static float PlaybackRate(float u, float alpha, double delay) => (float)(1.0 - u + alpha * delay);

        /// <summary>
        /// Injection rate that produces playback rate <paramref name="rate"/> while δ is small or α ≈ 0:
        /// u = 1 − rate. For example rate 0.2 (5× slow motion) gives u = 0.8, the value used in the study.
        /// Values of rate below 0 produce u &gt; 1, which is rewind.
        /// </summary>
        public static float InjectionForRate(float rate) => 1f - rate;

        /// <summary>
        /// Recovery gain α that removes 95% of the accumulated offset in <paramref name="seconds"/>
        /// (δ decays as e^(−αt), and e^(−3) ≈ 0.05, so α ≈ 3 / T). The study's α = 8 corresponds to ≈ 0.375 s.
        /// </summary>
        public static float AlphaForSettleTime(float seconds) => 3f / Mathf.Max(1e-3f, seconds);

        /// <summary>Inverse of <see cref="AlphaForSettleTime"/>.</summary>
        public static float SettleTimeForAlpha(float alpha) => 3f / Mathf.Max(1e-3f, alpha);

        /// <summary>
        /// Steady-state offset δ* = u/α reached under constant inputs. Note that at the steady state
        /// r = 1: constant (u, α) produce a constant <i>lag</i>, not lasting slow motion. Slow motion is a
        /// transient, so useful remappings switch u and α in response to events.
        /// </summary>
        public static float SteadyStateDelay(float u, float alpha) => alpha > 1e-6f ? u / alpha : float.PositiveInfinity;

        /// <summary>
        /// How long slow motion at <paramref name="rate"/> can be sustained before δ reaches the buffer budget
        /// (ignoring recovery): B / (1 − rate).
        /// </summary>
        public static float MaxSlowMotionDuration(float rate, float budgetSeconds)
            => rate < 1f ? budgetSeconds / (1f - rate) : float.PositiveInfinity;
    }
}
