using System;
using System.Collections.Generic;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// Design u(t) and α(t) as formulas, live.
    ///
    /// <para>Three expressions (see <see cref="Expression"/> for the syntax):</para>
    /// <list type="bullet">
    /// <item><b>when</b>: a trigger condition. While it is true, <c>on</c> = 1 and <c>ton</c> counts the
    /// seconds since it turned on; after it turns off, <c>toff</c> counts the seconds since.</item>
    /// <item><b>u</b>: the injection rate. 0 = real time, 0.8 = 5× slower, 1 = frozen, &gt;1 = rewind.</item>
    /// <item><b>α</b>: the recovery rate, how strongly the delay is pulled back to zero (catch-up ≈ 3/α seconds).</item>
    /// </list>
    /// <para>Three knobs <c>k1 k2 k3</c> (0–10) can be used inside the formulas and tuned from the headset HUD or
    /// the web page without retyping anything. See <see cref="VariableHelp"/> for every available variable.</para>
    ///
    /// <para>Example, "hold to slow":<br/>
    /// when = <c>hold</c>, u = <c>on ? k1 : 0</c>, α = <c>on ? 0.05 : k2</c> with k1 = 0.8, k2 = 5.</para>
    /// </summary>
    public sealed class FunctionPolicy : RemapPolicy
    {
        public override string Id => "fn";
        public override string DisplayName => "Time function";
        public override string Description =>
            "Type u(t) and α(t) as formulas. 'when' is the trigger; while it holds, on = 1 and ton counts up. " +
            "u adds delay (0.8 = 5× slower, 1 = freeze, >1 = rewind); α pulls it back (catch-up ≈ 3/α s). " +
            "Draw shapes c1(x) and c2(x) on the graphs to use custom curves.";

        /// <summary>Variables usable in the formulas, in slot order.</summary>
        public static readonly string[] Variables =
        {
            "t", "d", "B", "hold", "scrub", "speed", "lspeed", "rspeed", "depth", "inzone", "on", "ton", "toff", "tap", "k1", "k2", "k3",
            "uon", "aon", "aoff",
        };

        public static readonly string[] VariableHelp =
        {
            "time in seconds",
            "current delay δ: how far behind real time the view is (s)",
            "buffer budget: the largest possible delay (s)",
            "hold input 0–1 (right trigger, Space on the web page, or a phone)",
            "scrub input −1…1 (right thumbstick or web slider)",
            "speed of the fastest hand inside the cameras' view (m/s; 0 if none)",
            "left hand speed, inside the view (m/s)",
            "right hand speed, inside the view (m/s)",
            "how deep a hand is inside the water (m; 0 outside)",
            "1 while a hand is inside the water",
            "1 while 'when' is true",
            "seconds since 'when' became true (0 while off)",
            "seconds since 'when' became false (0 while on)",
            "seconds since the last Trigger press (A button / web)",
            "self-defined variable 1 (0–10)", "self-defined variable 2 (0–10)", "self-defined variable 3 (0–10)",
            "u when triggered (slider)", "α when triggered (slider)", "α when released (slider)",
        };

        private const int T = 0, D = 1, Budget = 2, Hold = 3, Scrub = 4, Speed = 5, LSpeed = 6, RSpeed = 7,
                          Depth = 8, InZone = 9, On = 10, TOn = 11, TOff = 12, Tap = 13, K1 = 14, K2 = 15, K3 = 16,
                          UOn = 17, AOn = 18, AOff = 19;

        /// <summary>Self-defined variables k1–k3: free numbers a preset names and uses inside its formulas.</summary>
        public readonly Tunable Knob1, Knob2, Knob3;

        /// <summary>
        /// The three basic constants most designs need, shown as plain sliders: u while triggered, and α while
        /// triggered / after release. Formulas use them as <c>uon</c>, <c>aon</c>, <c>aoff</c>.
        /// </summary>
        public readonly Tunable UWhenOn, AlphaWhenOn, AlphaWhenOff;

        /// <summary>UI group of the basic sliders, and of the self-defined variables.</summary>
        public const string BasicGroup = "Time", VariablesGroup = "Self-defined";

        /// <summary>
        /// Two drawable shapes, callable in formulas as <c>c1(x)</c> and <c>c2(x)</c>: by convention c1 shapes u
        /// (0–2) and c2 shapes α (0–20), each over 0–4 s (e.g. <c>c1(ton)</c>, <c>c2(toff)</c>).
        /// </summary>
        public readonly Curve Curve1 = new Curve("c1", "u shape", 4f, 0f, 2f);
        public readonly Curve Curve2 = new Curve("c2", "α shape", 4f, 0f, 20f);
        public Curve[] Curves => new[] { Curve1, Curve2 };

        /// <summary>Current trigger state, for UIs (e.g. a playhead on the shape editor).</summary>
        public bool IsOn => _on;
        public float TimeOn => _vars[TOn];
        public float TimeOff => _vars[TOff];

        private readonly Dictionary<string, Func<float, float>> _userFunctions;

        public string WhenSource { get; private set; }
        public string USource { get; private set; }
        public string AlphaSource { get; private set; }

        private Expression _when, _u, _alpha;
        private readonly float[] _vars = new float[Variables.Length];
        private bool _on;
        private double _changedAt;
        private double _lastTap = double.NegativeInfinity;
        private bool _hasTime;

        /// <summary>Raised when an expression changes (UIs refresh the formulas they show).</summary>
        public event Action ExpressionsChanged;

        public FunctionPolicy()
        {
            _userFunctions = new Dictionary<string, Func<float, float>>
            {
                ["c1"] = Curve1.Evaluate,
                ["c2"] = Curve2.Evaluate,
            };
            ResetCurves();
            Curve1.Changed += () => ExpressionsChanged?.Invoke();
            Curve2.Changed += () => ExpressionsChanged?.Invoke();
            UWhenOn = Range("uon", "u when triggered", 0f, 2f, 0.8f, 0.01f, "",
                "How fast delay builds while triggered: 0.8 = five times slower, 1 = freeze, above 1 = rewind.");
            AlphaWhenOn = Range("aon", "α when triggered", 0f, 20f, 0.05f, 0.01f, "",
                "How strongly time is pulled back toward real time while triggered. Small = the slowdown lasts.");
            AlphaWhenOff = Range("aoff", "α when released", 0f, 20f, 5f, 0.05f, "",
                "How fast it catches up after release: catch-up takes about 3/α seconds (5 ≈ 0.6 s).");
            Knob1 = Range("k1", "k1", 0f, 10f, 0.8f, 0.01f);
            Knob2 = Range("k2", "k2", 0f, 10f, 5f, 0.01f);
            Knob3 = Range("k3", "k3", 0f, 10f, 1f, 0.01f);
            foreach (Tunable t in new[] { UWhenOn, AlphaWhenOn, AlphaWhenOff }) t.Group = BasicGroup;
            foreach (Tunable t in new[] { Knob1, Knob2, Knob3 }) t.Group = VariablesGroup;
            SetExpressions("hold", "on ? uon : 0", "on ? aon : aoff");
        }

        public override bool UsesZone => Reads(Depth) || Reads(InZone);
        public bool UsesHold => Reads(Hold);
        public bool UsesScrub => Reads(Scrub);
        public bool UsesTap => Reads(Tap);
        public bool UsesMotion => Reads(Speed) || Reads(LSpeed) || Reads(RSpeed);
        public bool UsesCurve(string name) => (_when?.Calls(name) ?? false) || (_u?.Calls(name) ?? false) || (_alpha?.Calls(name) ?? false);

        private bool Reads(int slot) => (_when?.Uses(slot) ?? false) || (_u?.Uses(slot) ?? false) || (_alpha?.Uses(slot) ?? false);

        /// <summary>Sets all three expressions; any that fail to compile keep their previous version.</summary>
        public bool SetExpressions(string when, string u, string alpha)
        {
            bool ok = SetExpression("when", when, out _) & SetExpression("u", u, out _) & SetExpression("alpha", alpha, out _);
            return ok;
        }

        /// <summary>
        /// Compiles and installs one expression ("when", "u" or "alpha"). On error the previous expression stays
        /// active and <paramref name="error"/> explains the problem.
        /// </summary>
        public bool SetExpression(string which, string source, out string error)
        {
            source = (source ?? "").Trim();
            if (!Expression.TryCompile(source, Variables, out Expression expr, out error, _userFunctions)) return false;
            switch (which)
            {
                case "when": _when = expr; WhenSource = source; break;
                case "u": _u = expr; USource = source; break;
                case "alpha": case "α": _alpha = expr; AlphaSource = source; break;
                default: error = $"unknown formula '{which}'"; return false;
            }
            // Only show the knobs the formulas actually use.
            Knob1.Hidden = !Reads(K1);
            Knob2.Hidden = !Reads(K2);
            Knob3.Hidden = !Reads(K3);
            UWhenOn.Hidden = !Reads(UOn);
            AlphaWhenOn.Hidden = !Reads(AOn);
            AlphaWhenOff.Hidden = !Reads(AOff);
            ExpressionsChanged?.Invoke();
            return true;
        }

        /// <summary>Default shapes: c1 eases into 0.9 and back out; c2 starts gentle and strengthens.</summary>
        public void ResetCurves()
        {
            Curve1.SetPoints(new[] { 0f, 0.25f, 1.5f, 2.5f }, new[] { 0f, 0.9f, 0.9f, 0.4f });
            Curve2.SetPoints(new[] { 0f, 0.4f, 4f }, new[] { 1f, 8f, 8f });
        }

        public Curve FindCurve(string name) => name == "c1" ? Curve1 : name == "c2" ? Curve2 : null;

        public override void Reset()
        {
            _on = false;
            _hasTime = false;
            _lastTap = double.NegativeInfinity;
        }

        public override RemapCommand Evaluate(in RemapContext ctx)
        {
            if (!_hasTime) { _changedAt = ctx.Time - 1000.0; _hasTime = true; }
            if (ctx.Inputs.Action) _lastTap = ctx.Time;

            _vars[T] = (float)ctx.Time;
            _vars[D] = (float)ctx.Delay;
            _vars[Budget] = ctx.MaxDelay;
            _vars[Hold] = ctx.Inputs.Hold;
            _vars[Scrub] = ctx.Inputs.Scrub;
            _vars[Speed] = SpeedInView(ctx.Motion);
            _vars[LSpeed] = SpeedInView(ctx.Left);
            _vars[RSpeed] = SpeedInView(ctx.Right);
            _vars[Depth] = ctx.InZone ? ctx.ZoneDepth : 0f;
            _vars[InZone] = ctx.InZone ? 1f : 0f;
            _vars[Tap] = (float)Math.Min(1000.0, ctx.Time - _lastTap);
            _vars[K1] = Knob1.Value;
            _vars[K2] = Knob2.Value;
            _vars[K3] = Knob3.Value;
            _vars[UOn] = UWhenOn.Value;
            _vars[AOn] = AlphaWhenOn.Value;
            _vars[AOff] = AlphaWhenOff.Value;

            // The trigger sees the previous on/ton/toff (so 'when' can refer to them, e.g. for latching).
            float elapsed = (float)Math.Min(1000.0, ctx.Time - _changedAt);
            _vars[On] = _on ? 1f : 0f;
            _vars[TOn] = _on ? elapsed : 0f;
            _vars[TOff] = _on ? 0f : elapsed;

            bool on = _when != null && _when.Evaluate(_vars) > 0.5f;
            if (on != _on) { _on = on; _changedAt = ctx.Time; elapsed = 0f; }
            _vars[On] = _on ? 1f : 0f;
            _vars[TOn] = _on ? elapsed : 0f;
            _vars[TOff] = _on ? 0f : elapsed;

            float u = _u != null ? _u.Evaluate(_vars) : 0f;
            float alpha = _alpha != null ? _alpha.Evaluate(_vars) : 5f;
            string phase = _on ? "on" : ctx.Delay > 0.01 ? "catching up" : "idle";
            return new RemapCommand(u, Mathf.Max(0f, alpha), phase);
        }

        /// <summary>Motion outside the cameras' view counts as no motion: its remapping could not be seen.</summary>
        private static float SpeedInView(MotionSample s) => s.Tracked && s.InView ? s.Speed : 0f;
    }
}
