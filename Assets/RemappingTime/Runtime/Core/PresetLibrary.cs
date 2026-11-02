using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>The points of a drawn curve (c1 or c2), stored with a preset.</summary>
    [Serializable]
    public sealed class CurveData
    {
        public string name;
        public float[] xs, ys;
    }

    [Serializable]
    public sealed class ParamValue
    {
        public string id;
        public float v;
        public ParamValue(string id, float v) { this.id = id; this.v = v; }
    }

    /// <summary>
    /// A named configuration: the time function (formulas for when / u / α and the self-defined variables k1–k3)
    /// plus values for any tunables (sliders, view, tracking, …). Values that are not listed are reset to their
    /// defaults when the preset is applied, so a preset always produces the same experience regardless of what
    /// was tuned before.
    /// </summary>
    [Serializable]
    public sealed class RemapPreset
    {
        public string name;
        public string policy = "fn";
        /// <summary>One line shown in the UIs: what this preset does.</summary>
        public string description = "";
        public string when, u, alpha;
        /// <summary>Names for the self-defined variables k1, k2, k3 (empty = no name).</summary>
        public string[] knobs = Array.Empty<string>();
        /// <summary>Formulas for k1, k2, k3 (empty = the default). Older presets stored plain numbers as fn.k1… values.</summary>
        public string[] vars = Array.Empty<string>();
        public ParamValue[] values = Array.Empty<ParamValue>();
        /// <summary>Listed under "Advanced presets" in the UIs (the main list keeps the most representative ones).</summary>
        public bool advanced;
        /// <summary>Drawn curves; empty = the default shapes.</summary>
        public CurveData[] curves = Array.Empty<CurveData>();
        public bool builtIn;

        public RemapPreset() { }

        /// <summary>Marks a built-in preset as advanced (listed separately from the main presets).</summary>
        public RemapPreset AsAdvanced() { advanced = true; return this; }

        /// <summary>"main", "advanced" or "saved" (user presets), for grouping in the UIs.</summary>
        public string Category => !builtIn ? "saved" : advanced ? "advanced" : "main";

        /// <summary>A built-in time-function preset. <paramref name="knobs"/> are (name, value) for k1..k3.</summary>
        public static RemapPreset Fn(string name, string description, string when, string u, string alpha,
                                     (string label, float value)[] knobs, params (string id, float v)[] values)
        {
            var p = new RemapPreset
            {
                name = name, description = description, when = when, u = u, alpha = alpha, builtIn = true,
                knobs = new string[knobs.Length], vars = new string[knobs.Length],
            };
            var list = new List<ParamValue>();
            for (int k = 0; k < knobs.Length; k++)
            {
                p.knobs[k] = knobs[k].label;
                p.vars[k] = knobs[k].value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }
            foreach ((string id, float v) in values) list.Add(new ParamValue(id, v));
            p.values = list.ToArray();
            return p;
        }
    }

    [Serializable]
    internal sealed class PresetFile
    {
        public RemapPreset[] presets = Array.Empty<RemapPreset>();
    }

    /// <summary>
    /// Built-in presets plus user presets saved on the device
    /// (<c>Application.persistentDataPath/remapping_presets.json</c>; pull with
    /// <c>adb pull /sdcard/Android/data/&lt;package&gt;/files/remapping_presets.json</c>).
    /// </summary>
    public sealed class PresetLibrary
    {
        private readonly List<RemapPreset> _presets = new List<RemapPreset>();
        private static string FilePath => Path.Combine(Application.persistentDataPath, "remapping_presets.json");

        public IReadOnlyList<RemapPreset> All => _presets;
        public event Action Changed;

        public PresetLibrary()
        {
            _presets.AddRange(BuiltIns());
            LoadUserPresets();
        }

        public RemapPreset Find(string name) => _presets.Find(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase));

        public string[] Names()
        {
            var names = new string[_presets.Count];
            for (int i = 0; i < names.Length; i++) names[i] = _presets[i].name;
            return names;
        }

        /// <summary>
        /// The name a preset saved as <paramref name="name"/> will get: the same name (overwriting an earlier user
        /// preset), or "name (custom)", "name (custom 2)", … when a built-in preset already has it.
        /// </summary>
        public string ResolveSaveName(string name)
        {
            name = (name ?? "").Trim();
            RemapPreset existing = Find(name);
            if (existing == null || !existing.builtIn) return name;
            for (int n = 1; ; n++)
            {
                string candidate = n == 1 ? $"{name} (custom)" : $"{name} (custom {n})";
                RemapPreset other = Find(candidate);
                if (other == null || !other.builtIn) return candidate;
            }
        }

        /// <summary>Saves (or overwrites) a user preset and persists all user presets to disk. Returns its final name.</summary>
        public string Save(RemapPreset preset)
        {
            preset.builtIn = false;
            preset.name = ResolveSaveName(preset.name);
            int i = _presets.FindIndex(p => string.Equals(p.name, preset.name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _presets[i] = preset;
            else _presets.Add(preset);
            Persist();
            Changed?.Invoke();
            return preset.name;
        }

        public bool Delete(string name)
        {
            int i = _presets.FindIndex(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase) && !p.builtIn);
            if (i < 0) return false;
            _presets.RemoveAt(i);
            Persist();
            Changed?.Invoke();
            return true;
        }

        /// <summary>Applies a preset's values that belong to <paramref name="policy"/> (used for gallery channels).</summary>
        public static void ApplyToPolicy(RemapPreset preset, RemapPolicy policy)
        {
            foreach (Tunable t in policy.Params) t.ResetToDefault();
            if (policy is FunctionPolicy fn) ApplyFunction(preset, fn);
            foreach (ParamValue pv in preset.values)
            {
                Tunable t = policy.Params.Find(pv.id);
                if (t != null) t.Value = pv.v;
            }
        }

        /// <summary>
        /// Installs a preset's formulas and knob names on a time-function policy. If a formula doesn't compile (e.g. a
        /// hand-edited file), the policy falls back to live (when = 0, u = 0, α = 5) rather than keeping the previous
        /// preset's formulas, and false is returned.
        /// </summary>
        public static bool ApplyFunction(RemapPreset preset, FunctionPolicy fn)
        {
            bool ok = true;
            if (!string.IsNullOrEmpty(preset.u) || !string.IsNullOrEmpty(preset.when))
            {
                ok = fn.SetExpressions(preset.when ?? "0", preset.u ?? "0", preset.alpha ?? "5");
                if (!ok)
                {
                    fn.SetExpressions("0", "0", "5");
                    Debug.LogWarning($"[RemappingTime] Preset '{preset.name}' has a formula that doesn't compile; using live instead.");
                }
            }
            fn.ResetCurves();
            if (preset.curves != null)
                foreach (CurveData c in preset.curves) fn.FindCurve(c.name)?.SetPoints(c.xs, c.ys);
            for (int k = 1; k <= 3; k++)
            {
                fn.SetVariableName(k, preset.knobs != null && k <= preset.knobs.Length ? preset.knobs[k - 1] : "");
                string formula = preset.vars != null && k <= preset.vars.Length ? preset.vars[k - 1] : null;
                ParamValue legacy = Array.Find(preset.values ?? Array.Empty<ParamValue>(), v => v != null && v.id == $"fn.k{k}");
                if (!string.IsNullOrWhiteSpace(formula))
                {
                    if (!fn.SetExpression("k" + k, formula, out _)) { fn.SetExpression("k" + k, FunctionPolicy.DefaultVariable, out _); ok = false; }
                }
                else if (legacy != null) fn.SetVariableValue(k, legacy.v);
                else fn.SetExpression("k" + k, FunctionPolicy.DefaultVariable, out _);
            }
            return ok;
        }

        private void LoadUserPresets()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var file = JsonUtility.FromJson<PresetFile>(File.ReadAllText(FilePath));
                if (file?.presets == null) return;
                foreach (RemapPreset p in file.presets)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.name)) continue;
                    p.builtIn = false;
                    // A name a newer version gave to a built-in: keep the user's preset under a free name.
                    p.name = ResolveSaveName(p.name);
                    if (Find(p.name) == null) _presets.Add(p);
                }
            }
            catch (Exception e)
            {
                // Keep the unreadable file, so the next save doesn't overwrite the presets in it.
                Debug.LogWarning($"[RemappingTime] Could not read presets: {e.Message}");
                try { File.Copy(FilePath, FilePath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}", true); }
                catch (Exception) { /* nothing more we can do */ }
            }
        }

        private void Persist()
        {
            try
            {
                var file = new PresetFile { presets = _presets.FindAll(p => !p.builtIn).ToArray() };
                // Write a temporary file first, so a crash mid-write never leaves a truncated preset file.
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(file, true));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemappingTime] Could not save presets: {e.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Built-in presets: each is a trigger plus formulas for u(t) and α(t), i.e. one point in the design
        // space of time remapping. Most use the three basic sliders (uon, aon, aoff); self-defined variables
        // k1..k3 hold any extra numbers a preset needs (thresholds, timings), named per preset.
        // ─────────────────────────────────────────────────────────────────────
        private static IEnumerable<RemapPreset> BuiltIns()
        {
            var none = Array.Empty<(string, float)>();
            (string, float) U(float v) => ("fn.uon", v);
            (string, float) AOn(float v) => ("fn.aon", v);
            (string, float) AOff(float v) => ("fn.aoff", v);
            const string slowU = "on ? uon : 0", slowA = "on ? aon : aoff";

            yield return RemapPreset.Fn("Live", "Real time. Any leftover delay is caught up.",
                "0", "0", "aoff", none, AOff(5f));

            yield return RemapPreset.Fn("Hold to slow", "Hold the trigger (or Space) to slow time; release to fast-forward back.",
                "hold", slowU, slowA, none, U(0.8f), AOn(0.05f), AOff(5f));

            yield return RemapPreset.Fn("Echo trails", "Hold to slow, with earlier moments trailing behind moving things.",
                "hold", slowU, slowA, none, U(0.8f), AOn(0.05f), AOff(5f),
                ("view.echo", 1), ("view.echoCount", 4), ("view.echoSpacing", 0.07f), ("view.echoStrength", 0.5f));

            yield return RemapPreset.Fn("Time lens", "Only a circle around your hand is remapped.",
                "hold", slowU, slowA, none, U(0.8f), AOn(0.05f), AOff(5f),
                ("view.scope", 1), ("view.lensAnchor", 0), ("view.lensRadius", 0.22f), ("view.hideLive", 0));

            yield return RemapPreset.Fn("Ghost", "The slowed world appears as a ghost over the live one.",
                "hold", slowU, slowA, none, U(0.8f), AOn(0.05f), AOff(5f), ("view.opacity", 0.55f), ("view.hideLive", 0));

            yield return RemapPreset.Fn("Motion slow-mo", "Fast hand motion (over 0.6 m/s, in view) slows the world; it recovers when you stop.",
                "speed > 0.6", "on || toff < 0.15 ? uon : 0", "on || toff < 0.15 ? aon : aoff",
                none, U(0.75f), AOn(0.05f), AOff(5f));

            yield return RemapPreset.Fn("Time wobble", "u follows a slow sine wave (one breath every 2 s) around 'u when triggered', so time breathes slower and faster.",
                "1", "max(0, uon + 0.6 * sin(pi * t))", "aon",
                none, U(0.3f), AOn(1.5f));

            yield return RemapPreset.Fn("Constant u, α", "Fixed inputs settle into a constant lag (δ → u/α) at normal speed.",
                "1", "uon", "aon", none, U(0.3f), AOn(1f));

            yield return RemapPreset.Fn("Water", "Moving a hand through the water slows time: a pseudo-haptic 'thick' material.",
                "inzone", "on ? uon * sat(depth / 0.03) : 0", slowA, none,
                U(0.85f), AOn(1.5f), AOff(6f), ("view.distance", 0.6f), ("water.look", 0));

            yield return RemapPreset.Fn("Honey", "A much thicker material: deep, slow, and slow to let go.",
                "inzone", "on ? uon * sat(depth / 0.03) : 0", slowA, none,
                U(0.95f), AOn(0.6f), AOff(3f), ("view.distance", 0.6f), ("water.look", 1));

            // Advanced presets: more specialised designs, most with self-defined variables to tune.

            // Analog option: a half-pressed trigger (or the web strength slider) gives half the slowdown.
            yield return RemapPreset.Fn("Hold to slow (analog)", "Squeeze the trigger harder for slower time; release to fast-forward back.",
                "hold > 0.05", "on ? uon * hold : 0", slowA, none, U(0.8f), AOn(0.05f), AOff(5f)).AsAdvanced();

            yield return RemapPreset.Fn("Bullet time", "Very slow while held, then a long dramatic fast-forward.",
                "hold", slowU, slowA, none, U(0.92f), AOn(0.02f), AOff(3f)).AsAdvanced();

            yield return RemapPreset.Fn("Freeze frame", "Hold to freeze the world (u = 1).",
                "hold", slowU, slowA, none, U(1f), AOn(0f), AOff(5f)).AsAdvanced();

            yield return RemapPreset.Fn("Gradual brake", "Time eases into slow motion over k1 seconds instead of switching at once.",
                "hold", "on ? uon * smoothstep(0, k1, ton) : 0", slowA,
                new[] { ("ease-in seconds", 1.5f) }, U(0.9f), AOn(0.05f), AOff(4f)).AsAdvanced();

            yield return RemapPreset.Fn("Rewind while held", "Hold to play the world backwards (u > 1; u = 2 rewinds at normal speed).",
                "hold", slowU, slowA, none, U(2f), AOn(0f), AOff(3f)).AsAdvanced();

            yield return RemapPreset.Fn("Outrun yourself", "The faster you move, the slower your image: u grows with hand speed.",
                "speed > k1", "on ? min(0.95, k2 * (speed - k1)) : 0", slowA,
                new[] { ("speed threshold m/s", 0.3f), ("u per m/s", 0.4f) }, AOn(0.05f), AOff(5f), ("tracking.marker", 1)).AsAdvanced();

            // The freeze latches for k2 seconds ('on && ton < k2' keeps 'when' true), so enough delay builds up
            // for a visible burst when it releases.
            yield return RemapPreset.Fn("Hitstop punch", "A fast strike (or the Trigger button) freezes the world, then it bursts forward.",
                "speed > k1 || tap < 0.3 || (on && ton < k2)", slowU, slowA,
                new[] { ("strike speed m/s", 0.9f), ("freeze seconds", 0.35f) }, U(0.95f), AOn(0f), AOff(8f)).AsAdvanced();

            // Shapes drawn on the operator page: c1 = u over seconds since pressing, c2 = α over seconds since release.
            yield return RemapPreset.Fn("Draw your own", "Shape the slowdown and the catch-up yourself by dragging points on the two graphs (Advanced).",
                "hold", "on ? c1(ton) : 0", "on ? aon : c2(toff)", none, AOn(0.05f)).AsAdvanced();

            var loop = RemapPreset.Fn("Looping shape", "A drawn rhythm of slowdowns that repeats every k1 seconds.",
                "1", "c1(t % k1)", "c1(t % k1) > 0.5 ? aon : aoff", new[] { ("loop length s", 2f) }, AOn(0.05f), AOff(3f));
            loop.curves = new[] { new CurveData { name = "c1", xs = new[] { 0f, 0.2f, 0.5f, 0.7f, 2f }, ys = new[] { 0f, 0.95f, 0.95f, 0f, 0f } } };
            yield return loop.AsAdvanced();

            yield return RemapPreset.Fn("Stutter", "Brief freezes on a beat, like a skipping video.",
                "1", "uon * step(0.7, sin(2 * pi * k1 * t))", "aon", new[] { ("beats per second", 1.2f) }, U(0.98f), AOn(8f)).AsAdvanced();

            yield return RemapPreset.Fn("Drifting time", "Smoothly random slowdowns: time wanders.",
                "1", "uon * noise(k1 * t)", "aon", new[] { ("wander speed", 0.4f) }, U(0.8f), AOn(1f)).AsAdvanced();

            // Discontinuous counterexamples. δ settles at u/α, and a huge α gets it there within one frame, so
            // u = 1000·D, α = 1000 cuts the view straight to D seconds ago: a jump, with no slowdown or rewind between.
            yield return RemapPreset.Fn("Cut back", "Hold to cut k1 seconds into the past, release to cut back to the present: the view jumps instead of flowing.",
                "hold", "on ? 1000 * k1 : 0", "1000", new[] { ("cut back seconds", 1.5f) }).AsAdvanced();

            yield return RemapPreset.Fn("Jump cuts", "Every k1 seconds the view cuts to a random moment up to k2 seconds ago, like a film edit of your surroundings.",
                "1", "1000 * k2 * noise(floor(t / k1))", "1000", new[] { ("shot length seconds", 1.2f), ("longest cut back seconds", 1.5f) }).AsAdvanced();
        }
    }
}
