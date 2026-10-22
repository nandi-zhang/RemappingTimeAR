using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// A named, bounded parameter that can be tuned at runtime from the in-headset HUD or the web
    /// controller. Anything declared as a Tunable appears automatically in both UIs, so adding a knob
    /// to a policy or view only means declaring it.
    ///
    /// A Tunable is either a continuous range (<see cref="Min"/>..<see cref="Max"/>) or a discrete choice
    /// (<see cref="Options"/> != null), in which case <see cref="Value"/> holds the option index.
    /// Toggles are choices with options {"Off", "On"}.
    /// </summary>
    public sealed class Tunable
    {
        /// <summary>Stable identifier used by presets and the network protocol, e.g. "hold.speed".</summary>
        public readonly string Id;
        /// <summary>Display name. Settable so presets can name generic knobs (e.g. "k1 · slowdown").</summary>
        public string Label { get; set; }
        /// <summary>UI grouping, e.g. "Policy", "View", "Tracking", "Engine".</summary>
        public string Group;
        public readonly string Unit;
        public readonly string Help;
        public readonly float Min, Max, Step, Default;
        public readonly string[] Options;

        /// <summary>Raised after the value changes (from any source).</summary>
        public event Action<Tunable> Changed;

        private float _value;

        private Tunable(string id, string label, float min, float max, float step, float def,
                        string unit, string help, string[] options)
        {
            Id = id; Label = label; Min = min; Max = max; Step = step; Default = def;
            Unit = unit ?? ""; Help = help ?? ""; Options = options;
            _value = Sanitize(def);
        }

        public static Tunable Range(string id, string label, float min, float max, float def,
                                    float step = 0.01f, string unit = "", string help = "")
            => new Tunable(id, label, min, max, step, def, unit, help, null);

        public static Tunable Choice(string id, string label, string[] options, int def = 0, string help = "")
            => new Tunable(id, label, 0, options.Length - 1, 1, def, "", help, options);

        public static Tunable Toggle(string id, string label, bool def, string help = "")
            => Choice(id, label, new[] { "Off", "On" }, def ? 1 : 0, help);

        /// <summary>Hidden tunables keep working but are left out of the HUD and web UI (e.g. knobs no formula uses).</summary>
        public bool Hidden { get; set; }

        public bool IsChoice => Options != null;
        public bool IsToggle => Options != null && Options.Length == 2 && Options[0] == "Off" && Options[1] == "On";

        public float Value
        {
            get => _value;
            set
            {
                float v = Sanitize(value);
                if (Mathf.Approximately(v, _value)) return;
                _value = v;
                Changed?.Invoke(this);
            }
        }

        public int Index => Mathf.RoundToInt(_value);
        public bool On => _value > 0.5f;

        /// <summary>Moves the value by <paramref name="steps"/> increments (choices wrap around).</summary>
        public void Nudge(int steps)
        {
            if (IsChoice)
            {
                int n = Options.Length;
                Value = ((Index + steps) % n + n) % n;
            }
            else
            {
                Value = _value + steps * Step;
            }
        }

        public void ResetToDefault() => Value = Default;

        public string FormatValue()
        {
            if (IsChoice) return Options[Mathf.Clamp(Index, 0, Options.Length - 1)];
            int decimals = Step >= 1f ? 0 : Step >= 0.1f ? 1 : Step >= 0.01f ? 2 : 3;
            return _value.ToString("F" + decimals) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);
        }

        private float Sanitize(float v)
        {
            if (float.IsNaN(v)) v = Default;
            v = Mathf.Clamp(v, Min, Max);
            if (IsChoice) v = Mathf.Round(v);
            return v;
        }
    }

    /// <summary>An ordered collection of <see cref="Tunable"/>s owned by one component.</summary>
    public sealed class TunableSet : IEnumerable<Tunable>
    {
        private readonly List<Tunable> _items = new List<Tunable>();
        private readonly string _group;

        public TunableSet(string group) { _group = group; }

        public Tunable Add(Tunable t)
        {
            if (string.IsNullOrEmpty(t.Group)) t.Group = _group;
            _items.Add(t);
            return t;
        }

        /// <summary>Inserts at a display position (used when a choice's options must be rebuilt).</summary>
        public Tunable Insert(int index, Tunable t)
        {
            if (string.IsNullOrEmpty(t.Group)) t.Group = _group;
            _items.Insert(Mathf.Clamp(index, 0, _items.Count), t);
            return t;
        }

        public bool Remove(Tunable t) => t != null && _items.Remove(t);

        public Tunable Find(string id) => _items.Find(t => t.Id == id);
        public int Count => _items.Count;

        public IEnumerator<Tunable> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
