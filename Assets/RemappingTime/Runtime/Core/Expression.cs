using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemappingTime
{
    /// <summary>
    /// A tiny math expression language for designing u(t) and α(t) live, e.g.
    /// <code>on ? k1 * smoothstep(0, 0.3, ton) : 0</code>
    ///
    /// <para><b>Syntax.</b> Numbers, variables, <c>+ - * / % ^</c>, comparisons <c>&lt; &lt;= &gt; &gt;= == !=</c>
    /// (giving 1 or 0), logic <c>&amp;&amp; || !</c> (also <c>and or not</c>), the conditional <c>c ? a : b</c>,
    /// parentheses, and the functions listed in <see cref="FunctionNames"/>. A value counts as <i>true</i>
    /// when it is greater than 0.5, so a half-pressed trigger is "off" and comparisons work as expected.</para>
    ///
    /// <para><b>Use.</b> Compile once with <see cref="TryCompile"/> (variables are resolved to slots at compile
    /// time), then call <see cref="Evaluate"/> every frame with the current variable values. Evaluation does not
    /// allocate.</para>
    /// </summary>
    public sealed class Expression
    {
        public static readonly string[] FunctionNames =
        {
            "sin", "cos", "tan", "abs", "sqrt", "exp", "log", "floor", "ceil", "round", "sign", "sat",
            "min", "max", "pow", "step", "clamp", "lerp", "smoothstep", "noise",
        };

        public string Source { get; }
        private readonly Node _root;

        private Expression(string source, Node root) { Source = source; _root = root; }

        /// <summary>Evaluates with the given variable values (indexed as in the variable list used to compile).</summary>
        public float Evaluate(float[] vars)
        {
            float v = _root.Eval(vars);
            return float.IsNaN(v) || float.IsInfinity(v) ? 0f : v;
        }

        /// <summary>True if the expression reads the named variable (e.g. to know whether a policy needs the water zone).</summary>
        public bool Uses(int variableIndex) => _root.Uses(variableIndex);

        /// <summary>True if the expression calls the named user function (e.g. a drawn curve).</summary>
        public bool Calls(string userFunction) => _root.Calls(userFunction);

        /// <summary>Compiles <paramref name="source"/>. On failure returns false and a short, human-readable error.</summary>
        /// <param name="userFunctions">Optional one-argument functions available by name (e.g. drawn curves "c1", "c2").</param>
        public static bool TryCompile(string source, IReadOnlyList<string> variables, out Expression expr, out string error,
                                      IReadOnlyDictionary<string, Func<float, float>> userFunctions = null)
        {
            expr = null;
            error = null;
            try
            {
                var p = new Parser(source ?? "", variables, userFunctions);
                Node root = p.ParseAll();
                expr = new Expression(source, root);
                return true;
            }
            catch (FormatException e)
            {
                error = e.Message;
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // AST
        // ─────────────────────────────────────────────────────────────────────

        private abstract class Node
        {
            public abstract float Eval(float[] v);
            public virtual bool Uses(int index) => false;
            public virtual bool Calls(string name) => false;
        }

        private static bool Truthy(float x) => x > 0.5f;

        private sealed class Const : Node
        {
            private readonly float _value;
            public Const(float value) { _value = value; }
            public override float Eval(float[] v) => _value;
        }

        private sealed class Var : Node
        {
            private readonly int _index;
            public Var(int index) { _index = index; }
            public override float Eval(float[] v) => v[_index];
            public override bool Uses(int index) => index == _index;
        }

        private sealed class Unary : Node
        {
            private readonly char _op;
            private readonly Node _a;
            public Unary(char op, Node a) { _op = op; _a = a; }
            public override float Eval(float[] v) => _op == '-' ? -_a.Eval(v) : (Truthy(_a.Eval(v)) ? 0f : 1f);
            public override bool Uses(int index) => _a.Uses(index);
            public override bool Calls(string name) => _a.Calls(name);
        }

        private sealed class Binary : Node
        {
            private readonly string _op;
            private readonly Node _a, _b;
            public Binary(string op, Node a, Node b) { _op = op; _a = a; _b = b; }

            public override float Eval(float[] v)
            {
                switch (_op)
                {
                    case "&&": return Truthy(_a.Eval(v)) && Truthy(_b.Eval(v)) ? 1f : 0f;
                    case "||": return Truthy(_a.Eval(v)) || Truthy(_b.Eval(v)) ? 1f : 0f;
                }
                float a = _a.Eval(v), b = _b.Eval(v);
                switch (_op)
                {
                    case "+": return a + b;
                    case "-": return a - b;
                    case "*": return a * b;
                    case "/": return b == 0f ? 0f : a / b;
                    case "%": return b == 0f ? 0f : a - b * (float)Math.Floor(a / b);
                    case "^": return (float)Math.Pow(a, b);
                    case "<": return a < b ? 1f : 0f;
                    case "<=": return a <= b ? 1f : 0f;
                    case ">": return a > b ? 1f : 0f;
                    case ">=": return a >= b ? 1f : 0f;
                    case "==": return Math.Abs(a - b) < 1e-6f ? 1f : 0f;
                    case "!=": return Math.Abs(a - b) >= 1e-6f ? 1f : 0f;
                    default: return 0f;
                }
            }

            public override bool Uses(int index) => _a.Uses(index) || _b.Uses(index);
            public override bool Calls(string name) => _a.Calls(name) || _b.Calls(name);
        }

        private sealed class Conditional : Node
        {
            private readonly Node _c, _a, _b;
            public Conditional(Node c, Node a, Node b) { _c = c; _a = a; _b = b; }
            public override float Eval(float[] v) => Truthy(_c.Eval(v)) ? _a.Eval(v) : _b.Eval(v);
            public override bool Uses(int index) => _c.Uses(index) || _a.Uses(index) || _b.Uses(index);
            public override bool Calls(string name) => _c.Calls(name) || _a.Calls(name) || _b.Calls(name);
        }

        /// <summary>A call to a user-supplied one-argument function (e.g. a drawn curve).</summary>
        private sealed class UserCall : Node
        {
            private readonly string _name;
            private readonly Func<float, float> _fn;
            private readonly Node _arg;
            public UserCall(string name, Func<float, float> fn, Node arg) { _name = name; _fn = fn; _arg = arg; }
            public override float Eval(float[] v) => _fn(_arg.Eval(v));
            public override bool Uses(int index) => _arg.Uses(index);
            public override bool Calls(string name) => string.Equals(name, _name, StringComparison.OrdinalIgnoreCase) || _arg.Calls(name);
        }

        private sealed class Call : Node
        {
            private readonly string _name;
            private readonly Node[] _args;
            public Call(string name, Node[] args) { _name = name; _args = args; }

            public override bool Uses(int index)
            {
                foreach (Node a in _args) if (a.Uses(index)) return true;
                return false;
            }

            public override bool Calls(string name)
            {
                foreach (Node a in _args) if (a.Calls(name)) return true;
                return false;
            }

            public override float Eval(float[] v)
            {
                float A(int i) => _args[i].Eval(v);
                switch (_name)
                {
                    case "sin": return (float)Math.Sin(A(0));
                    case "cos": return (float)Math.Cos(A(0));
                    case "tan": return (float)Math.Tan(A(0));
                    case "abs": return Math.Abs(A(0));
                    case "sqrt": return (float)Math.Sqrt(Math.Max(0f, A(0)));
                    case "exp": return (float)Math.Exp(A(0));
                    case "log": { float x = A(0); return x > 0f ? (float)Math.Log(x) : 0f; }
                    case "floor": return (float)Math.Floor(A(0));
                    case "ceil": return (float)Math.Ceiling(A(0));
                    case "round": return (float)Math.Round(A(0));
                    case "sign": return Math.Sign(A(0));
                    case "sat": return Clamp(A(0), 0f, 1f);
                    case "min": return Math.Min(A(0), A(1));
                    case "max": return Math.Max(A(0), A(1));
                    case "pow": return (float)Math.Pow(A(0), A(1));
                    case "step": return A(1) >= A(0) ? 1f : 0f;
                    case "clamp": return Clamp(A(0), A(1), A(2));
                    case "lerp": { float a = A(0), b = A(1); return a + (b - a) * A(2); }
                    case "smoothstep":
                    {
                        float e0 = A(0), e1 = A(1);
                        float t = e1 == e0 ? (A(2) >= e1 ? 1f : 0f) : Clamp((A(2) - e0) / (e1 - e0), 0f, 1f);
                        return t * t * (3f - 2f * t);
                    }
                    case "noise": return Noise(A(0));
                    default: return 0f;
                }
            }

            private static float Clamp(float x, float a, float b) => x < a ? a : x > b ? b : x;

            /// <summary>Smooth 1D value noise in [0, 1].</summary>
            private static float Noise(float x)
            {
                float i = (float)Math.Floor(x), f = x - i;
                float u = f * f * (3f - 2f * f);
                return Hash(i) + (Hash(i + 1f) - Hash(i)) * u;
            }

            private static float Hash(float n)
            {
                double s = Math.Sin(n * 127.1) * 43758.5453;
                return (float)(s - Math.Floor(s));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Recursive-descent parser
        // ─────────────────────────────────────────────────────────────────────

        private sealed class Parser
        {
            private static readonly Dictionary<string, int> Arity = new Dictionary<string, int>
            {
                ["sin"] = 1, ["cos"] = 1, ["tan"] = 1, ["abs"] = 1, ["sqrt"] = 1, ["exp"] = 1, ["log"] = 1,
                ["floor"] = 1, ["ceil"] = 1, ["round"] = 1, ["sign"] = 1, ["sat"] = 1, ["noise"] = 1,
                ["min"] = 2, ["max"] = 2, ["pow"] = 2, ["step"] = 2,
                ["clamp"] = 3, ["lerp"] = 3, ["smoothstep"] = 3,
            };

            private readonly string _s;
            private readonly IReadOnlyList<string> _vars;
            private readonly IReadOnlyDictionary<string, Func<float, float>> _userFunctions;
            private int _i;

            public Parser(string s, IReadOnlyList<string> vars, IReadOnlyDictionary<string, Func<float, float>> userFunctions)
            {
                _s = s; _vars = vars; _userFunctions = userFunctions;
            }

            public Node ParseAll()
            {
                SkipWs();
                if (_i >= _s.Length) throw Error("empty expression");
                Node n = ParseConditional();
                SkipWs();
                if (_i < _s.Length) throw Error($"unexpected '{_s[_i]}'");
                return n;
            }

            private Node ParseConditional()
            {
                Node c = ParseOr();
                if (Accept("?"))
                {
                    Node a = ParseConditional();
                    Expect(":");
                    Node b = ParseConditional();
                    return new Conditional(c, a, b);
                }
                return c;
            }

            private Node ParseOr()
            {
                Node n = ParseAnd();
                while (Accept("||") || AcceptWord("or")) n = new Binary("||", n, ParseAnd());
                return n;
            }

            private Node ParseAnd()
            {
                Node n = ParseComparison();
                while (Accept("&&") || AcceptWord("and")) n = new Binary("&&", n, ParseComparison());
                return n;
            }

            private Node ParseComparison()
            {
                Node n = ParseAdditive();
                foreach (string op in new[] { "<=", ">=", "==", "!=", "<", ">" })
                    if (Accept(op)) return new Binary(op, n, ParseAdditive());
                return n;
            }

            private Node ParseAdditive()
            {
                Node n = ParseMultiplicative();
                while (true)
                {
                    if (Accept("+")) n = new Binary("+", n, ParseMultiplicative());
                    else if (Accept("-")) n = new Binary("-", n, ParseMultiplicative());
                    else return n;
                }
            }

            private Node ParseMultiplicative()
            {
                Node n = ParseUnary();
                while (true)
                {
                    if (Accept("*")) n = new Binary("*", n, ParseUnary());
                    else if (Accept("/")) n = new Binary("/", n, ParseUnary());
                    else if (Accept("%")) n = new Binary("%", n, ParseUnary());
                    else return n;
                }
            }

            private Node ParseUnary()
            {
                if (Accept("-")) return new Unary('-', ParseUnary());
                if (Accept("+")) return ParseUnary();
                if (AcceptNot()) return new Unary('!', ParseUnary());
                return ParsePower();
            }

            private Node ParsePower()
            {
                Node b = ParsePrimary();
                if (Accept("^")) return new Binary("^", b, ParseUnary()); // right-associative
                return b;
            }

            private Node ParsePrimary()
            {
                SkipWs();
                if (_i >= _s.Length) throw Error("expression ends too early");
                char ch = _s[_i];

                if (Accept("(")) { Node n = ParseConditional(); Expect(")"); return n; }

                if (char.IsDigit(ch) || ch == '.')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                    if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                    {
                        int save = _i++;
                        if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                        if (_i < _s.Length && char.IsDigit(_s[_i])) while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                        else _i = save;
                    }
                    string num = _s.Substring(start, _i - start);
                    if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                        throw Error($"bad number '{num}'", start);
                    return new Const(value);
                }

                if (char.IsLetter(ch) || ch == '_' || ch == 'α')
                {
                    int start = _i;
                    while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == 'α')) _i++;
                    string name = _s.Substring(start, _i - start);
                    string lower = name.ToLowerInvariant();

                    if (lower == "pi" || name == "π") return new Const((float)Math.PI);
                    if (lower == "e") return new Const((float)Math.E);
                    if (lower == "true") return new Const(1f);
                    if (lower == "false") return new Const(0f);

                    SkipWs();
                    if (_i < _s.Length && _s[_i] == '(')
                    {
                        if (_userFunctions != null && _userFunctions.TryGetValue(lower, out Func<float, float> user))
                        {
                            _i++;
                            Node arg = ParseConditional();
                            Expect(")");
                            return new UserCall(lower, user, arg);
                        }
                        if (!Arity.TryGetValue(lower, out int arity)) throw Error($"unknown function '{name}'", start);
                        _i++;
                        var args = new List<Node>();
                        SkipWs();
                        if (!Accept(")"))
                        {
                            do args.Add(ParseConditional()); while (Accept(","));
                            Expect(")");
                        }
                        if (args.Count != arity) throw Error($"{name}() takes {arity} argument{(arity == 1 ? "" : "s")}", start);
                        return new Call(lower, args.ToArray());
                    }

                    for (int k = 0; k < _vars.Count; k++)
                        if (string.Equals(_vars[k], name, StringComparison.OrdinalIgnoreCase)) return new Var(k);
                    throw Error($"unknown name '{name}'", start);
                }

                throw Error($"unexpected '{ch}'");
            }

            private void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

            private bool Accept(string token)
            {
                SkipWs();
                if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0) return false;
                // Don't read "<" out of "<=" etc.
                if ((token == "<" || token == ">") && _i + 1 < _s.Length && _s[_i + 1] == '=') return false;
                _i += token.Length;
                return true;
            }

            private bool AcceptNot()
            {
                SkipWs();
                if (_i < _s.Length && _s[_i] == '!' && !(_i + 1 < _s.Length && _s[_i + 1] == '=')) { _i++; return true; }
                return AcceptWord("not");
            }

            private bool AcceptWord(string word)
            {
                SkipWs();
                if (string.Compare(_s, _i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
                int end = _i + word.Length;
                if (end < _s.Length && (char.IsLetterOrDigit(_s[end]) || _s[end] == '_')) return false;
                _i = end;
                return true;
            }

            private void Expect(string token)
            {
                if (!Accept(token)) throw Error($"expected '{token}'");
            }

            private FormatException Error(string message, int at = -1)
                => new FormatException($"{message} (at {(at >= 0 ? at : _i) + 1})");
        }
    }
}
