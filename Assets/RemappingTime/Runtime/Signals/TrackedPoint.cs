using System;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// The body points that motion-driven policies react to. Each side (left, right) is an index
    /// fingertip when hand tracking sees it, otherwise that side's Touch controller. Each side has its
    /// own smoothed velocity and an "in view" flag (inside the passthrough cameras' image).
    ///
    /// <para>With <b>Both hands</b> (the default), <see cref="Sample"/> is whichever side is moving fastest
    /// while in view, so either hand can trigger an effect. Switching between sides uses hysteresis, so things
    /// that follow the sample (like the time lens) do not jitter between two resting hands.</para>
    ///
    /// <para>Practical note: optical hand tracking can drop out during very fast motion (slashes,
    /// punches); a held controller tracks those far more reliably.</para>
    ///
    /// <para>Optionally shows a small marker at each point's <i>real-time</i> position, which makes the gap
    /// between the actual and the displayed (remapped) hand visible, like the white dot in Fig. 7 of the paper.</para>
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class TrackedPoint : MonoBehaviour
    {
        public enum Source { BothHands, RightHand, LeftHand, RightController, LeftController }

        [SerializeField] private OVRCameraRig rig;
        [SerializeField] private OVRHand rightHand;
        [SerializeField] private OVRSkeleton rightSkeleton;
        [SerializeField] private OVRHand leftHand;
        [SerializeField] private OVRSkeleton leftSkeleton;
        [SerializeField] private Shader markerShader;

        public readonly TunableSet Params = new TunableSet("Tracking");
        private Tunable _source, _smoothing, _marker, _viewMargin;

        /// <summary>The point policies react to (see class remarks for how it is chosen).</summary>
        public MotionSample Sample { get; private set; }
        public MotionSample LeftSample => _left.Sample;
        public MotionSample RightSample => _right.Sample;
        /// <summary>Device currently tracked on each side ("left hand", "left controller", "none").</summary>
        public string LeftDevice => _left.Device;
        public string RightDevice => _right.Device;
        /// <summary>Which device currently provides <see cref="Sample"/> ("right hand", "left controller", …).</summary>
        public string ActiveSourceName { get; private set; } = "none";

        /// <summary>Inset from the camera image edge used for the "in view" test (fraction of the image).</summary>
        public float ViewMargin => _viewMargin.Value;

        /// <summary>
        /// Decides whether a world point is "in view". Set by <see cref="TimeRemapEngine"/> to test against the
        /// passthrough cameras; when null, every tracked point counts as in view.
        /// </summary>
        public Func<Vector3, bool> InViewTest { get; set; }

        /// <summary>Per-side tracking state with its own velocity filter.</summary>
        private sealed class Side
        {
            public readonly string Label;
            public Transform TipCache;
            public Vector3 PrevPos;
            public bool HasPrev;
            public Vector3 Velocity;
            public MotionSample Sample;
            public string Device = "none";
            public Transform Marker;
            public Side(string label) { Label = label; }
        }

        private readonly Side _left = new Side("left"), _right = new Side("right");
        private Side _chosen;

        private void Awake()
        {
            _source = Params.Add(Tunable.Choice("tracking.source", "Tracked point",
                new[] { "Both hands", "Right hand", "Left hand", "Right controller", "Left controller" }, 0,
                "What motion policies follow. 'Both hands' lets either hand trigger effects (the faster one in view wins). " +
                "Each hand falls back to its controller when held. Controllers track fast motion more reliably."));
            _smoothing = Params.Add(Tunable.Range("tracking.smoothing", "Speed smoothing", 0.005f, 0.2f, 0.04f, 0.005f, "s",
                "Time constant of the velocity filter. Lower reacts faster but is noisier."));
            _marker = Params.Add(Tunable.Toggle("tracking.marker", "Show real-time marker", false,
                "Draw a dot at the actual (not remapped) position of each tracked point."));
            _viewMargin = Params.Add(Tunable.Range("tracking.viewMargin", "In-view margin", 0f, 0.3f, 0.08f, 0.01f, "",
                "How far inside the camera image edge a point must be to count as 'in view' (fraction of the image). " +
                "Keeps triggers away from the feathered border."));

            if (rig == null) rig = FindFirstObjectByType<OVRCameraRig>();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            var source = (Source)_source.Index;

            bool rightOn = false, leftOn = false;
            Vector3 rightPos = default, leftPos = default;
            string rightDev = "none", leftDev = "none";

            switch (source)
            {
                case Source.RightHand:
                    rightOn = TryHandTip(rightHand, rightSkeleton, _right, out rightPos); rightDev = "right hand"; break;
                case Source.LeftHand:
                    leftOn = TryHandTip(leftHand, leftSkeleton, _left, out leftPos); leftDev = "left hand"; break;
                case Source.RightController:
                    rightOn = TryController(OVRInput.Controller.RTouch, out rightPos); rightDev = "right controller"; break;
                case Source.LeftController:
                    leftOn = TryController(OVRInput.Controller.LTouch, out leftPos); leftDev = "left controller"; break;
                default: // BothHands
                    rightOn = TrySide(rightHand, rightSkeleton, OVRInput.Controller.RTouch, _right, "right", out rightPos, out rightDev);
                    leftOn = TrySide(leftHand, leftSkeleton, OVRInput.Controller.LTouch, _left, "left", out leftPos, out leftDev);
                    break;
            }

            UpdateSide(_right, rightOn, rightPos, rightDev, dt);
            UpdateSide(_left, leftOn, leftPos, leftDev, dt);
            Choose();

            ActiveSourceName = _chosen != null && _chosen.Sample.Tracked ? _chosen.Device : "none";
            Sample = _chosen != null ? _chosen.Sample : default;

            UpdateMarker(_right);
            UpdateMarker(_left);
        }

        /// <summary>Picks the side policies follow: prefer in-view, then faster, with hysteresis against flicker.</summary>
        private void Choose()
        {
            Side best = AtLeastAsGood(_right, _left) ? _right : _left;
            if (_chosen == null || !_chosen.Sample.Tracked || best == _chosen) { _chosen = best; return; }

            // Only switch when the other side is clearly better: it just came into view, or it is noticeably faster.
            bool newlyInView = best.Sample.InView && !_chosen.Sample.InView;
            bool clearlyFaster = best.Sample.Speed > _chosen.Sample.Speed * 1.3f + 0.1f;
            if (newlyInView || clearlyFaster) _chosen = best;
        }

        /// <summary>Ranking: tracked beats untracked, in view beats out of view, then faster wins.</summary>
        private static bool AtLeastAsGood(Side a, Side b)
        {
            if (a.Sample.Tracked != b.Sample.Tracked) return a.Sample.Tracked;
            if (a.Sample.InView != b.Sample.InView) return a.Sample.InView;
            return a.Sample.Speed >= b.Sample.Speed;
        }

        private bool TrySide(OVRHand hand, OVRSkeleton skeleton, OVRInput.Controller controller, Side side, string label,
                             out Vector3 pos, out string device)
        {
            if (TryHandTip(hand, skeleton, side, out pos)) { device = label + " hand"; return true; }
            if (TryController(controller, out pos)) { device = label + " controller"; return true; }
            device = "none";
            return false;
        }

        private void UpdateSide(Side s, bool tracked, Vector3 pos, string device, float dt)
        {
            if (tracked && dt > 1e-5f)
            {
                // Restart the filter if the device changed (hand ↔ controller), so we don't see a fake jump.
                if (s.HasPrev && device == s.Device)
                {
                    Vector3 raw = (pos - s.PrevPos) / dt;
                    float k = 1f - Mathf.Exp(-dt / Mathf.Max(1e-3f, _smoothing.Value));
                    s.Velocity = Vector3.Lerp(s.Velocity, raw, k);
                }
                s.PrevPos = pos;
                s.HasPrev = true;
            }
            else
            {
                // Lost tracking: let the speed decay instead of freezing at the last (possibly high) value.
                s.Velocity = Vector3.Lerp(s.Velocity, Vector3.zero, 1f - Mathf.Exp(-dt / 0.05f));
                s.HasPrev = false;
            }

            s.Device = tracked ? device : "none";
            s.Sample = new MotionSample
            {
                Tracked = tracked,
                Position = tracked ? pos : s.PrevPos,
                Velocity = s.Velocity,
                Speed = s.Velocity.magnitude,
                InView = tracked && (InViewTest == null || InViewTest(pos)),
            };
        }

        private static bool TryHandTip(OVRHand hand, OVRSkeleton skeleton, Side side, out Vector3 pos)
        {
            pos = default;
            if (!SkeletonUtil.IsTracked(hand, skeleton)) return false;
            if (side.TipCache == null) side.TipCache = SkeletonUtil.IndexTip(skeleton);
            if (side.TipCache == null) return false;
            pos = side.TipCache.position;
            return true;
        }

        private bool TryController(OVRInput.Controller controller, out Vector3 pos)
        {
            pos = default;
            if (!OVRInput.IsControllerConnected(controller) || !OVRInput.GetControllerPositionTracked(controller)) return false;
            Vector3 local = OVRInput.GetLocalControllerPosition(controller);
            Transform space = rig != null ? rig.trackingSpace : null;
            pos = space != null ? space.TransformPoint(local) : local;
            return true;
        }

        private void UpdateMarker(Side s)
        {
            bool show = _marker.On && s.Sample.Tracked;
            if (show && s.Marker == null)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"Real-time marker ({s.Label})";
                Destroy(go.GetComponent<Collider>());
                go.transform.localScale = Vector3.one * 0.012f;
                if (markerShader != null)
                    go.GetComponent<MeshRenderer>().sharedMaterial = new Material(markerShader) { color = Color.white };
                s.Marker = go.transform;
            }
            if (s.Marker != null)
            {
                s.Marker.gameObject.SetActive(show);
                if (show) s.Marker.position = s.Sample.Position;
            }
        }
    }
}
