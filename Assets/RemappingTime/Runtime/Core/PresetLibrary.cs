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
    /// A named configuration: the time function (formulas for when / u / α, with names for its knobs) plus
    /// values for any tunables (knobs, view, tracking, …). Values that are not listed are reset to their
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
        /// <summary>Display names for knobs k1, k2, k3 (empty = unused).</summary>
        public string[] knobs = Array.Empty<string>();
        public ParamValue[] values = Array.Empty<ParamValue>();
        /// <summary>Drawn curves; empty = the default shapes.</summary>
        public CurveData[] curves = Array.Empty<CurveData>();
        public bool builtIn;

        public RemapPreset() { }

        /// <summary>A built-in time-function preset. <paramref name="knobs"/> are (label, value) for k1..k3.</summary>
        public static RemapPreset Fn(string name, string description, string when, string u, string alpha,
                                     (string label, float value)[] knobs, params (string id, float v)[] values)
        {
            var p = new RemapPreset
            {
                name = name, description = description, when = when, u = u, alpha = alpha, builtIn = true,
                knobs = new string[knobs.Length],
            };
            var list = new List<ParamValue>();
            for (int k = 0; k < knobs.Length; k++)
            {
                p.knobs[k] = knobs[k].label;
                list.Add(new ParamValue($"fn.k{k + 1}", knobs[k].value));
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

        /// <summary>Saves (or overwrites) a user preset and persists all user presets to disk.</summary>
        public void Save(RemapPreset preset)
        {
            preset.builtIn = false;
            int i = _presets.FindIndex(p => string.Equals(p.name, preset.name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (_presets[i].builtIn) preset.name += " (custom)";
                else { _presets[i] = preset; Persist(); Changed?.Invoke(); return; }
            }
            _presets.Add(preset);
            Persist();
            Changed?.Invoke();
        }

        public bool Delete(string name)
        {
            int i = _presets.FindIndex(p => p.name == name && !p.builtIn);
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

        /// <summary>Installs a preset's formulas and knob names on a time-function policy.</summary>
        public static void ApplyFunction(RemapPreset preset, FunctionPolicy fn)
        {
            if (!string.IsNullOrEmpty(preset.u) || !string.IsNullOrEmpty(preset.when))
                fn.SetExpressions(preset.when ?? "0", preset.u ?? "0", preset.alpha ?? "5");
            fn.ResetCurves();
            if (preset.curves != null)
                foreach (CurveData c in preset.curves) fn.FindCurve(c.name)?.SetPoints(c.xs, c.ys);
            Tunable[] knobs = { fn.Knob1, fn.Knob2, fn.Knob3 };
            for (int k = 0; k < knobs.Length; k++)
            {
                string label = preset.knobs != null && k < preset.knobs.Length ? preset.knobs[k] : "";
                knobs[k].Label = string.IsNullOrEmpty(label) ? $"k{k + 1} (unused)" : $"k{k + 1} · {label}";
            }
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
                    p.builtIn = false;
                    if (Find(p.name) == null) _presets.Add(p);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemappingTime] Could not read presets: {e.Message}");
            }
        }

        private void Persist()
        {
            try
            {
                var file = new PresetFile { presets = _presets.FindAll(p => !p.builtIn).ToArray() };
                File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemappingTime] Could not save presets: {e.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Built-in presets: each is a pair of formulas for u(t) and α(t) (plus a trigger), i.e. one point in
        // the design space of time remapping. Knobs k1..k3 expose the numbers worth tuning live.
        // ─────────────────────────────────────────────────────────────────────
        private static IEnumerable<RemapPreset> BuiltIns()
        {
            var catchUp = ("catch-up α", 5f);

            yield return RemapPreset.Fn("Live", "Real time. Any leftover delay is caught up.",
                "0", "0", "k2", new[] { ("", 0f), catchUp });

            yield return RemapPreset.Fn("Hold to slow", "Hold the trigger (or Space) to slow time; release to fast-forward back.",
                "hold", "on ? k1 : 0", "on ? 0.05 : k2", new[] { ("slowdown u", 0.8f), catchUp });

            // Analog option: a half-pressed trigger (or the web strength slider) gives half the slowdown.
            yield return RemapPreset.Fn("Hold to slow (analog)", "Squeeze the trigger harder for slower time; release to fast-forward back.",
                "hold > 0.05", "on ? k1 * hold : 0", "on ? 0.05 : k2", new[] { ("slowdown u at full press", 0.8f), catchUp });

            yield return RemapPreset.Fn("Bullet time", "Very slow while held, then a long dramatic fast-forward.",
                "hold", "on ? k1 : 0", "on ? 0.02 : k2", new[] { ("slowdown u", 0.92f), ("catch-up α", 3f) });

            yield return RemapPreset.Fn("Freeze frame", "Hold to freeze the world (u = 1).",
                "hold", "on ? 1 : 0", "on ? 0 : k2", new[] { ("", 0f), catchUp });

            yield return RemapPreset.Fn("Gradual brake", "Time eases into slow motion over k3 seconds instead of switching at once.",
                "hold", "on ? k1 * smoothstep(0, k3, ton) : 0", "on ? 0.05 : k2",
                new[] { ("slowdown u", 0.9f), catchUp, ("ease-in s", 1.5f) });

            yield return RemapPreset.Fn("Rewind while held", "Hold to play the world backwards (u > 1).",
                "hold", "on ? 1 + k1 : 0", "on ? 0 : k2", new[] { ("rewind speed ×", 1f), ("catch-up α", 3f) });

            yield return RemapPreset.Fn("Referee replay", "Hold to pause; push the stick or web slider left to rewind, right to play.",
                "hold || abs(scrub) > 0.15", "on ? 1 - k1 * scrub : 0", "on ? 0 : k2",
                new[] { ("scrub speed ×", 1.5f), ("catch-up α", 3f) }, ("view.distance", 1.8f));

            yield return RemapPreset.Fn("Motion slow-mo", "Fast hand motion (in view) slows the world; it recovers when you stop.",
                "speed > k3", "on || toff < 0.15 ? k1 : 0", "on || toff < 0.15 ? 0.05 : k2",
                new[] { ("slowdown u", 0.75f), catchUp, ("speed threshold m/s", 0.6f) });

            yield return RemapPreset.Fn("Outrun yourself", "The faster you move, the slower your image: u grows with hand speed.",
                "speed > k3", "on ? min(0.95, k1 * (speed - k3)) : 0", "on ? 0.05 : k2",
                new[] { ("gain per m/s", 0.4f), catchUp, ("speed threshold m/s", 0.3f) }, ("tracking.marker", 1));

            // The freeze latches for k1 seconds ('on && ton < k1' keeps 'when' true), so enough delay builds up
            // for a visible burst when it releases.
            yield return RemapPreset.Fn("Hitstop punch", "A fast strike (or the Trigger button) freezes the world, then it bursts forward.",
                "speed > k3 || tap < 0.3 || (on && ton < k1)", "on ? 0.95 : 0", "on ? 0 : k2",
                new[] { ("freeze s", 0.35f), ("burst α", 8f), ("strike speed m/s", 0.9f) });

            yield return RemapPreset.Fn("Water", "Moving a hand through the water slows time with depth: a pseudo-haptic 'thick' material.",
                "inzone", "on ? k1 * sat(depth / 0.03) : 0", "on ? k3 : k2",
                new[] { ("thickness u", 0.85f), ("release α", 6f), ("settle α inside", 1.5f) },
                ("view.distance", 0.6f), ("water.look", 0));

            yield return RemapPreset.Fn("Honey", "A much thicker material: deep, slow, and slow to let go.",
                "inzone", "on ? k1 * sat(depth / 0.03) : 0", "on ? k3 : k2",
                new[] { ("thickness u", 0.95f), ("release α", 3f), ("settle α inside", 0.6f) },
                ("view.distance", 0.6f), ("water.look", 1));

            // Shapes drawn on the operator page: c1 = u over seconds since pressing, c2 = α over seconds since release.
            yield return RemapPreset.Fn("Draw your own", "Shape the slowdown and the catch-up yourself by dragging points on the two graphs.",
                "hold", "on ? c1(ton) : 0", "on ? 0.05 : c2(toff)", Array.Empty<(string, float)>());

            var loop = RemapPreset.Fn("Looping shape", "A drawn rhythm of slowdowns that repeats every k1 seconds.",
                "1", "c1(t % k1)", "c1(t % k1) > 0.5 ? 0.05 : k2", new[] { ("loop length s", 2f), ("recovery α between beats", 3f) });
            loop.curves = new[] { new CurveData { name = "c1", xs = new[] { 0f, 0.2f, 0.5f, 0.7f, 2f }, ys = new[] { 0f, 0.95f, 0.95f, 0f, 0f } } };
            yield return loop;

            yield return RemapPreset.Fn("Time wobble", "u follows a sine wave, so time breathes slower and faster around real time.",
                "1", "max(0, k1 + 0.6 * sin(2 * pi * k3 * t))", "k2",
                new[] { ("base u", 0.3f), ("recovery α", 1.5f), ("frequency Hz", 0.5f) });

            yield return RemapPreset.Fn("Stutter", "Brief freezes on a beat, like a skipping video.",
                "1", "0.98 * step(k1, sin(2 * pi * k3 * t))", "k2",
                new[] { ("freeze share", 0.7f), ("recovery α", 8f), ("beats per s", 1.2f) });

            yield return RemapPreset.Fn("Drifting time", "Smoothly random slowdowns: time wanders.",
                "1", "k1 * noise(k3 * t)", "k2", new[] { ("drift u", 0.8f), ("recovery α", 1f), ("wander speed", 0.4f) });

            yield return RemapPreset.Fn("Constant u, α", "Fixed inputs settle into a constant lag (δ → u/α) at normal speed.",
                "0", "k1", "k2", new[] { ("u", 0.3f), ("α", 1f) });

            yield return RemapPreset.Fn("Echo trails", "Hold to slow, with earlier moments trailing behind moving things.",
                "hold", "on ? k1 : 0", "on ? 0.05 : k2", new[] { ("slowdown u", 0.8f), catchUp },
                ("view.echo", 1), ("view.echoCount", 4), ("view.echoSpacing", 0.07f), ("view.echoStrength", 0.5f));

            yield return RemapPreset.Fn("Time lens", "Only a circle around your hand is remapped.",
                "hold", "on ? k1 : 0", "on ? 0.05 : k2", new[] { ("slowdown u", 0.8f), catchUp },
                ("view.scope", 1), ("view.lensAnchor", 0), ("view.lensRadius", 0.22f), ("view.hideLive", 0));

            yield return RemapPreset.Fn("Ghost", "The slowed world appears as a ghost over the live one.",
                "hold", "on ? k1 : 0", "on ? 0.05 : k2", new[] { ("slowdown u", 0.8f), catchUp },
                ("view.opacity", 0.55f), ("view.hideLive", 0));
        }
    }
}
