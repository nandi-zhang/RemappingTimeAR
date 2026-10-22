using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// A floating stereo "screen" showing one <see cref="RemapChannel"/>. Unlike the main view it is not
    /// reprojected: it behaves like a window into a remapped copy of the camera feed.
    /// </summary>
    public sealed class TimelineWindow : MonoBehaviour
    {
        private StereoPassthroughCapture _capture;
        private RemapChannel _channel;
        private readonly FrameQuad[] _quads = new FrameQuad[2];
        private TextMeshPro _label;
        private float _nextLabelUpdate;

        public RemapChannel Channel => _channel;

        public void Init(StereoPassthroughCapture capture, RemapChannel channel, Shader shader, float width,
                         int leftLayer, int rightLayer, bool singlePassStereo, string title)
        {
            _capture = capture;
            _channel = channel;
            float h = width / Mathf.Max(0.1f, capture.Aspect);
            for (int e = 0; e < 2; e++)
            {
                var eye = (Eye)e;
                var q = new FrameQuad($"Window {title} {eye}", eye, shader, transform, eye == Eye.Left ? leftLayer : rightLayer, 1020);
                q.SetCorners(new Vector3(-width / 2, -h / 2, 0), new Vector3(width / 2, -h / 2, 0),
                             new Vector3(-width / 2, h / 2, 0), new Vector3(width / 2, h / 2, 0));
                q.UseStereoEyeIndex = singlePassStereo;
                q.SetLook(1f, 0.03f, capture.Aspect, Color.white, 0f, 0f, Color.white, 0f);
                q.SetLens(false, Vector2.zero, 0, 0);
                _quads[e] = q;
            }

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0, h / 2 + 0.035f, 0);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.alignment = TextAlignmentOptions.Center;
            _label.fontSize = 0.22f;
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.rectTransform.sizeDelta = new Vector2(width, 0.06f);
            _label.text = title;
        }

        private void LateUpdate()
        {
            if (_channel == null || !_capture.Buffer.TrySample(_channel.DisplayTime, out FrameSample s)) return;
            for (int e = 0; e < 2; e++)
                _quads[e].SetFrames(s.Older.Texture((Eye)e), s.Newer.Texture((Eye)e), s.Blend);

            if (Time.time >= _nextLabelUpdate)
            {
                _nextLabelUpdate = Time.time + 0.1f;
                _label.text = $"<b>{_channel.Name}</b>\n<size=70%>delay {_channel.Delay:F2}s   rate {_channel.Rate:F2}x   {_channel.Phase}</size>";
            }
        }

        private void OnDestroy()
        {
            foreach (FrameQuad q in _quads) q?.Destroy();
        }
    }

    /// <summary>
    /// Shows several remapping configurations side by side, each in its own <see cref="TimelineWindow"/>,
    /// all fed by the same live camera buffer and the same inputs. Press the trigger once and see how
    /// each u(t), α(t) responds. This is the in-headset version of a "many functions, many outcomes" slide.
    ///
    /// Each entry is a preset name from <see cref="PresetLibrary"/>, so the gallery can be edited
    /// without code.
    /// </summary>
    public sealed class TimelineGallery : MonoBehaviour
    {
        [SerializeField] private StereoPassthroughCapture capture;
        [SerializeField] private Shader frameShader;
        [SerializeField] private string[] presetNames = { "Hold to slow", "Bullet time", "Rewind while held", "Time wobble" };
        [SerializeField] private float distance = 1.4f;
        [SerializeField] private float windowWidth = 0.52f;
        [SerializeField] private float angleStep = 23f;
        [SerializeField] private float verticalOffset = -0.05f;
        [SerializeField] private string leftEyeLayer = "LeftEye";
        [SerializeField] private string rightEyeLayer = "RightEye";
        [SerializeField] private bool singlePassStereo = false;

        private readonly List<TimelineWindow> _windows = new List<TimelineWindow>();

        public bool Visible => _windows.Count > 0;
        public IReadOnlyList<TimelineWindow> Windows => _windows;

        public void Show(PresetLibrary presets, Pose head)
        {
            Hide();
            if (capture == null || !capture.IsReady) return;

            Vector3 fwd = Vector3.ProjectOnPlane(head.rotation * Vector3.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            int left = LayerMask.NameToLayer(leftEyeLayer), right = LayerMask.NameToLayer(rightEyeLayer);
            int n = presetNames.Length;
            for (int i = 0; i < n; i++)
            {
                RemapPreset preset = presets.Find(presetNames[i]);
                RemapPolicy policy = PolicyCatalog.Create(preset != null ? preset.policy : "live");
                if (preset != null) PresetLibrary.ApplyToPolicy(preset, policy);
                var channel = new RemapChannel(preset != null ? preset.name : presetNames[i], policy);

                float angle = (i - (n - 1) * 0.5f) * angleStep;
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * fwd;
                var go = new GameObject($"Timeline {channel.Name}");
                go.transform.SetParent(transform, false);
                go.transform.position = head.position + dir * distance + Vector3.up * verticalOffset;
                go.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

                var window = go.AddComponent<TimelineWindow>();
                window.Init(capture, channel, frameShader, windowWidth, left, right, singlePassStereo, channel.Name);
                _windows.Add(window);
            }
        }

        public void Hide()
        {
            foreach (TimelineWindow w in _windows) if (w != null) Destroy(w.gameObject);
            _windows.Clear();
        }

        /// <summary>Advances every gallery channel with the shared context (called by the engine).</summary>
        public void Step(in RemapContext ctx)
        {
            foreach (TimelineWindow w in _windows) w.Channel.Step(ctx);
        }
    }
}
