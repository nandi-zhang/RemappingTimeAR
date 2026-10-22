using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// Displays a <see cref="RemapChannel"/>'s remapped passthrough to both eyes, with head reprojection.
    ///
    /// <para><b>Head reprojection (canvas repositioning).</b> Each buffered frame carries the world pose of
    /// the camera that captured it. The frame is drawn on a quad shaped like that camera's frustum
    /// (true intrinsics), at the <i>capture</i> pose, at distance D. So a past frame stays where it was
    /// seen in the world while the head moves:</para>
    /// <list type="bullet">
    /// <item>Head <b>rotation</b> is compensated for the whole image, whatever the scene depth.</item>
    /// <item>Head <b>translation</b> is exact for surfaces at distance D. Elsewhere it is off by parallax
    /// proportional to (translation) × |1/D − 1/depth|, which is small for delays of a second or two.
    /// Set D to the typical interaction depth (≈0.5–0.8 m for hands on a table, 1.5–3 m for watching people).</item>
    /// <item>If the head turns far enough that the old frame leaves the view, the uncovered area shows
    /// the live system passthrough, which degrades gracefully.</item>
    /// </list>
    /// <para>Turning reprojection off reproduces the study's head-locked canvas: the image is pinned
    /// to the current head pose (which is why it only suited near-stationary viewing).</para>
    ///
    /// <para><b>Looks.</b> Edges are feathered into the live passthrough. When nothing is remapped the
    /// canvas can fade out entirely ("hide when live"), so the wearer sees the full-quality system
    /// passthrough until the effect starts. Optional echo layers add earlier frames on top to create
    /// motion trails (chronophotography). A lens scope restricts the remapping to a soft circle around
    /// the hand or at the centre of view.</para>
    /// </summary>
    [DefaultExecutionOrder(100)] // after the engine has stepped the channel
    public sealed class ReprojectedFrameView : MonoBehaviour
    {
        public const int MaxEchoes = 4;

        [SerializeField] private StereoPassthroughCapture capture;
        [SerializeField] private TrackedPoint tracking;
        [SerializeField] private Transform head;
        [SerializeField] private Shader frameShader;
        [SerializeField] private string leftEyeLayer = "LeftEye";
        [SerializeField] private string rightEyeLayer = "RightEye";
        [Tooltip("Set when the rig renders both eyes in a single pass rather than with per-eye cameras.")]
        [SerializeField] private bool singlePassStereo = false;

        /// <summary>The channel to display. Assigned by <see cref="TimeRemapEngine"/>.</summary>
        public RemapChannel Channel { get; set; }

        public readonly TunableSet Params = new TunableSet("View");
        private Tunable _reproject, _distance, _blend, _hideLive, _opacity, _feather, _cues,
                        _scope, _lensAnchor, _lensRadius, _echo, _echoCount, _echoSpacing, _echoStrength;

        private readonly FrameQuad[] _main = new FrameQuad[2];
        private readonly FrameQuad[,] _echoes = new FrameQuad[2, MaxEchoes];
        private bool _built;
        private float _presence;

        /// <summary>0..1: how visible the remapped canvas currently is (after "hide when live" fading).</summary>
        public float Presence => _presence;

        // Groups follow the design space of time remapping: how the remapped world is shown (Representation),
        // where (Scope), and how clearly the remapping is signalled (Transparency). "Display" holds rendering
        // quality settings that are not effects in themselves.
        public const string Representation = "Representation", Scope = "Scope", Transparency = "Transparency", Display = "Display";

        private void Awake()
        {
            // Representation: replace the world, blend it as a ghost, or stack past moments as trails.
            _opacity = Add(Representation, Tunable.Range("view.opacity", "Ghost opacity", 0f, 1f, 1f, 0.05f, "",
                "1 = the remapped world replaces the real one. Lower = a ghost of the past over the live world."));
            _echo = Add(Representation, Tunable.Toggle("view.echo", "Echo trails", false,
                "Stack earlier moments on top, so moving things leave trails (like a long-exposure photo)."));
            _echoCount = Add(Representation, Tunable.Range("view.echoCount", "Echo count", 1, MaxEchoes, 3, 1));
            _echoSpacing = Add(Representation, Tunable.Range("view.echoSpacing", "Echo spacing", 0.02f, 0.5f, 0.08f, 0.01f, "s",
                "Time between successive echoes."));
            _echoStrength = Add(Representation, Tunable.Range("view.echoStrength", "Echo strength", 0f, 1f, 0.45f, 0.05f));

            // Scope: the whole view, or a region.
            _scope = Add(Scope, Tunable.Choice("view.scope", "Region", new[] { "Whole view", "Lens" }, 0,
                "Whole view = everything the cameras see is remapped. Lens = only inside a soft circle."));
            _lensAnchor = Add(Scope, Tunable.Choice("view.lensAnchor", "Lens follows", new[] { "Hand", "Gaze" }, 0));
            _lensRadius = Add(Scope, Tunable.Range("view.lensRadius", "Lens size", 0.05f, 0.6f, 0.2f, 0.01f, "",
                "Radius as a fraction of the image height."));

            // Transparency: how visibly the system signals that time is being remapped.
            _cues = Add(Transparency, Tunable.Toggle("view.cues", "Show remapping cues", true,
                "On: the remapped view is tinted by playback speed (cool = slowed, warm = fast-forward, violet = rewind) " +
                "and glows at its edges as delay builds. Off: no visual hint, so the wearer must notice the remapping themselves."));

            // Display quality and registration.
            _reproject = Add(Display, Tunable.Choice("view.reprojection", "Head reprojection", new[] { "Off", "Flat plane", "Depth (experimental)" }, 1,
                "How past frames are placed as you move your head. Flat plane (default): on one plane at the projection distance, " +
                "clean but exact only at that distance. Depth (experimental): bent to the scene's measured depth (Meta Depth API), " +
                "so near and far things stay aligned as you move, but the coarse depth map breaks up fine details. " +
                "Off: the image follows your head."));
            _distance = Add(Display, Tunable.Range("view.distance", "Projection distance", 0.3f, 5f, 1.0f, 0.05f, "m",
                "Depth that is exactly registered under head motion. Match it to where the action happens."));
            // Fixed at 1 m in the UIs: in practice the difference was hard to perceive. Presets may still set it.
            _distance.Hidden = true;
            _blend = Add(Display, Tunable.Toggle("view.blend", "Frame blending", true,
                "Cross-fade neighbouring camera frames for smooth slow motion. Off = step from frame to frame."));
            _hideLive = Add(Display, Tunable.Toggle("view.hideLive", "Hide when live", true,
                "Fade the remapped view out when there is no remapping, revealing the full-quality system passthrough."));
            _feather = Add(Display, Tunable.Range("view.feather", "Edge softness", 0f, 0.3f, 0.08f, 0.01f, "",
                "How softly the remapped view blends into the live view at its edges."));

        }

        /// <summary>Strength of the speed tint and edge glow when cues are on.</summary>
        private const float CueTint = 0.45f, CueGlow = 0.35f;

        private Tunable Add(string group, Tunable t)
        {
            t.Group = group;
            return Params.Add(t);
        }

        private void Build()
        {
            int leftLayer = LayerMask.NameToLayer(leftEyeLayer);
            int rightLayer = LayerMask.NameToLayer(rightEyeLayer);
            if (!singlePassStereo && (leftLayer < 0 || rightLayer < 0))
                Debug.LogError($"[RemappingTime] Layers '{leftEyeLayer}'/'{rightEyeLayer}' are missing. Add them in Tags & Layers, " +
                               "or enable singlePassStereo on the view.");

            for (int e = 0; e < 2; e++)
            {
                var eye = (Eye)e;
                int layer = eye == Eye.Left ? leftLayer : rightLayer;
                _main[e] = CreateQuad($"Remapped {eye}", eye, layer, 1010);
                for (int k = 0; k < MaxEchoes; k++)
                    _echoes[e, k] = CreateQuad($"Echo {k + 1} {eye}", eye, layer, 1011 + k);
            }
            _built = true;
        }

        private FrameQuad CreateQuad(string name, Eye eye, int layer, int queue)
        {
            var q = new FrameQuad(name, eye, frameShader, transform, layer, queue);
            Vector3[] c = capture.LocalCorners(eye);
            // A grid rather than a quad, so depth reprojection can bend it to the scene.
            q.SetGrid(c[0], c[1], c[2], c[3], GridCols, GridRows);
            q.UseStereoEyeIndex = singlePassStereo;
            q.Visible = false;
            return q;
        }

        private void LateUpdate()
        {
            if (capture == null || !capture.IsReady || Channel == null || capture.LocalCorners(Eye.Left) == null) return;
            if (!_built) Build();

            UpdatePresence();
            bool echoOn = _echo.On;
            bool visible = _presence > 0.001f || echoOn;

            if (!capture.Buffer.TrySample(Channel.DisplayTime, out FrameSample sample) || !visible)
            {
                SetAllVisible(false);
                return;
            }

            ComputeRateCues(out Color tint, out float tintAmount, out float desat, out float glow);
            float opacity = _opacity.Value * (echoOn ? 1f : _presence);
            float aspect = capture.Aspect;
            float distance = _distance.Value;

            for (int e = 0; e < 2; e++)
            {
                var eye = (Eye)e;
                FrameQuad q = _main[e];
                Pose pose = ResolvePose(eye, sample);
                Place(q, pose, distance);
                q.SetDepth(DepthMode ? sample.Nearest : null, distance);

                if (_blend.On) q.SetFrames(sample.Older.Texture(eye), sample.Newer.Texture(eye), sample.Blend);
                else { RenderTexture n = sample.Nearest.Texture(eye); q.SetFrames(n, n, 0f); }

                q.SetLook(opacity, _feather.Value, aspect, tint, tintAmount, desat, tint, glow);
                ApplyLens(q, eye, pose);
                q.Visible = true;

                // Echo layers: earlier frames drawn on top with decreasing opacity. Static regions are
                // identical in every layer and stay unchanged; only moving things leave trails.
                int count = echoOn ? Mathf.RoundToInt(_echoCount.Value) : 0;
                for (int k = 0; k < MaxEchoes; k++)
                {
                    FrameQuad echo = _echoes[e, k];
                    if (k >= count || !capture.Buffer.TrySample(Channel.DisplayTime - (k + 1) * _echoSpacing.Value, out FrameSample es))
                    {
                        echo.Visible = false;
                        continue;
                    }
                    StereoFrame f = es.Nearest;
                    Place(echo, Reprojecting ? f.CameraPose(eye) : pose, distance);
                    echo.SetDepth(DepthMode ? f : null, distance);
                    echo.SetFrames(f.Texture(eye), f.Texture(eye), 0f);
                    float strength = _echoStrength.Value * (1f - k / (float)(count + 1));
                    echo.SetLook(strength * _opacity.Value, _feather.Value, aspect, tint, tintAmount, desat, tint, 0f);
                    ApplyLens(echo, eye, pose);
                    echo.Visible = true;
                }
            }
        }

        private Pose ResolvePose(Eye eye, FrameSample sample)
            => Reprojecting ? sample.InterpolatedPose(eye) : capture.CurrentCameraPose(eye);

        private bool Reprojecting => _reproject.Index > 0;
        private bool DepthMode => _reproject.Index == 2;
        private const int GridCols = 48, GridRows = 36;

        private static void Place(FrameQuad q, Pose pose, float distance)
        {
            // Quad vertices sit on the unit-depth image plane; scaling by D moves the plane to depth D
            // while keeping its angular size (the frustum) unchanged.
            q.Transform.SetPositionAndRotation(pose.position, pose.rotation);
            q.Transform.localScale = Vector3.one * distance;
        }

        private void ApplyLens(FrameQuad q, Eye eye, Pose framePose)
        {
            if (_scope.Index == 0) { q.SetLens(false, Vector2.zero, 0, 0); return; }

            Vector2 center = new Vector2(0.5f, 0.5f);
            bool show = true;
            if (_lensAnchor.Index == 0)
            {
                if (tracking != null && tracking.Sample.Tracked)
                    center = capture.WorldToViewport(eye, tracking.Sample.Position, framePose, out show);
                else if (head != null)
                    center = capture.WorldToViewport(eye, head.position + head.forward, framePose, out show);
            }
            float radius = _lensRadius.Value;
            q.SetLens(true, center, show ? radius : 0f, Mathf.Max(0.01f, radius * 0.25f));
        }

        private void UpdatePresence()
        {
            float target = 1f;
            if (_hideLive.On)
            {
                bool remapped = Channel.Delay > 0.012 || Mathf.Abs(Channel.Rate - 1f) > 0.03f;
                target = remapped ? 1f : 0f;
            }
            float tau = target > _presence ? 0.06f : 0.25f; // appear fast, disappear gently
            _presence = Mathf.MoveTowards(_presence, target, Time.deltaTime / tau);
        }

        /// <summary>Maps the playback rate and delay to colour cues (the Transparency dimension).</summary>
        private void ComputeRateCues(out Color tint, out float tintAmount, out float desat, out float glow)
        {
            float r = Channel.Rate;
            float strength = _cues.On ? CueTint : 0f;
            if (r < 0f) { tint = new Color(0.85f, 0.6f, 1f); tintAmount = strength * 0.7f; desat = strength * 0.4f; }
            else if (r < 1f) { float s = Mathf.Clamp01(1f - r); tint = new Color(0.6f, 0.8f, 1f); tintAmount = strength * 0.6f * s; desat = strength * 0.35f * s; }
            else { float f = Mathf.Clamp01((r - 1f) / 4f); tint = new Color(1f, 0.78f, 0.5f); tintAmount = strength * 0.6f * f; desat = 0f; }
            glow = (_cues.On ? CueGlow : 0f) * Mathf.Clamp01((float)Channel.Delay / 0.3f);
        }

        private void SetAllVisible(bool v)
        {
            if (!_built) return;
            for (int e = 0; e < 2; e++)
            {
                _main[e].Visible = v;
                for (int k = 0; k < MaxEchoes; k++) _echoes[e, k].Visible = v;
            }
        }

        private void OnDestroy()
        {
            if (!_built) return;
            for (int e = 0; e < 2; e++)
            {
                _main[e].Destroy();
                for (int k = 0; k < MaxEchoes; k++) _echoes[e, k].Destroy();
            }
        }
    }
}
