using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// The hub of the system. Each frame it:
    /// <list type="number">
    /// <item>reads physical time t from the camera clock,</item>
    /// <item>gathers signals (inputs, tracked-point motion, zone depth) into a <see cref="RemapContext"/>,</item>
    /// <item>lets the active <see cref="RemapPolicy"/> choose u(t), α(t), and steps the main
    /// <see cref="RemapChannel"/> (plus any gallery channels),</item>
    /// <item>hands the channel to the <see cref="ReprojectedFrameView"/>, which draws frame s(t) = t − δ(t).</item>
    /// </list>
    ///
    /// It also owns the tunable registry (everything adjustable from the HUD or the web), presets,
    /// and a small command API (<see cref="SelectPolicy"/>, <see cref="ApplyPreset"/>, <see cref="SetTunable"/>, …)
    /// used by both front ends. Games and studies built on this should drive it through the same API.
    /// </summary>
    [DefaultExecutionOrder(0)]
    public sealed class TimeRemapEngine : MonoBehaviour
    {
        [Header("Components")]
        [SerializeField] private StereoPassthroughCapture capture;
        [SerializeField] private TrackedPoint tracking;
        [SerializeField] private InputHub input;
        [SerializeField] private ReprojectedFrameView view;
        [SerializeField] private ZoneVolume zone;
        [SerializeField] private TimelineGallery gallery;
        [Tooltip("The centre-eye anchor of the camera rig.")]
        [SerializeField] private Transform head;

        [Header("Startup")]
        [SerializeField] private string startupPreset = "Hold to slow";

        public StereoPassthroughCapture Capture => capture;
        public TrackedPoint Tracking => tracking;
        public InputHub Inputs => input;
        public ReprojectedFrameView View => view;
        public Transform Head => head;

        public RemapChannel Main { get; private set; }
        public RemapPolicy ActivePolicy => Main?.Policy;
        public IReadOnlyList<RemapPolicy> Policies => _policies;
        public PresetLibrary Presets { get; private set; }
        public string ActivePresetName { get; private set; } = "";
        /// <summary>The context used for the most recent step (for UIs and logging).</summary>
        public RemapContext LastContext { get; private set; }
        public float BudgetSeconds => capture != null ? capture.BufferSeconds : 0f;
        /// <summary>URL of the web controller, if the remote bridge is running (set by <see cref="RemoteControlBridge"/>).</summary>
        public string ControlUrl { get; set; } = "";
        public int WebClients { get; set; }

        /// <summary>
        /// When true, policies see no tracked motion (and no zone contact). Set by UI that uses the hands
        /// for interaction (e.g. an in-headset menu), so that poking it does not trigger motion effects.
        /// </summary>
        public bool SuppressMotionSignals { get; set; }

        /// <summary>When true, the main view shows live (an A/B "compare" request from UI or scripts).</summary>
        public bool CompareLive { get; set; }

        private readonly SortedDictionary<string, string> _diagnostics = new SortedDictionary<string, string>();

        /// <summary>
        /// Publishes a short live diagnostic line under <paramref name="key"/> (e.g. "menu"). Shown on the web
        /// operator page, which makes it easy to debug on-device behaviour without a log viewer.
        /// </summary>
        public void SetDiagnostic(string key, string text) => _diagnostics[key] = text;

        /// <summary>All diagnostic lines, "key: text" joined by newlines.</summary>
        public string DiagnosticsText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, string> kv in _diagnostics) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            return sb.ToString().TrimEnd();
        }

        /// <summary>Raised when the set of tunables or presets changes (policy switched, preset saved, …).</summary>
        public event Action SchemaChanged;

        public readonly TunableSet EngineParams = new TunableSet("Engine");
        private Tunable _policyChoice, _presetChoice, _softBudget, _galleryToggle;
        private readonly List<RemapPolicy> _policies = new List<RemapPolicy>();
        private readonly List<TunableSet> _extraSets = new List<TunableSet>();
        private bool _suppressChoiceCallbacks;
        private double _lastTime = double.NaN;

        private void Awake()
        {
            if (head == null && Camera.main != null) head = Camera.main.transform;

            _policies.AddRange(PolicyCatalog.CreateAll());
            Presets = new PresetLibrary();
            Presets.Changed += RebuildPresetChoice;

            var names = new string[_policies.Count];
            for (int i = 0; i < names.Length; i++) names[i] = _policies[i].DisplayName;
            _policyChoice = Tunable.Choice("engine.policy", "Policy", names, 0, "Which remapping policy drives u(t), α(t).");
            _policyChoice.Changed += t => { if (!_suppressChoiceCallbacks) SelectPolicy(_policies[t.Index].Id); };
            // Only offer a policy choice when there is more than one (custom policies registered).
            if (_policies.Count > 1) EngineParams.Add(_policyChoice);

            foreach (RemapPolicy p in _policies)
                if (p is FunctionPolicy fn) fn.ExpressionsChanged += () => SchemaChanged?.Invoke();

            RebuildPresetChoice();

            _softBudget = EngineParams.Add(Tunable.Toggle("engine.softBudget", "Soft buffer limit", true,
                "Ease slow motion into a steady lag as the delay nears the buffer budget, not a hard stop."));
            _galleryToggle = EngineParams.Add(Tunable.Toggle("engine.gallery", "Timeline gallery", false,
                "Show several presets side by side on floating screens, all reacting to the same input."));
            _galleryToggle.Changed += t => SetGallery(t.On);

            Main = new RemapChannel("main", _policies[0]);
        }

        private void Start()
        {
            if (view != null) view.Channel = Main;
            // "In view" = inside the passthrough cameras' image, where remapping is visible.
            if (tracking != null && capture != null)
                tracking.InViewTest = p => capture.IsInView(p, tracking.ViewMargin);
            if (zone != null) RegisterTunables(zone.Params);
            if (!ApplyPreset(startupPreset)) SelectPolicy(_policies[0].Id);
        }

        /// <summary>Adds a component's tunables to the registry so they appear in the HUD and web UI.</summary>
        public void RegisterTunables(TunableSet set)
        {
            if (set != null && !_extraSets.Contains(set)) { _extraSets.Add(set); SchemaChanged?.Invoke(); }
        }

        /// <summary>All tunables in display order: engine, active policy, view, tracking, then others.</summary>
        public IEnumerable<Tunable> AllTunables()
        {
            foreach (Tunable t in EngineParams) yield return t;
            if (ActivePolicy != null) foreach (Tunable t in ActivePolicy.Params) yield return t;
            if (view != null) foreach (Tunable t in view.Params) yield return t;
            if (tracking != null) foreach (Tunable t in tracking.Params) yield return t;
            foreach (TunableSet s in _extraSets) foreach (Tunable t in s) yield return t;
        }

        public Tunable FindTunable(string id)
        {
            foreach (Tunable t in AllTunables()) if (t.Id == id) return t;
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Command API (used by the HUD, the web bridge, and your own scripts)
        // ─────────────────────────────────────────────────────────────────────

        public bool SetTunable(string id, float value)
        {
            Tunable t = FindTunable(id);
            if (t == null) return false;
            t.Value = value;
            return true;
        }

        public bool SelectPolicy(string id)
        {
            int index = _policies.FindIndex(p => p.Id == id);
            if (index < 0) return false;
            if (Main.Policy != _policies[index]) Main.SetPolicy(_policies[index]);

            _suppressChoiceCallbacks = true;
            _policyChoice.Value = index;
            _suppressChoiceCallbacks = false;

            if (ActivePolicy.UsesZone && zone != null && !zone.HasBeenPlaced) zone.PlaceInFront(HeadPose());
            SchemaChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Applies a preset: selects its policy, resets policy and view tunables to defaults, then sets
        /// the listed values. Tracking settings are left alone, since they depend on the hardware at hand.
        /// </summary>
        public bool ApplyPreset(string name)
        {
            RemapPreset preset = Presets.Find(name);
            if (preset == null) return false;

            if (!SelectPolicy(string.IsNullOrEmpty(preset.policy) ? "fn" : preset.policy)) SelectPolicy(_policies[0].Id);
            foreach (Tunable t in ActivePolicy.Params) t.ResetToDefault();
            if (view != null) foreach (Tunable t in view.Params) t.ResetToDefault();
            if (ActivePolicy is FunctionPolicy fn) PresetLibrary.ApplyFunction(preset, fn);
            foreach (ParamValue pv in preset.values) SetTunable(pv.id, pv.v);
            if (ActivePolicy.UsesZone && zone != null && !zone.HasBeenPlaced) zone.PlaceInFront(HeadPose());

            ActivePresetName = preset.name;
            _suppressChoiceCallbacks = true;
            _presetChoice.Value = Array.IndexOf(Presets.Names(), preset.name);
            _suppressChoiceCallbacks = false;
            SchemaChanged?.Invoke();
            return true;
        }

        /// <summary>Saves the active policy and all policy/view values under <paramref name="name"/>.</summary>
        public void SavePreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var values = new List<ParamValue>();
            foreach (Tunable t in ActivePolicy.Params) values.Add(new ParamValue(t.Id, t.Value));
            if (view != null) foreach (Tunable t in view.Params) values.Add(new ParamValue(t.Id, t.Value));
            ActivePresetName = name.Trim(); // set first: saving rebuilds the preset choice around it
            var preset = new RemapPreset { name = ActivePresetName, policy = ActivePolicy.Id, values = values.ToArray() };
            if (ActivePolicy is FunctionPolicy fn)
            {
                preset.when = fn.WhenSource;
                preset.u = fn.USource;
                preset.alpha = fn.AlphaSource;
                // Store knob names without the "k1 · " prefix the UI adds.
                preset.knobs = new[] { fn.Knob1, fn.Knob2, fn.Knob3 }
                    .Select(k => k.Label.Contains("·") ? k.Label.Substring(k.Label.IndexOf('·') + 1).Trim() : "").ToArray();
                preset.curves = fn.Curves.Select(c => new CurveData { name = c.Name, xs = c.Xs, ys = c.Ys }).ToArray();
            }
            Presets.Save(preset);
        }

        /// <summary>The time-function policy, if it is the active one.</summary>
        public FunctionPolicy Function => ActivePolicy as FunctionPolicy;

        /// <summary>Sets one formula of the active time function ("when", "u" or "alpha").</summary>
        public bool SetExpression(string which, string source, out string error)
        {
            error = "the active policy has no formulas";
            if (Function == null) return false;
            bool ok = Function.SetExpression(which, source, out error);
            if (ok && Function.UsesZone && zone != null && !zone.HasBeenPlaced) zone.PlaceInFront(HeadPose());
            return ok;
        }

        /// <summary>Replaces the points of a drawn curve ("c1" or "c2") of the active time function.</summary>
        public bool SetCurve(string name, float[] xs, float[] ys)
        {
            Curve c = Function?.FindCurve(name);
            return c != null && c.SetPoints(xs, ys);
        }

        /// <summary>Renames a knob (k1..k3) of the active time function.</summary>
        public void SetKnobLabel(int knob, string label)
        {
            if (Function == null || knob < 1 || knob > 3) return;
            Tunable k = knob == 1 ? Function.Knob1 : knob == 2 ? Function.Knob2 : Function.Knob3;
            k.Label = string.IsNullOrWhiteSpace(label) ? $"k{knob} (unused)" : $"k{knob} · {label.Trim()}";
            SchemaChanged?.Invoke();
        }

        public void DeletePreset(string name) => Presets.Delete(name);

        public void ResetPolicyParams()
        {
            foreach (Tunable t in ActivePolicy.Params) t.ResetToDefault();
        }

        public void MoveWaterInFront()
        {
            if (zone != null) zone.PlaceInFront(HeadPose());
        }

        public void SetGallery(bool on)
        {
            if (gallery == null) return;
            if (on) gallery.Show(Presets, HeadPose()); else gallery.Hide();
            if (_galleryToggle.On != on) _galleryToggle.Value = on ? 1 : 0;
        }

        public Pose HeadPose() => head != null ? new Pose(head.position, head.rotation) : new Pose(Vector3.zero, Quaternion.identity);

        // ─────────────────────────────────────────────────────────────────────
        // Per-frame update
        // ─────────────────────────────────────────────────────────────────────

        private void Update()
        {
            if (capture == null || !capture.IsReady) return;

            double now = capture.Clock.Now;
            float dt = double.IsNaN(_lastTime) ? 0f : (float)Math.Min(0.1, Math.Max(0.0, now - _lastTime));
            _lastTime = now;

            // The largest delay we can show is bounded by the budget and by how much has been buffered so far.
            float maxDelay = Mathf.Min(capture.BufferSeconds, (float)Math.Max(0.0, now - capture.Buffer.OldestTime));

            bool motionOn = tracking != null && !SuppressMotionSignals;
            MotionSample motion = motionOn ? tracking.Sample : default;
            RemapInputs inputs = input != null ? input.Current : default;

            var ctx = new RemapContext
            {
                Time = now,
                Dt = dt,
                MaxDelay = maxDelay,
                Inputs = inputs,
                Motion = motion,
                Left = motionOn ? tracking.LeftSample : default,
                Right = motionOn ? tracking.RightSample : default,
                Head = HeadPose(),
            };

            UpdateZone(ref ctx);

            // While the gallery is open the surroundings stay live, so the floating screens are the comparison.
            bool galleryOpen = gallery != null && gallery.Visible;
            Main.SoftBudget = _softBudget.On;
            Main.Step(ctx, (input != null && input.Bypass) || CompareLive || galleryOpen);
            ctx.Delay = Main.Delay;
            LastContext = ctx;

            if (galleryOpen) gallery.Step(ctx);
        }

        private void UpdateZone(ref RemapContext ctx)
        {
            if (zone == null) return;
            bool uses = ActivePolicy != null && ActivePolicy.UsesZone;
            if (zone.Visible != uses) zone.Visible = uses;
            if (!uses) return;

            // Right grip drags the zone to the right controller/hand (handy for placing it on a real table).
            bool grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch) > 0.6f;
            MotionSample right = tracking != null ? tracking.RightSample : ctx.Motion;
            if (grip && right.Tracked) zone.MoveTo(right.Position);

            // Either hand can enter the water; the deeper one drives the effect (and becomes 'speed').
            ctx.ZoneDepth = 0f;
            ctx.InZone = false;
            if (!grip && !SuppressMotionSignals && tracking != null)
            {
                foreach (MotionSample s in new[] { tracking.RightSample, tracking.LeftSample })
                {
                    if (!s.Tracked) continue;
                    float depth = zone.Depth(s.Position, out bool inside);
                    if (inside && (!ctx.InZone || depth > ctx.ZoneDepth))
                    {
                        ctx.InZone = true;
                        ctx.ZoneDepth = depth;
                        ctx.Motion = s;
                    }
                }
            }
            // Refract whatever the main view currently shows, so the water matches the remapped world.
            StereoFrame frame = capture.Buffer.TrySample(Main.DisplayTime, out FrameSample sample) ? sample.Nearest : null;
            zone.UpdateVisual(ctx.InZone, frame);
        }

        private void RebuildPresetChoice()
        {
            Tunable old = _presetChoice;
            _presetChoice = Tunable.Choice("engine.preset", "Preset", Presets.Names(),
                Math.Max(0, Array.IndexOf(Presets.Names(), ActivePresetName)), "Load a saved configuration.");
            _presetChoice.Changed += t => { if (!_suppressChoiceCallbacks) ApplyPreset(Presets.All[t.Index].name); };

            // Keep the preset choice right after the policy choice in the display order.
            EngineParams.Remove(old);
            EngineParams.Insert(1, _presetChoice);
            SchemaChanged?.Invoke();
        }
    }
}
