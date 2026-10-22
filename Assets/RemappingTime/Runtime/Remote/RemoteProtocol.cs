using System;

namespace RemappingTime.Remote
{
    // JSON messages exchanged with web clients. Kept flat and JsonUtility-friendly.
    // See Docs/PROTOCOL.md for the full description with examples.

    /// <summary>Any message from a client. Only the fields relevant to its <see cref="type"/> are used.</summary>
    [Serializable]
    public sealed class ClientMessage
    {
        /// <summary>"hello" | "input" | "action" | "set" | "policy" | "preset" | "savePreset" | "deletePreset" | "expr" | "knobLabel" | "curve"</summary>
        public string type;
        public string role;   // hello: "operator" or "remote"
        public string pin;    // hello: operator PIN, if one is configured
        public string id;     // set: tunable id; policy: policy id; expr: "when" | "u" | "alpha"; knobLabel: "1".."3"
        public float value;   // set
        public float hold;    // input: 0..1
        public string name;   // action name, preset name, formula text (expr) or knob name (knobLabel)
        public float[] xs, ys; // curve: the new points (id = "c1" | "c2")
    }

    /// <summary>A drawable curve (c1 / c2) and the range its editor shows.</summary>
    [Serializable]
    public sealed class CurveInfo
    {
        public string name, label;
        public float xMax, yMin, yMax;
        public float[] xs, ys;
        public bool used;        // whether the current formulas call it
    }

    [Serializable]
    public sealed class PolicyInfo
    {
        public string id;
        public string name;
        public string description;
    }

    /// <summary>The active time function: its formulas, and the vocabulary they can use.</summary>
    [Serializable]
    public sealed class FunctionInfo
    {
        public string when, u, alpha;
        public string[] variables, variableHelp, functions;
        // What the formulas read, so pages can show the right controls (e.g. a hold button only if 'hold' is used).
        public bool usesHold, usesTrigger, usesWater, usesMotion;
        public CurveInfo[] curves;
    }

    /// <summary>One hand (or held controller) as seen by the motion tracking.</summary>
    [Serializable]
    public sealed class HandState
    {
        public bool tracked;
        public bool inView;
        public float speed;      // m/s
        public string device;    // "left hand", "right controller", … or "none"
    }

    [Serializable]
    public sealed class TunableInfo
    {
        public string id, label, group, unit, help;
        public float min, max, step, value, def;
        public string[] options;
    }

    /// <summary>Sent on connect and whenever the set of policies, tunables or presets changes.</summary>
    [Serializable]
    public sealed class SchemaMessage
    {
        public string type = "schema";
        public string role;
        public bool pinRequired;
        public string device;
        public string url;
        public float budget;
        public string activePolicy;
        public string activePreset;
        public PolicyInfo[] policies;
        public TunableInfo[] tunables;
        public string[] presets;
        public string[] presetDescriptions;
        public string[] presetCategories;   // per preset: "main", "advanced" or "saved"
        public FunctionInfo function;
        public string[] actions;
    }

    [Serializable]
    public sealed class TunableValue
    {
        public string id;
        public float v;
    }

    /// <summary>Live telemetry, broadcast several times per second.</summary>
    [Serializable]
    public sealed class StateMessage
    {
        public string type = "state";
        public float t;          // physical time (s)
        public float delay;      // δ (s)
        public float rate;       // measured playback rate
        public float u;
        public float alpha;
        public string phase;
        public string policy;
        public string preset;
        public float maxDelay;   // currently reachable delay (s)
        public float budget;     // buffer budget B (s)
        public float fps;        // camera capture rate
        public float hold;       // combined hold input
        public float remoteHold;
        public bool bypass;
        public bool tracked;
        public bool inView;      // tracked point inside the cameras' view (where remapping is visible)
        public float speed;      // speed of the point driving policies (m/s)
        public string source;    // which device that is
        public HandState left;
        public HandState right;
        public float battery;    // 0..1, or -1 if unknown
        public int clients;
        public string status;
        public bool fnOn;        // time function trigger state, and seconds since it turned on / off
        public float fnTon, fnToff;
        public string diag;      // live diagnostics from components, one "key: text" per line
        public TunableValue[] values;
    }

    [Serializable]
    public sealed class NoticeMessage
    {
        public string type = "notice";
        public string level;     // "info" | "error"
        public string text;
    }
}
