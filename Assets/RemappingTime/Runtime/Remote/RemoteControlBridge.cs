using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RemappingTime.Remote
{
    /// <summary>
    /// Connects <see cref="TimeRemapEngine"/> to browsers over Wi-Fi.
    ///
    /// Two pages are served from the headset:
    /// <list type="bullet">
    /// <item><c>http://&lt;headset-ip&gt;:8080/</c>: the <b>remote</b>. A big hold button (and a Trigger button
    /// when the formulas use it), so someone else can slow the wearer's world from a phone.
    /// Remote clients can only send inputs and fire pulses.</item>
    /// <item><c>http://&lt;headset-ip&gt;:8080/operator</c>: the <b>operator</b> console. Pick policies and presets,
    /// tune every parameter, and watch live telemetry (δ, r, u, α). Protected by an optional PIN.</item>
    /// </list>
    /// All phones/laptops must be on the same network as the headset. Some networks (campus, public Wi-Fi) block
    /// devices from reaching each other; a home router or a phone hotspot avoids that.
    /// </summary>
    public sealed class RemoteControlBridge : MonoBehaviour
    {
        [SerializeField] private TimeRemapEngine engine;
        [SerializeField] private int port = 8080;
        [Tooltip("If set, the operator page asks for this PIN before allowing configuration changes.")]
        [SerializeField] private string operatorPin = "";
        [SerializeField, Range(2f, 30f)] private float stateRateHz = 15f;
        [SerializeField] private TextAsset remotePage;
        [SerializeField] private TextAsset operatorPage;

        private enum Role { Remote, Operator }

        private static readonly string[] OperatorActions = { "live", "trigger", "moveWater", "resetKnobs" };
        private static readonly string[] RemoteActions = { "trigger" };

        private readonly MiniWebServer _server = new MiniWebServer();
        private readonly Dictionary<int, Role> _roles = new Dictionary<int, Role>();
        private readonly List<Tunable> _tunableCache = new List<Tunable>();
        private byte[] _remoteHtml, _operatorHtml;
        private bool _schemaDirty = true;
        private float _nextStateTime;

        public string Url { get; private set; } = "";
        public int ClientCount => _roles.Count;

        private void Start()
        {
            if (engine == null) engine = FindFirstObjectByType<TimeRemapEngine>();
            if (remotePage == null) remotePage = Resources.Load<TextAsset>("RemappingTime/remote");
            if (operatorPage == null) operatorPage = Resources.Load<TextAsset>("RemappingTime/operator");
            _remoteHtml = remotePage != null ? remotePage.bytes : Encoding.UTF8.GetBytes("remote.html missing");
            _operatorHtml = operatorPage != null ? operatorPage.bytes : Encoding.UTF8.GetBytes("operator.html missing");

            _server.Http = Route;
            if (_server.Start(port))
            {
                Url = $"http://{NetworkInfo.LocalIPv4()}:{port}/";
                Debug.Log($"[RemappingTime] Web control at {Url} (operator: {Url}operator)");
            }
            else
            {
                Url = "web server failed to start";
            }

            engine.ControlUrl = Url;
            engine.SchemaChanged += MarkSchemaDirty;
        }

        private void OnDestroy()
        {
            if (engine != null) engine.SchemaChanged -= MarkSchemaDirty;
            _server.Stop();
        }

        private void MarkSchemaDirty() => _schemaDirty = true;

        // Runs on server worker threads: only touches immutable byte arrays.
        private bool Route(string path, out string contentType, out byte[] body)
        {
            contentType = "text/html; charset=utf-8";
            switch (path.TrimEnd('/').ToLowerInvariant())
            {
                case "":
                case "/remote":
                    body = _remoteHtml;
                    return true;
                case "/operator":
                    body = _operatorHtml;
                    return true;
                default:
                    body = null;
                    return false;
            }
        }

        private void Update()
        {
            while (_server.TryDequeue(out MiniWebServer.Event e))
            {
                switch (e.Kind)
                {
                    case MiniWebServer.EventKind.Connected:
                        _roles[e.ClientId] = Role.Remote;
                        break;
                    case MiniWebServer.EventKind.Disconnected:
                        _roles.Remove(e.ClientId);
                        engine.Inputs?.RemoveRemoteClient(e.ClientId);
                        break;
                    case MiniWebServer.EventKind.Message:
                        Handle(e.ClientId, e.Text);
                        break;
                }
            }

            engine.WebClients = _roles.Count;

            if (_schemaDirty)
            {
                _schemaDirty = false;
                foreach (KeyValuePair<int, Role> kv in _roles) SendSchema(kv.Key, kv.Value);
            }

            if (_roles.Count > 0 && Time.unscaledTime >= _nextStateTime)
            {
                _nextStateTime = Time.unscaledTime + 1f / stateRateHz;
                _server.Broadcast(JsonUtility.ToJson(BuildState()));
            }
        }

        private void Handle(int client, string text)
        {
            ClientMessage m;
            try { m = JsonUtility.FromJson<ClientMessage>(text); }
            catch (Exception) { return; }
            if (m == null || string.IsNullOrEmpty(m.type)) return;

            Role role = _roles.TryGetValue(client, out Role r) ? r : Role.Remote;
            bool isOperator = role == Role.Operator;

            switch (m.type)
            {
                case "hello":
                    if (m.role == "operator")
                    {
                        if (string.IsNullOrEmpty(operatorPin) || m.pin == operatorPin) role = Role.Operator;
                        else Notice(client, "error", "Wrong PIN: connected as remote only.");
                    }
                    else role = Role.Remote;
                    _roles[client] = role;
                    SendSchema(client, role);
                    break;

                case "input":
                    engine.Inputs?.SetRemoteInput(client, m.hold);
                    break;

                case "action":
                    if (Array.IndexOf(isOperator ? OperatorActions : RemoteActions, m.name) < 0) return;
                    RunAction(m.name);
                    break;

                case "set":
                    if (isOperator && !engine.SetTunable(m.id, m.value)) Notice(client, "error", $"Unknown parameter '{m.id}'.");
                    break;

                case "policy":
                    if (isOperator) engine.SelectPolicy(m.id);
                    break;

                case "preset":
                    if (isOperator && !engine.ApplyPreset(m.name)) Notice(client, "error", $"No preset '{m.name}'.");
                    break;

                case "savePreset":
                    if (isOperator) { engine.SavePreset(m.name); Notice(client, "info", $"Saved preset '{m.name}'."); }
                    break;

                case "deletePreset":
                    if (isOperator) engine.DeletePreset(m.name);
                    break;

                case "expr":
                    if (!isOperator) return;
                    if (engine.SetExpression(m.id, m.name, out string error)) Notice(client, "info", $"{FormulaName(m.id)} = {m.name}");
                    else { Notice(client, "error", $"{FormulaName(m.id)}: {error}"); _schemaDirty = true; }
                    break;

                case "curve":
                    if (isOperator && !engine.SetCurve(m.id, m.xs, m.ys)) Notice(client, "error", $"Could not set curve '{m.id}'.");
                    break;

                case "knobLabel":
                    if (isOperator && int.TryParse(m.id, out int knob)) engine.SetKnobLabel(knob, m.name);
                    break;
            }
        }

        private static string FormulaName(string id) => id == "alpha" ? "α" : id;

        private void RunAction(string name)
        {
            switch (name)
            {
                case "trigger": engine.Inputs?.QueueAction(); break;
                case "live": engine.Inputs?.QueueSnapToLive(); break;
                case "moveWater": engine.MoveWaterInFront(); break;
                case "resetKnobs": engine.ResetPolicyParams(); break;
            }
        }

        private void Notice(int client, string level, string text)
            => _server.Send(client, JsonUtility.ToJson(new NoticeMessage { level = level, text = text }));

        private void SendSchema(int client, Role role)
        {
            var policies = new List<PolicyInfo>();
            foreach (RemapPolicy p in engine.Policies)
                policies.Add(new PolicyInfo { id = p.Id, name = p.DisplayName, description = p.Description });

            FunctionInfo function = null;
            if (engine.Function != null)
            {
                FunctionPolicy fn = engine.Function;
                function = new FunctionInfo
                {
                    when = fn.WhenSource, u = fn.USource, alpha = fn.AlphaSource,
                    variables = FunctionPolicy.Variables, variableHelp = FunctionPolicy.VariableHelp,
                    functions = Expression.FunctionNames,
                    usesHold = fn.UsesHold, usesTrigger = fn.UsesTap,
                    usesWater = fn.UsesZone, usesMotion = fn.UsesMotion,
                    curves = Array.ConvertAll(fn.Curves, c => new CurveInfo
                    {
                        name = c.Name, label = c.Label, xMax = c.XMax, yMin = c.YMin, yMax = c.YMax,
                        xs = c.Xs, ys = c.Ys, used = fn.UsesCurve(c.Name),
                    }),
                };
                function.functions = new List<string>(Expression.FunctionNames) { "c1", "c2" }.ToArray();
            }

            var presetDescriptions = new string[engine.Presets.All.Count];
            var presetCategories = new string[presetDescriptions.Length];
            for (int i = 0; i < presetDescriptions.Length; i++)
            {
                presetDescriptions[i] = engine.Presets.All[i].description ?? "";
                presetCategories[i] = engine.Presets.All[i].Category;
            }

            var tunables = new List<TunableInfo>();
            foreach (Tunable t in engine.AllTunables())
            {
                if (t.Hidden) continue;
                tunables.Add(new TunableInfo
                {
                    id = t.Id, label = t.Label, group = t.Group, unit = t.Unit, help = t.Help,
                    min = t.Min, max = t.Max, step = t.Step, value = t.Value, def = t.Default,
                    options = t.Options ?? Array.Empty<string>(),
                });
            }

            var schema = new SchemaMessage
            {
                role = role == Role.Operator ? "operator" : "remote",
                pinRequired = !string.IsNullOrEmpty(operatorPin),
                device = SystemInfo.deviceModel,
                url = Url,
                budget = engine.BudgetSeconds,
                activePolicy = engine.ActivePolicy?.Id,
                activePreset = engine.ActivePresetName,
                policies = policies.ToArray(),
                tunables = tunables.ToArray(),
                presets = engine.Presets.Names(),
                presetDescriptions = presetDescriptions,
                presetCategories = presetCategories,
                function = function,
                actions = role == Role.Operator ? OperatorActions : RemoteActions,
            };
            _server.Send(client, JsonUtility.ToJson(schema));
        }

        private static HandState Hand(MotionSample s, string device) => new HandState
        {
            tracked = s.Tracked, inView = s.InView, speed = s.Speed, device = s.Tracked ? device ?? "" : "none",
        };

        private StateMessage BuildState()
        {
            RemapChannel ch = engine.Main;
            RemapContext ctx = engine.LastContext;

            _tunableCache.Clear();
            _tunableCache.AddRange(engine.AllTunables());
            var values = new TunableValue[_tunableCache.Count];
            for (int i = 0; i < values.Length; i++) values[i] = new TunableValue { id = _tunableCache[i].Id, v = _tunableCache[i].Value };

            return new StateMessage
            {
                t = (float)ctx.Time,
                delay = (float)ch.Delay,
                rate = ch.Rate,
                u = ch.U,
                alpha = ch.Alpha,
                phase = ch.Phase,
                policy = engine.ActivePolicy?.Id,
                preset = engine.ActivePresetName,
                maxDelay = ctx.MaxDelay,
                budget = engine.BudgetSeconds,
                fps = engine.Capture != null ? engine.Capture.CaptureFps : 0f,
                hold = ctx.Inputs.Hold,
                remoteHold = ctx.Inputs.RemoteHold,
                bypass = engine.Inputs != null && engine.Inputs.Bypass,
                tracked = ctx.Motion.Tracked,
                inView = ctx.Motion.InView,
                speed = ctx.Motion.Speed,
                source = engine.Tracking != null ? engine.Tracking.ActiveSourceName : "none",
                left = Hand(engine.Tracking != null ? engine.Tracking.LeftSample : default, engine.Tracking?.LeftDevice),
                right = Hand(engine.Tracking != null ? engine.Tracking.RightSample : default, engine.Tracking?.RightDevice),
                battery = SystemInfo.batteryLevel,
                clients = _roles.Count,
                status = engine.Capture != null ? engine.Capture.Status : "",
                diag = engine.DiagnosticsText(),
                fnOn = engine.Function != null && engine.Function.IsOn,
                fnTon = engine.Function != null ? engine.Function.TimeOn : 0f,
                fnToff = engine.Function != null ? engine.Function.TimeOff : 0f,
                values = values,
            };
        }
    }
}
