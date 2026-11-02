using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <para>Three self-defined variables <c>k1 k2 k3</c> are formulas of their own (a number such as <c>1.5</c>, or
    /// anything like <c>0.5 + 0.3 * sin(t)</c>) that when, u and α can use. A variable can use the inputs and the
    /// variables before it (k2 can use k1). See <see cref="VariableHelp"/> for every available variable.</para>
    ///
    /// <para>Example, "gradual brake":<br/>
    /// when = <c>hold</c>, u = <c>on ? uon * smoothstep(0, k1, ton) : 0</c>, α = <c>on ? aon : aoff</c> with k1 = 1.5.</para>
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
            "t", "d", "B", "hold", "speed", "lspeed", "rspeed", "depth", "inzone", "on", "ton", "toff", "tap", "k1", "k2", "k3",
            "uon", "aon", "aoff",
        };

        public static readonly string[] VariableHelp =
        {
            "time in seconds",
            "current delay δ: how far behind real time the view is (s)",
            "buffer budget: the largest possible delay (s)",
            "hold input 0–1 (right trigger, Space on the web page, or a phone)",
            "speed of the fastest hand inside the cameras' view (m/s; 0 if none)",
            "left hand speed, inside the view (m/s)",
            "right hand speed, inside the view (m/s)",
            "how deep a hand is inside the water (m; 0 outside)",
            "1 while a hand is inside the water",
            "1 while 'when' is true",
            "seconds since 'when' became true (0 while off)",
            "seconds since 'when' became false (0 while on)",
            "seconds since the last Tap (A button / web)",
            "self-defined variable 1 (its own formula)", "self-defined variable 2 (its own formula; can use k1)",
            "self-defined variable 3 (its own formula; can use k1, k2)",
            "u when triggered (slider)", "α when triggered (slider)", "α when released (slider)",
        };

        private const int T = 0, D = 1, Budget = 2, Hold = 3, Speed = 4, LSpeed = 5, RSpeed = 6,
                          Depth = 7, InZone = 8, On = 9, TOn = 10, TOff = 11, Tap = 12, K1 = 13, K2 = 14, K3 = 15,
                          UOn = 16, AOn = 17, AOff = 18;

        /// <summary>The formula a self-defined variable starts with.</summary>
        public const string DefaultVariable = "1";

        /// <summary>
        /// The three basic constants most designs need, shown as plain sliders: u while triggered, and α while
        /// triggered / after release. Formulas use them as <c>uon</c>, <c>aon</c>, <c>aoff</c>.
        /// </summary>
        public readonly Tunable UWhenOn, AlphaWhenOn, AlphaWhenOff;

        /// <summary>UI group of the basic sliders.</summary>
        public const string BasicGroup = "Time";

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
        private readonly Expression[] _k = new Expression[3];
        private readonly string[] _kSources = { DefaultVariable, DefaultVariable, DefaultVariable };
        private readonly string[] _kNames = { "", "", "" };
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
                "Delay added while triggered. 1 = freeze, above 1 = rewind.");
            AlphaWhenOn = Range("aon", "α when triggered", 0f, 20f, 0.05f, 0.01f, "",
                "Pull back toward the present while triggered.");
            AlphaWhenOff = Range("aoff", "α when released", 0f, 20f, 5f, 0.05f, "",
                "Pull back after release. Catching up takes about 3/α seconds.");
            foreach (Tunable t in new[] { UWhenOn, AlphaWhenOn, AlphaWhenOff }) t.Group = BasicGroup;
            for (int i = 0; i < 3; i++) Expression.TryCompile(DefaultVariable, Variables, out _k[i], out _);
            SetExpressions("hold", "on ? uon : 0", "on ? aon : aoff");
        }

        public override bool UsesZone => Reads(Depth) || Reads(InZone);
        public bool UsesHold => Reads(Hold);
        public bool UsesTap => Reads(Tap);
        public bool UsesMotion => Reads(Speed) || Reads(LSpeed) || Reads(RSpeed);

        public bool UsesCurve(string name)
        {
            if (CallsDeep(_when, name) || CallsDeep(_u, name) || CallsDeep(_alpha, name)) return true;
            return false;
        }

        /// <summary>True while when, u or α use self-defined variable k<paramref name="index"/> (1–3), directly or through another one.</summary>
        public bool UsesVariable(int index) => index >= 1 && index <= 3 && Reads(K1 + index - 1);

        /// <summary>The formula of self-defined variable k<paramref name="index"/> (1–3).</summary>
        public string VariableSource(int index) => index >= 1 && index <= 3 ? _kSources[index - 1] : "";

        /// <summary>The name a preset gives self-defined variable k<paramref name="index"/> (1–3), or "".</summary>
        public string VariableName(int index) => index >= 1 && index <= 3 ? _kNames[index - 1] : "";

        /// <summary>Names a self-defined variable (shown as "k1 (name)"); "" removes the name.</summary>
        public void SetVariableName(int index, string name)
        {
            if (index < 1 || index > 3) return;
            name = System.Text.RegularExpressions.Regex.Replace(name ?? "", @"[<>\p{Cc}]", "");
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
            _kNames[index - 1] = name.Length > 40 ? name.Substring(0, 40).Trim() : name;
            ExpressionsChanged?.Invoke();
        }

        /// <summary>Reads <paramref name="slot"/> in when, u or α, also through the self-defined variables they use.</summary>
        private bool Reads(int slot) => UsesDeep(_when, slot) || UsesDeep(_u, slot) || UsesDeep(_alpha, slot);
        private bool WhenReads(int slot) => UsesDeep(_when, slot);

        private bool UsesDeep(Expression e, int slot)
        {
            if (e == null) return false;
            if (e.Uses(slot)) return true;
            for (int i = 0; i < 3; i++)
                if (_k[i] != null && e.Uses(K1 + i) && UsesDeep(_k[i], slot)) return true;
            return false;
        }

        private bool CallsDeep(Expression e, string fn)
        {
            if (e == null) return false;
            if (e.Calls(fn)) return true;
            for (int i = 0; i < 3; i++)
                if (_k[i] != null && e.Uses(K1 + i) && CallsDeep(_k[i], fn)) return true;
            return false;
        }

        /// <summary>
        /// What starts the remapping, in plain words, derived from the 'when' formula (shown on the HUD and the
        /// operator page): e.g. "hold the right trigger", "move a hand fast or press A", "always on".
        /// </summary>
        public string TriggerHint
        {
            get
            {
                var ways = new List<string>();
                if (WhenReads(Hold)) ways.Add("hold the right trigger");
                if (WhenReads(Speed) || WhenReads(LSpeed) || WhenReads(RSpeed)) ways.Add("move a hand fast");
                if (WhenReads(Depth) || WhenReads(InZone)) ways.Add("put a hand in the water");
                if (WhenReads(Tap)) ways.Add("press A");
                if (ways.Count > 0) return string.Join(" or ", ways);
                if (_when == null) return "never";
                // No user input: a constant ("1" / "0") or a function of time.
                bool timed = WhenReads(T) || WhenReads(D) || WhenReads(On) || WhenReads(TOn) || WhenReads(TOff);
                if (timed) return "automatic";
                var probe = new float[Variables.Length];
                EvaluateVariables(probe);
                return _when.Evaluate(probe) > 0.5f ? "always on" : "never (live)";
            }
        }

        /// <summary>Sets all three expressions; any that fail to compile keep their previous version.</summary>
        public bool SetExpressions(string when, string u, string alpha)
        {
            bool ok = SetExpression("when", when, out _) & SetExpression("u", u, out _) & SetExpression("alpha", alpha, out _);
            return ok;
        }

        /// <summary>
        /// Compiles and installs one expression ("when", "u", "alpha", or a self-defined variable "k1", "k2", "k3").
        /// On error the previous expression stays active and <paramref name="error"/> explains the problem.
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
                case "k1": case "k2": case "k3":
                {
                    int i = which[1] - '1';
                    // A variable can use the ones before it, never itself or later ones (no loops).
                    for (int j = i; j < 3; j++)
                        if (expr.Uses(K1 + j))
                        {
                            error = i == 0 ? "k1 can't use k1, k2 or k3" : $"k{i + 1} can only use {(i == 1 ? "k1" : "k1 and k2")}";
                            return false;
                        }
                    _k[i] = expr; _kSources[i] = source;
                    break;
                }
                default: error = $"unknown formula '{which}'"; return false;
            }
            // Mark the sliders no formula reads (the web page dims them, the headset menu leaves them out).
            UWhenOn.Unused = !Reads(UOn);
            AlphaWhenOn.Unused = !Reads(AOn);
            AlphaWhenOff.Unused = !Reads(AOff);
            ExpressionsChanged?.Invoke();
            return true;
        }

        /// <summary>Sets self-defined variable k<paramref name="index"/> (1–3) to a plain number.</summary>
        public void SetVariableValue(int index, float value)
        {
            if (index < 1 || index > 3 || float.IsNaN(value) || float.IsInfinity(value)) return;
            SetExpression("k" + index, value.ToString("0.###", CultureInfo.InvariantCulture), out _);
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

        private void EvaluateVariables(float[] vars)
        {
            for (int i = 0; i < 3; i++) vars[K1 + i] = _k[i] != null ? _k[i].Evaluate(vars) : 0f;
        }

        public override RemapCommand Evaluate(in RemapContext ctx)
        {
            if (!_hasTime) { _changedAt = ctx.Time - 1000.0; _hasTime = true; }
            if (ctx.Inputs.Action) _lastTap = ctx.Time;

            _vars[T] = (float)ctx.Time;
            _vars[D] = (float)ctx.Delay;
            _vars[Budget] = ctx.MaxDelay;
            _vars[Hold] = ctx.Inputs.Hold;
            _vars[Speed] = SpeedInView(ctx.Motion);
            _vars[LSpeed] = SpeedInView(ctx.Left);
            _vars[RSpeed] = SpeedInView(ctx.Right);
            _vars[Depth] = ctx.InZone ? ctx.ZoneDepth : 0f;
            _vars[InZone] = ctx.InZone ? 1f : 0f;
            _vars[Tap] = (float)Math.Min(1000.0, ctx.Time - _lastTap);
            _vars[UOn] = UWhenOn.Value;
            _vars[AOn] = AlphaWhenOn.Value;
            _vars[AOff] = AlphaWhenOff.Value;

            // The trigger sees the previous on/ton/toff (so 'when' can refer to them, e.g. for latching).
            float elapsed = (float)Math.Min(1000.0, ctx.Time - _changedAt);
            _vars[On] = _on ? 1f : 0f;
            _vars[TOn] = _on ? elapsed : 0f;
            _vars[TOff] = _on ? 0f : elapsed;
            EvaluateVariables(_vars);

            bool on = _when != null && _when.Evaluate(_vars) > 0.5f;
            if (on != _on) { _on = on; _changedAt = ctx.Time; elapsed = 0f; }
            _vars[On] = _on ? 1f : 0f;
            _vars[TOn] = _on ? elapsed : 0f;
            _vars[TOff] = _on ? 0f : elapsed;
            EvaluateVariables(_vars); // again, so u and α see variables that depend on the new on/ton/toff

            float u = _u != null ? _u.Evaluate(_vars) : 0f;
            float alpha = _alpha != null ? _alpha.Evaluate(_vars) : 5f;
            string phase = _on ? "on" : ctx.Delay > 0.01 ? "catching up" : "idle";
            return new RemapCommand(u, Mathf.Max(0f, alpha), phase);
        }

        /// <summary>Motion outside the cameras' view counts as no motion: its remapping could not be seen.</summary>
        private static float SpeedInView(MotionSample s) => s.Tracked && s.InView ? s.Speed : 0f;
    }
}
