using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// User-facing control inputs, merged from the headset controllers and any connected web clients.
    /// The formulas decide what these mean (e.g. "hold" slows time in the Hold to slow preset).
    /// </summary>
    public struct RemapInputs
    {
        /// <summary>Combined hold strength in [0, 1]: max of local and remote.</summary>
        public float Hold;
        /// <summary>Hold strength from the headset controller trigger only.</summary>
        public float LocalHold;
        /// <summary>Hold strength from web clients only (e.g. a phone).</summary>
        public float RemoteHold;
        /// <summary>True on the frame an "action" was requested (A button or web). Policies may use it to fire a pulse.</summary>
        public bool Action;
        /// <summary>True on the frame a "snap to live" was requested (B button or web).</summary>
        public bool SnapToLive;
    }

    /// <summary>Kinematics of the tracked point (hand or controller) that motion-driven policies react to.</summary>
    public struct MotionSample
    {
        public bool Tracked;
        public Vector3 Position;
        /// <summary>Smoothed world-space velocity (m/s).</summary>
        public Vector3 Velocity;
        /// <summary>Smoothed speed (m/s).</summary>
        public float Speed;
        /// <summary>
        /// True if the point is inside the passthrough cameras' view, i.e. inside the region where the
        /// remapped image is shown. Motion outside it would trigger effects the wearer cannot see.
        /// </summary>
        public bool InView;
    }

    /// <summary>Everything a <see cref="RemapPolicy"/> may look at when deciding u(t) and α(t).</summary>
    public struct RemapContext
    {
        /// <summary>Physical time t (camera timebase, seconds).</summary>
        public double Time;
        /// <summary>Time since the last evaluation (seconds).</summary>
        public float Dt;
        /// <summary>Current temporal offset δ (seconds).</summary>
        public double Delay;
        /// <summary>Largest δ currently allowed (buffer budget, or less while the buffer is still filling).</summary>
        public float MaxDelay;
        public RemapInputs Inputs;
        /// <summary>The point motion effects follow (fastest hand in view, see <see cref="TrackedPoint"/>).</summary>
        public MotionSample Motion;
        /// <summary>Each hand (or held controller) individually.</summary>
        public MotionSample Left, Right;
        /// <summary>Penetration depth (m) of the tracked point into the active <see cref="ZoneVolume"/>; 0 when outside or none.</summary>
        public float ZoneDepth;
        public bool InZone;
        public Pose Head;
    }

    /// <summary>A policy's output for one step: the ODE inputs plus a label for UIs and logs.</summary>
    public struct RemapCommand
    {
        public float U;
        public float Alpha;
        /// <summary>Short human-readable state, e.g. "slowing", "catching up", "live".</summary>
        public string Phase;

        public RemapCommand(float u, float alpha, string phase)
        {
            U = u; Alpha = alpha; Phase = phase;
        }
    }
}
