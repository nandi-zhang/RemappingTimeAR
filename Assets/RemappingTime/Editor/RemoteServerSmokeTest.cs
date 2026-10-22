using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using RemappingTime.Remote;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RemappingTime.EditorTools
{
    /// <summary>
    /// Runs the web control server in the Editor, without a headset, so the pages and protocol can be
    /// checked from a browser: <b>Remapping Time → Diagnostics → Run Web UI Smoke Test (60 s)</b>, then open
    /// http://localhost:8099/ and http://localhost:8099/operator.
    ///
    /// It serves the real pages and sends real schema/state messages built from the policy catalog, with
    /// a simulated delay signal. Client messages are logged to the Console. Nothing is applied to an
    /// engine; this checks connectivity, framing and UI, not remapping behaviour.
    /// </summary>
    public static class RemoteServerSmokeTest
    {
        public const int Port = 8099;

        [MenuItem("Remapping Time/Diagnostics/Run Web UI Smoke Test (60 s)", priority = 100)]
        public static void RunFromMenu() => Run(60);

        /// <summary>Batch mode entry point; duration from env var RT_SMOKE_SECONDS (default 45).</summary>
        public static void RunFromCommandLine()
        {
            int seconds = int.TryParse(Environment.GetEnvironmentVariable("RT_SMOKE_SECONDS"), out int s) ? s : 45;
            int received = Run(seconds);
            EditorApplication.Exit(received > 0 ? 0 : 2);
        }

        /// <summary>Serves for <paramref name="seconds"/> and returns the number of client messages received.</summary>
        public static int Run(int seconds)
        {
            var remotePage = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/RemappingTime/Resources/RemappingTime/remote.html");
            var operatorPage = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/RemappingTime/Resources/RemappingTime/operator.html");
            byte[] remote = remotePage.bytes, op = operatorPage.bytes;

            var server = new MiniWebServer
            {
                Http = (string path, out string type, out byte[] body) =>
                {
                    type = "text/html; charset=utf-8";
                    string p = path.TrimEnd('/');
                    body = p == "" ? remote : p == "/operator" ? op : null;
                    return body != null;
                }
            };
            if (!server.Start(Port)) return 0;
            Debug.Log($"[RemappingTime] Smoke test serving http://localhost:{Port}/ for {seconds}s");

            List<RemapPolicy> policies = PolicyCatalog.CreateAll();
            RemapPolicy active = policies[0];
            int received = 0;
            float hold = 0f;
            var clock = Stopwatch.StartNew();
            long nextState = 0;

            try
            {
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    while (server.TryDequeue(out MiniWebServer.Event e))
                    {
                        if (e.Kind == MiniWebServer.EventKind.Message)
                        {
                            received++;
                            Debug.Log($"[RemappingTime] smoke rx from {e.ClientId}: {e.Text}");
                            var m = JsonUtility.FromJson<ClientMessage>(e.Text);
                            if (m.type == "hello") server.Send(e.ClientId, JsonUtility.ToJson(Schema(policies, active, m.role)));
                            if (m.type == "input") hold = m.hold;
                            if (m.type == "policy") { active = policies.Find(p => p.Id == m.id) ?? active; server.Broadcast(JsonUtility.ToJson(Schema(policies, active, "operator"))); }
                            server.Send(e.ClientId, JsonUtility.ToJson(new NoticeMessage { level = "info", text = "ack " + m.type }));
                        }
                    }

                    if (clock.ElapsedMilliseconds >= nextState)
                    {
                        nextState = clock.ElapsedMilliseconds + 66;
                        float t = (float)clock.Elapsed.TotalSeconds;
                        float delay = 0.6f * (0.5f + 0.5f * Mathf.Sin(t * 0.8f)) + hold * 0.4f;
                        var values = new List<TunableValue>();
                        foreach (Tunable tu in active.Params) values.Add(new TunableValue { id = tu.Id, v = tu.Value });
                        server.Broadcast(JsonUtility.ToJson(new StateMessage
                        {
                            t = t, delay = delay, rate = 1f - 0.4f * Mathf.Cos(t * 0.8f), u = hold * 0.8f, alpha = 0.05f,
                            phase = hold > 0 ? "slowing" : "live", policy = active.Id, preset = "smoke test", maxDelay = 2f,
                            budget = 2f, fps = 60f, hold = hold, remoteHold = hold, scrub = 0f, tracked = true, speed = 0.4f,
                            source = "simulated", battery = 0.8f, clients = server.ClientCount, status = "smoke test",
                            values = values.ToArray(),
                        }));
                    }
                    Thread.Sleep(5);
                }
            }
            finally
            {
                server.Stop();
            }
            Debug.Log($"[RemappingTime] Smoke test finished: {received} client messages received.");
            return received;
        }

        private static SchemaMessage Schema(List<RemapPolicy> policies, RemapPolicy active, string role)
        {
            var infos = new List<PolicyInfo>();
            foreach (RemapPolicy p in policies) infos.Add(new PolicyInfo { id = p.Id, name = p.DisplayName, description = p.Description });
            var tunables = new List<TunableInfo>();
            foreach (Tunable t in active.Params)
                tunables.Add(new TunableInfo
                {
                    id = t.Id, label = t.Label, group = t.Group, unit = t.Unit, help = t.Help, min = t.Min, max = t.Max,
                    step = t.Step, value = t.Value, def = t.Default, options = t.Options ?? Array.Empty<string>(),
                });
            return new SchemaMessage
            {
                role = role == "operator" ? "operator" : "remote", device = "Editor", url = $"http://localhost:{Port}/",
                budget = 2f, activePolicy = active.Id, activePreset = "smoke test", policies = infos.ToArray(),
                tunables = tunables.ToArray(), presets = new[] { "Hold to slow", "Study replica" },
                actions = new[] { "live", "trigger" },
            };
        }
    }
}
