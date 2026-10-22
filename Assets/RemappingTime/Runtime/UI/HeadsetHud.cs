using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// In-headset tuner and status display, operated with the <b>left</b> controller so the right hand
    /// (or right controller) stays free for the experience itself.
    ///
    /// <para>Controls:</para>
    /// <list type="bullet">
    /// <item>Y: cycle Full → Minimal → Hidden</item>
    /// <item>Left stick up/down: select a parameter. Left/right: change it (hold to accelerate)</item>
    /// <item>Left stick click: reset the selected parameter to its default</item>
    /// <item>X: next preset (main list; advanced presets are in the Preset entry and on the web page)</item>
    /// <item>Left grip (hold): bypass, i.e. compare with live (handled by <see cref="InputHub"/>)</item>
    /// </list>
    ///
    /// <para>Design choices to keep tuning from spoiling the experience: the panel is world-anchored low
    /// in the field of view and only re-centres after a large head turn; it is drawn in real time (it is
    /// virtual content, not part of the remapped passthrough); it collapses to one status line after a few
    /// idle seconds; and every change applies instantly, so you can feel a difference without taking the
    /// headset off. Use Hidden (or the web operator page) when demoing low-transparency effects.</para>
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class HeadsetHud : MonoBehaviour
    {
        private enum Mode { Minimal = 0, Full = 1, Hidden = 2 } // order matches the "hud.mode" options

        [SerializeField] private TimeRemapEngine engine;
        [SerializeField] private Transform head;
        [SerializeField] private Shader uiShader;
        [SerializeField] private int visibleRows = 11;

        public readonly TunableSet Params = new TunableSet("HUD");
        private Tunable _modeChoice, _autoCollapse, _distance, _height;

        // Starts as a status display with a Menu button that opens the tuning panel. Y or "hud.mode" changes it.
        private Mode _mode
        {
            get => (Mode)_modeChoice.Index;
            set => _modeChoice.Value = (int)value;
        }
        private Transform _panel;
        private TextMeshPro _status, _menu, _footer;
        private LineRenderer _delayLine, _rateLine, _unityLine;
        private GameObject _background, _graphRoot;
        private readonly List<Tunable> _items = new List<Tunable>();
        private int _cursor;
        private float _idleTime, _repeatTimer, _holdTime;
        private int _lastStickDirY, _lastStickDirX;
        private bool _placed, _recentering;
        private float _nextTextUpdate;
        private bool _itemsDirty = true;
        private readonly StringBuilder _sb = new StringBuilder(2048);

        private const float PanelW = 0.46f, PanelH = 0.36f;

        /// <summary>The HUD's panel (null until built). Other UI can attach to it, like the menu launcher.</summary>
        public Transform PanelTransform => _panel;
        /// <summary>True while the HUD is visible (not in Hidden mode).</summary>
        public bool IsShown => _panel != null && _mode != Mode.Hidden;
        /// <summary>Where the menu launcher sits on the HUD: just right of the status lines.</summary>
        public Vector3 LauncherLocalPosition => new Vector3(PanelW / 2 + 0.05f, PanelH / 2 - 0.05f, -0.002f);
        private const int GraphPoints = 120;
        private const float GraphSeconds = 6f;

        private void Awake()
        {
            _modeChoice = Params.Add(Tunable.Choice("hud.mode", "HUD mode", new[] { "Minimal", "Full", "Hidden" }, 0,
                "Minimal = status line with the web address. Full = adds the thumbstick tuner and live graph. (Y cycles.)"));
            _autoCollapse = Params.Add(Tunable.Range("hud.autoCollapse", "Full HUD auto-collapse", 0f, 30f, 8f, 1f, "s",
                "When the HUD is Full, fall back to Minimal after this many seconds without thumbstick input (0 = never)."));
            _distance = Params.Add(Tunable.Range("hud.distance", "HUD distance", 0.3f, 1.2f, 0.5f, 0.05f, "m",
                "Within arm's reach if you want to poke the Menu button."));
            _height = Params.Add(Tunable.Range("hud.height", "HUD height", -0.5f, 0.2f, -0.2f, 0.02f, "m",
                "Vertical offset from eye level (negative = below)."));

            // Placement changes apply right away (otherwise the panel only moves after a large head turn).
            _distance.Changed += _ => _recentering = true;
            _height.Changed += _ => _recentering = true;
            _modeChoice.Changed += _ => _idleTime = 0f;
        }

        private void Start()
        {
            if (engine == null) engine = FindFirstObjectByType<TimeRemapEngine>();
            if (head == null) head = engine != null ? engine.Head : null;
            engine.RegisterTunables(Params);
            engine.SchemaChanged += () => _itemsDirty = true;
            Build();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Construction
        // ─────────────────────────────────────────────────────────────────────

        private void Build()
        {
            _panel = new GameObject("HUD panel").transform;
            _panel.SetParent(transform, false);

            _background = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _background.name = "Background";
            Destroy(_background.GetComponent<Collider>());
            _background.transform.SetParent(_panel, false);
            _background.transform.localScale = new Vector3(PanelW, PanelH, 1f);
            _background.GetComponent<MeshRenderer>().sharedMaterial =
                new Material(uiShader) { color = new Color(0.04f, 0.05f, 0.07f, 0.78f), renderQueue = 2990 };

            _status = CreateText("Status", new Vector2(PanelW - 0.03f, 0.09f), new Vector3(0, PanelH / 2 - 0.055f, -0.002f), 0.15f);
            _menu = CreateText("Menu", new Vector2(PanelW - 0.03f, 0.17f), new Vector3(0, -0.005f, -0.002f), 0.14f);
            _footer = CreateText("Footer", new Vector2(PanelW - 0.03f, 0.03f), new Vector3(0, -PanelH / 2 + 0.085f, -0.002f), 0.11f);

            _graphRoot = new GameObject("Graph");
            _graphRoot.transform.SetParent(_panel, false);
            _graphRoot.transform.localPosition = new Vector3(0, -PanelH / 2 + 0.04f, -0.001f);
            _unityLine = CreateLine("rate = 1", new Color(1f, 1f, 1f, 0.18f), 2);
            _delayLine = CreateLine("delay", new Color(0.35f, 0.85f, 1f, 0.95f), GraphPoints);
            _rateLine = CreateLine("rate", new Color(1f, 0.72f, 0.3f, 0.95f), GraphPoints);
        }

        private TextMeshPro CreateText(string name, Vector2 size, Vector3 localPos, float fontSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_panel, false);
            go.transform.localPosition = localPos;
            var t = go.AddComponent<TextMeshPro>();
            t.rectTransform.sizeDelta = size;
            t.fontSize = fontSize;
            t.alignment = TextAlignmentOptions.TopLeft;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Truncate;
            t.richText = true;
            t.color = new Color(0.92f, 0.95f, 1f);
            return t;
        }

        private LineRenderer CreateLine(string name, Color color, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_graphRoot.transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.alignment = LineAlignment.TransformZ;
            lr.widthMultiplier = 0.0025f;
            lr.positionCount = points;
            lr.material = new Material(uiShader) { color = Color.white, renderQueue = 2995 };
            lr.startColor = lr.endColor = color;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Update
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (engine == null || _panel == null) return;
            if (_itemsDirty) RebuildItems();
            HandleInput();
        }

        private void LateUpdate()
        {
            if (engine == null || _panel == null) return;

            bool show = _mode != Mode.Hidden;
            if (_panel.gameObject.activeSelf != show) _panel.gameObject.SetActive(show);
            if (!show) return;

            bool full = _mode == Mode.Full;
            _menu.gameObject.SetActive(full);
            _footer.gameObject.SetActive(full);
            _graphRoot.SetActive(full);
            _background.transform.localScale = full ? new Vector3(PanelW, PanelH, 1f) : new Vector3(PanelW, 0.1f, 1f);
            _background.transform.localPosition = full ? Vector3.zero : new Vector3(0, PanelH / 2 - 0.05f, 0);

            FollowHead();

            if (Time.unscaledTime >= _nextTextUpdate)
            {
                _nextTextUpdate = Time.unscaledTime + 1f / 15f;
                _status.text = BuildStatus(full);
                if (full)
                {
                    _menu.text = BuildMenu();
                    _footer.text = BuildFooter();
                }
            }
            if (full) UpdateGraph();
        }

        private void FollowHead()
        {
            if (head == null) return;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 target = head.position + fwd * _distance.Value + Vector3.up * _height.Value;
            Vector3 toPanel = Vector3.ProjectOnPlane(_panel.position - head.position, Vector3.up);
            bool farOff = !_placed || Vector3.Angle(toPanel, fwd) > 35f || Mathf.Abs(toPanel.magnitude - _distance.Value) > 0.25f;

            if (farOff || _recentering)
            {
                _recentering = Vector3.Distance(_panel.position, target) > 0.01f;
                _panel.position = _placed ? Vector3.Lerp(_panel.position, target, 1f - Mathf.Exp(-Time.deltaTime / 0.2f)) : target;
                _placed = true;
            }
            _panel.rotation = Quaternion.LookRotation(_panel.position - head.position, Vector3.up);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Input (left controller)
        // ─────────────────────────────────────────────────────────────────────

        private void HandleInput()
        {
            const OVRInput.Controller L = OVRInput.Controller.LTouch;
            bool any = false;

            if (OVRInput.GetDown(OVRInput.Button.Two, L))
            {
                _mode = _mode == Mode.Minimal ? Mode.Hidden : _mode == Mode.Hidden ? Mode.Full : Mode.Minimal;
                any = true;
            }

            if (_mode != Mode.Hidden)
            {
                Vector2 stick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, L);
                int dirY = stick.y > 0.6f ? 1 : stick.y < -0.6f ? -1 : 0;
                int dirX = Mathf.Abs(stick.y) < 0.5f ? (stick.x > 0.6f ? 1 : stick.x < -0.6f ? -1 : 0) : 0;

                if (dirY != 0 || dirX != 0)
                {
                    any = true;
                    if (_mode == Mode.Minimal) { _mode = Mode.Full; _lastStickDirY = dirY; _lastStickDirX = dirX; }
                    else
                    {
                        bool fresh = dirY != _lastStickDirY || dirX != _lastStickDirX;
                        _holdTime = fresh ? 0f : _holdTime + Time.unscaledDeltaTime;
                        _repeatTimer -= Time.unscaledDeltaTime;
                        if (fresh || _repeatTimer <= 0f)
                        {
                            _repeatTimer = fresh ? 0.35f : 0.09f;
                            if (dirY != 0) MoveCursor(-dirY);
                            if (dirX != 0) Adjust(dirX * (_holdTime > 1.2f ? 5 : 1));
                        }
                    }
                }
                _lastStickDirY = dirY;
                _lastStickDirX = dirX;

                if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, L) && Selected() != null)
                {
                    Selected().ResetToDefault();
                    any = true;
                }
                if (OVRInput.GetDown(OVRInput.Button.One, L))
                {
                    engine.NextMainPreset();
                    any = true;
                }
            }

            _idleTime = any ? 0f : _idleTime + Time.unscaledDeltaTime;
            if (_mode == Mode.Full && _autoCollapse.Value > 0f && _idleTime > _autoCollapse.Value) _mode = Mode.Minimal;
        }

        private void RebuildItems()
        {
            string selectedId = Selected()?.Id;
            _items.Clear();
            foreach (Tunable t in engine.AllTunables()) if (!t.Hidden) _items.Add(t);
            int idx = selectedId == null ? -1 : _items.FindIndex(t => t.Id == selectedId);
            _cursor = Mathf.Clamp(idx >= 0 ? idx : _cursor, 0, Mathf.Max(0, _items.Count - 1));
            _itemsDirty = false;
        }

        private Tunable Selected() => _cursor >= 0 && _cursor < _items.Count ? _items[_cursor] : null;

        private void MoveCursor(int delta)
        {
            if (_items.Count == 0) return;
            _cursor = (_cursor + delta + _items.Count) % _items.Count;
        }

        private void Adjust(int steps)
        {
            Tunable t = Selected();
            if (t == null) return;
            // Choices step one option at a time regardless of acceleration.
            t.Nudge(t.IsChoice ? (int)Mathf.Sign(steps) : steps);
            // Changing policy/preset rebuilds the list; keep the cursor on the same entry.
            _itemsDirty = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Text
        // ─────────────────────────────────────────────────────────────────────

        private string BuildStatus(bool full)
        {
            RemapChannel ch = engine.Main;
            RemapContext ctx = engine.LastContext;
            float budget = Mathf.Max(0.01f, engine.BudgetSeconds);
            _sb.Clear();

            _sb.Append("<b>").Append(Clean(engine.ActivePolicy?.DisplayName)).Append("</b>");
            if (!string.IsNullOrEmpty(engine.ActivePresetName))
                _sb.Append("  <color=#8fa3b8>").Append(Clean(engine.ActivePresetName)).Append("</color>");
            if (engine.Inputs != null && engine.Inputs.Bypass) _sb.Append("  <color=#ffd54a><b>BYPASS</b></color>");
            _sb.Append('\n');

            _sb.Append("delay <b>").Append(ch.Delay.ToString("F2")).Append("s</b>   rate <b>")
               .Append(ch.Rate.ToString("F2")).Append("x</b>   ");
            if (full) _sb.Append("u ").Append(ch.U.ToString("F2")).Append("  alpha ").Append(ch.Alpha.ToString("F2")).Append("   ");
            _sb.Append("<i>").Append(Clean(ch.Phase)).Append("</i>");

            if (!full)
            {
                // Always show where to point a browser: this line is often the only HUD visible.
                _sb.Append("\n<size=80%><color=#8fa3b8>")
                   .Append(string.IsNullOrEmpty(engine.ControlUrl) ? "web off" : engine.ControlUrl + "operator")
                   .Append(engine.WebClients > 0 ? $"   ({engine.WebClients} connected)" : "")
                   .Append("</color></size>");
                return _sb.ToString();
            }

            _sb.Append('\n');
            int bars = 20, filled = Mathf.RoundToInt(Mathf.Clamp01((float)ch.Delay / budget) * bars);
            _sb.Append("buffer <color=#5fd3ff>").Append('|', filled).Append("</color><color=#33404d>")
               .Append('|', bars - filled).Append("</color> ").Append(budget.ToString("F1")).Append("s   cam ")
               .Append(engine.Capture.CaptureFps.ToString("F0")).Append(" Hz   ")
               .Append(engine.Tracking != null ? engine.Tracking.ActiveSourceName : "no tracking");
            if (ctx.Motion.Tracked) _sb.Append(' ').Append(ctx.Motion.Speed.ToString("F1")).Append(" m/s");
            _sb.Append('\n');
            _sb.Append("<color=#8fa3b8>web ").Append(string.IsNullOrEmpty(engine.ControlUrl) ? "off" : engine.ControlUrl)
               .Append("   ").Append(engine.WebClients).Append(" connected</color>");
            return _sb.ToString();
        }

        private string BuildMenu()
        {
            _sb.Clear();
            if (_items.Count == 0) return "";
            int rows = Mathf.Min(visibleRows, _items.Count);
            int start = Mathf.Clamp(_cursor - rows / 2, 0, _items.Count - rows);
            for (int i = start; i < start + rows; i++)
            {
                Tunable t = _items[i];
                bool sel = i == _cursor;
                _sb.Append(sel ? "<color=#ffffff><b>> " : "<color=#b7c3cf>  ");
                _sb.Append("<color=#6f8091><size=80%>").Append(Clean(t.Group)).Append("</size></color>  ");
                _sb.Append(Clean(t.Label));
                _sb.Append("<pos=64%>");
                if (sel && !t.IsToggle) _sb.Append("< ");
                _sb.Append(Clean(t.FormatValue()));
                if (sel && !t.IsToggle) _sb.Append(" >");
                _sb.Append(sel ? "</b></color>\n" : "</color>\n");
            }
            return _sb.ToString();
        }

        private string BuildFooter()
        {
            Tunable t = Selected();
            string help = t != null ? Clean(t.Help) : "";
            if (help.Length > 110) help = help.Substring(0, 107) + "...";
            return $"<color=#8fa3b8><noparse>{help}</noparse>\n<size=85%>stick: select/adjust   click: default   X: next preset   Y: hide   L-grip: compare live</size></color>";
        }

        private void UpdateGraph()
        {
            TelemetryHistory h = engine.Main.History;
            float w = PanelW - 0.04f, gh = 0.05f;
            float budget = Mathf.Max(0.01f, engine.BudgetSeconds);
            float yUnity = Mathf.InverseLerp(-1f, 4f, 1f) * gh;
            _unityLine.SetPosition(0, new Vector3(-w / 2, yUnity, 0));
            _unityLine.SetPosition(1, new Vector3(w / 2, yUnity, 0));
            if (h.Count < 2) return;

            float latest = h.LatestTime;
            int n = h.Count;
            for (int p = 0; p < GraphPoints; p++)
            {
                float tt = latest - GraphSeconds + GraphSeconds * p / (GraphPoints - 1);
                int i = FindSample(h, tt, n);
                float x = -w / 2 + w * p / (GraphPoints - 1);
                _delayLine.SetPosition(p, new Vector3(x, Mathf.Clamp01(h.Delay[i] / budget) * gh, 0));
                _rateLine.SetPosition(p, new Vector3(x, Mathf.InverseLerp(-1f, 4f, h.Rate[i]) * gh, 0));
            }
        }

        private static int FindSample(TelemetryHistory h, float t, int n)
        {
            // Binary search over logical indices (time increases with index).
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (h.Time[h.IndexOf(mid)] <= t) lo = mid; else hi = mid;
            }
            return h.IndexOf(lo);
        }

        /// <summary>The default TMP font lacks some symbols used in labels; spell them out.</summary>
        internal static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("α", "alpha").Replace("δ", "delay").Replace("×", "x").Replace("≈", "~")
                    .Replace("∝", "~").Replace("→", "->").Replace("—", "-").Replace("–", "-").Replace("·", "-");
        }
    }
}
