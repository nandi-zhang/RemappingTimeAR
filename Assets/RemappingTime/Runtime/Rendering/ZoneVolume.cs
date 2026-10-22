using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// A block of virtual water in the room. Time-function formulas can read how deep a hand is inside it
    /// (<c>depth</c>, <c>inzone</c>), which makes pseudo-haptic "thick material" effects possible.
    ///
    /// <para><b>Trigger region.</b> The trigger region is the visible block grown by a forgiveness margin on
    /// every side. People judge contact by the visible surface while the tracked fingertip sits inside the
    /// finger, and hand tracking has small offsets, so without a margin the effect starts too late and you have
    /// to reach "through" the water.</para>
    ///
    /// <para><b>Look.</b> Rendered with the WaterVolume shader, which refracts each eye's passthrough camera
    /// image, so it reads as clear water rather than a painted box. One copy is drawn per eye (on the eye
    /// layers) so each eye refracts its own camera.</para>
    ///
    /// Placed in front of the wearer when first needed; hold the right grip to move it to your right hand.
    /// </summary>
    public sealed class ZoneVolume : MonoBehaviour
    {
        [SerializeField] private Shader shader;
        [SerializeField] private StereoPassthroughCapture capture;
        [SerializeField] private string leftEyeLayer = "LeftEye";
        [SerializeField] private string rightEyeLayer = "RightEye";

        public readonly TunableSet Params = new TunableSet("Water");
        private Tunable _size, _margin, _opacity, _look;

        /// <summary>Material looks: tint, deep colour, ripple strength. Honey is amber, thicker and calmer.</summary>
        private static readonly (Color tint, Color deep, float waves, float opacityBoost)[] Looks =
        {
            (new Color(0.62f, 0.86f, 1.0f), new Color(0.05f, 0.28f, 0.45f), 0.35f, 0f),   // Water
            (new Color(1.0f, 0.78f, 0.32f), new Color(0.55f, 0.30f, 0.02f), 0.12f, 0.05f), // Honey
        };

        private readonly Transform[] _visual = new Transform[2];
        private readonly Material[] _material = new Material[2];
        private GameObject _root;
        private float _inside;
        private bool _placed;

        private static readonly int InsideId = Shader.PropertyToID("_Inside");
        private static readonly int CamTexId = Shader.PropertyToID("_CamTex");
        private static readonly int WorldToCamId = Shader.PropertyToID("_WorldToCam");
        private static readonly int IntrId = Shader.PropertyToID("_Intr");
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int DeepId = Shader.PropertyToID("_Deep");
        private static readonly int WaveId = Shader.PropertyToID("_WaveAmp");

        public float Size => _size.Value;
        public float Margin => _margin.Value;
        public bool HasBeenPlaced => _placed;

        private void Awake()
        {
            _size = Params.Add(Tunable.Range("water.size", "Water block size", 0.1f, 0.8f, 0.3f, 0.02f, "m"));
            _margin = Params.Add(Tunable.Range("water.margin", "Touch margin", 0f, 0.1f, 0.03f, 0.005f, "m",
                "The water reacts this far outside its visible surface, so touching the surface is enough."));
            _opacity = Params.Add(Tunable.Range("water.opacity", "Water opacity", 0.2f, 1f, 0.9f, 0.05f));
            _look = Params.Add(Tunable.Choice("water.look", "Material look", new[] { "Water", "Honey" }, 0,
                "Clear blue water, or thick amber honey."));
        }

        public bool Visible
        {
            get => _root != null && _root.activeSelf;
            set
            {
                EnsureVisual();
                _root.SetActive(value);
            }
        }

        /// <summary>Places the block at a comfortable reach in front of and below the head (yaw only).</summary>
        public void PlaceInFront(Pose head)
        {
            Vector3 fwd = Vector3.ProjectOnPlane(head.rotation * Vector3.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            transform.SetPositionAndRotation(head.position + fwd * 0.45f + Vector3.down * 0.35f, Quaternion.LookRotation(fwd, Vector3.up));
            _placed = true;
        }

        public void MoveTo(Vector3 center)
        {
            transform.position = center;
            _placed = true;
        }

        /// <summary>
        /// Depth (m) of <paramref name="worldPoint"/> inside the trigger region (the block grown by the margin):
        /// the distance to its nearest face, 0 when outside.
        /// </summary>
        public float Depth(Vector3 worldPoint, out bool inside)
        {
            Vector3 p = transform.InverseTransformPoint(worldPoint);
            float half = _size.Value * 0.5f + _margin.Value;
            float depth = Mathf.Min(half - Mathf.Abs(p.x), Mathf.Min(half - Mathf.Abs(p.y), half - Mathf.Abs(p.z)));
            inside = depth > 0f;
            return Mathf.Max(0f, depth);
        }

        /// <summary>Per-frame visual update: hand feedback and the camera frame to refract.</summary>
        public void UpdateVisual(bool handInside, StereoFrame frame)
        {
            if (_root == null) return;
            _inside = Mathf.MoveTowards(_inside, handInside ? 1f : 0f, Time.deltaTime / 0.15f);
            for (int e = 0; e < 2; e++)
            {
                if (_visual[e] == null) continue;
                _visual[e].localScale = Vector3.one * _size.Value;
                Material m = _material[e];
                m.SetFloat(InsideId, _inside);
                var look = Looks[Mathf.Clamp(_look.Index, 0, Looks.Length - 1)];
                m.SetFloat(OpacityId, Mathf.Clamp01(_opacity.Value + look.opacityBoost));
                m.SetColor(TintId, look.tint);
                m.SetColor(DeepId, look.deep);
                m.SetFloat(WaveId, look.waves);
                if (frame == null || capture == null || capture.LocalCorners((Eye)e) == null) continue;

                var eye = (Eye)e;
                Pose pose = frame.CameraPose(eye);
                m.SetTexture(CamTexId, frame.Texture(eye));
                m.SetMatrix(WorldToCamId, Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one).inverse);
                // Camera-local (x/z, y/z) → viewport (0..1), from the image corners at unit depth.
                Vector3[] c = capture.LocalCorners(eye);
                float a = 1f / (c[1].x - c[0].x), cy = 1f / (c[2].y - c[0].y);
                m.SetVector(IntrId, new Vector4(a, -c[0].x * a, cy, -c[0].y * cy));
            }
        }

        private void EnsureVisual()
        {
            if (_root != null) return;
            _root = new GameObject("Water visual");
            _root.transform.SetParent(transform, false);

            int left = LayerMask.NameToLayer(leftEyeLayer), right = LayerMask.NameToLayer(rightEyeLayer);
            bool perEye = left >= 0 && right >= 0;
            for (int e = 0; e < (perEye ? 2 : 1); e++)
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = perEye ? $"Water ({(Eye)e} eye)" : "Water";
                Destroy(cube.GetComponent<Collider>());
                cube.layer = perEye ? (e == 0 ? left : right) : 0;
                cube.transform.SetParent(_root.transform, false);
                cube.transform.localScale = Vector3.one * _size.Value;
                if (shader != null)
                {
                    _material[e] = new Material(shader);
                    cube.GetComponent<MeshRenderer>().sharedMaterial = _material[e];
                }
                _visual[e] = cube.transform;
            }
        }
    }
}
