using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// One textured quad that displays a buffered camera frame to one eye. Shared by
    /// <see cref="ReprojectedFrameView"/> (frustum-shaped quads placed at the capture pose) and
    /// <see cref="TimelineWindow"/> (flat floating panels).
    ///
    /// Per-eye visibility works two ways so it is robust to rig configuration:
    /// the quad is put on the eye's culling layer (for per-eye cameras), and the shader can also
    /// discard it in the other view (for single-pass multiview), see <see cref="UseStereoEyeIndex"/>.
    /// </summary>
    public sealed class FrameQuad
    {
        public readonly Eye Eye;
        public readonly GameObject GameObject;
        public readonly Transform Transform;
        public readonly Material Material;
        private readonly MeshRenderer _renderer;
        private readonly Mesh _mesh;

        private static readonly int TexA = Shader.PropertyToID("_TexA");
        private static readonly int TexB = Shader.PropertyToID("_TexB");
        private static readonly int BlendId = Shader.PropertyToID("_Blend");
        private static readonly int Opacity = Shader.PropertyToID("_Opacity");
        private static readonly int Feather = Shader.PropertyToID("_Feather");
        private static readonly int AspectId = Shader.PropertyToID("_Aspect");
        private static readonly int TintColor = Shader.PropertyToID("_TintColor");
        private static readonly int TintAmount = Shader.PropertyToID("_TintAmount");
        private static readonly int Desat = Shader.PropertyToID("_Desat");
        private static readonly int GlowColor = Shader.PropertyToID("_GlowColor");
        private static readonly int Glow = Shader.PropertyToID("_Glow");
        private static readonly int LensOn = Shader.PropertyToID("_LensOn");
        private static readonly int LensCenter = Shader.PropertyToID("_LensCenter");
        private static readonly int LensRadius = Shader.PropertyToID("_LensRadius");
        private static readonly int LensFeather = Shader.PropertyToID("_LensFeather");
        private static readonly int EyeSelect = Shader.PropertyToID("_EyeSelect");
        private static readonly int EyeIndex = Shader.PropertyToID("_EyeIndex");

        public FrameQuad(string name, Eye eye, Shader shader, Transform parent, int layer, int renderQueue)
        {
            Eye = eye;
            GameObject = new GameObject(name) { layer = layer >= 0 ? layer : 0 };
            Transform = GameObject.transform;
            Transform.SetParent(parent, false);

            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            SetCorners(new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0));
            _mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            _mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };

            GameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = GameObject.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            Material = new Material(shader) { name = name, renderQueue = renderQueue };
            Material.SetFloat(EyeIndex, (int)eye);
            _renderer.sharedMaterial = Material;
        }

        /// <summary>Sets the quad corners in local space: bottom-left, bottom-right, top-left, top-right.</summary>
        public void SetCorners(Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr)
        {
            _mesh.vertices = new[] { bl, br, tl, tr };
            // Generous bounds: the quad is often far from the transform origin (frustum at depth).
            _mesh.bounds = new Bounds((bl + tr) * 0.5f, Vector3.one * 20f);
        }

        public bool Visible
        {
            get => _renderer.enabled;
            set { if (_renderer.enabled != value) _renderer.enabled = value; }
        }

        public bool UseStereoEyeIndex { set => Material.SetFloat(EyeSelect, value ? 1f : 0f); }

        public void SetFrames(Texture a, Texture b, float blend)
        {
            Material.SetTexture(TexA, a);
            Material.SetTexture(TexB, b);
            Material.SetFloat(BlendId, blend);
        }

        public void SetLook(float opacity, float feather, float aspect, Color tint, float tintAmount, float desat, Color glowColor, float glow)
        {
            Material.SetFloat(Opacity, opacity);
            Material.SetFloat(Feather, feather);
            Material.SetFloat(AspectId, aspect);
            Material.SetColor(TintColor, tint);
            Material.SetFloat(TintAmount, tintAmount);
            Material.SetFloat(Desat, desat);
            Material.SetColor(GlowColor, glowColor);
            Material.SetFloat(Glow, glow);
        }

        public void SetLens(bool on, Vector2 centerUv, float radius, float feather)
        {
            Material.SetFloat(LensOn, on ? 1f : 0f);
            Material.SetVector(LensCenter, new Vector4(centerUv.x, centerUv.y, 0, 0));
            Material.SetFloat(LensRadius, radius);
            Material.SetFloat(LensFeather, feather);
        }

        public void Destroy()
        {
            Object.Destroy(Material);
            Object.Destroy(_mesh);
            Object.Destroy(GameObject);
        }
    }
}
