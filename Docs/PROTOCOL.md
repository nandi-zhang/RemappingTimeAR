# Web control protocol

The headset app listens on TCP port **8080** (configurable on `RemoteControlBridge`).

- `GET /` serves the remote page and `GET /operator` serves the operator page.
- Any request with `Upgrade: websocket` (e.g. `ws://<headset-ip>:8080/ws`) opens a WebSocket.

All WebSocket messages are UTF-8 JSON text frames with a `type` field. Unknown fields are ignored.

## Roles

A client starts as a **remote**. Send `hello` to choose a role:

```json
{ "type": "hello", "role": "operator", "pin": "1234" }
```

If the bridge has an operator PIN and it does not match, the client stays a remote and gets a `notice`.

| Message | remote | operator |
|---|:-:|:-:|
| `hello`, `input` | ✓ | ✓ |
| `action` `trigger` | ✓ | ✓ |
| `action` `live`, `moveWater`, `resetKnobs` | | ✓ |
| `set`, `policy`, `preset`, `savePreset`, `deletePreset`, `expr`, `knobLabel` | | ✓ |

## Client → headset

| type | fields | effect |
|---|---|---|
| `hello` | `role`, `pin` | Choose a role; the reply is a `schema`. |
| `input` | `hold` 0..1 | This client's hold. **Resend at ≥ 5 Hz while active**: inputs expire after 0.6 s without updates, and on disconnect. The strongest hold across clients wins. |
| `action` | `name` | `trigger` (same as the A button: resets `tap`), `live` (snap to live), `moveWater` (water block in front of the wearer), `resetKnobs`. |
| `set` | `id`, `value` | Set a tunable (see `schema.tunables`). Choices take the option index; toggles take 0/1. |
| `expr` | `id`, `name` | Set a formula: `id` = `when`, `u` or `alpha`, `name` = the formula text. Replies with a `notice`: `"u = …"` when applied, `"u: <error>"` when not (the previous formula stays). |
| `knobLabel` | `id`, `name` | Rename knob `id` = `1`, `2` or `3`. |
| `policy` | `id` | Switch policy (only relevant if custom policies are registered; the built-in one is `fn`). |
| `preset` | `name` | Apply a preset. |
| `savePreset` | `name` | Save the current policy + policy/view values as a user preset. |
| `deletePreset` | `name` | Delete a user preset. Built-in presets cannot be deleted. |

## Headset → client

**`schema`**: sent after `hello` and whenever policies, tunables or presets change.

```json
{
  "type": "schema", "role": "operator", "pinRequired": true, "device": "Oculus Quest",
  "url": "http://192.168.1.23:8080/", "budget": 2.0,
  "activePolicy": "hold", "activePreset": "Hold to slow",
  "policies": [ { "id": "fn", "name": "Time function", "description": "…" } ],
  "function": { "when": "hold", "u": "on ? uon : 0", "alpha": "on ? aon : aoff",
                "variables": [ "t", "d", "…" ], "variableHelp": [ "…" ], "functions": [ "sin", "…" ],
                "usesHold": true, "usesTrigger": false, "usesWater": false, "usesMotion": false },
  "tunables": [ { "id": "fn.k1", "label": "k1 · slowdown u", "group": "Policy", "unit": "",
                  "help": "…", "min": 0, "max": 10, "step": 0.01, "value": 0.8, "def": 0.8, "options": [] } ],
  "presets": [ "Live", "Hold to slow", "…" ], "presetDescriptions": [ "…", "…" ],
  "presetCategories": [ "main", "main", "…" ],
  "actions": [ "live", "trigger", "moveWater", "resetKnobs" ]
}
```

A tunable with a non-empty `options` array is a choice, and `["Off","On"]` is a toggle.
`presetCategories` groups the presets: `main` (representative), `advanced` (specialised, usually with self-defined
variables) or `saved` (saved on this headset).

**`state`**: broadcast about 15 times per second.

```json
{
  "type": "state", "t": 123.45, "delay": 0.82, "rate": 0.21, "u": 0.8, "alpha": 0.05, "phase": "slowing",
  "policy": "fn", "preset": "Hold to slow", "maxDelay": 2.0, "budget": 2.0, "fps": 59.8,
  "hold": 1.0, "remoteHold": 1.0, "bypass": false,
  "tracked": true, "inView": true, "speed": 0.42, "source": "right hand",
  "left":  { "tracked": true, "inView": false, "speed": 0.10, "device": "left hand" },
  "right": { "tracked": true, "inView": true,  "speed": 0.42, "device": "right hand" },
  "battery": 0.71, "clients": 2, "status": "streaming", "diag": "menu: panel closed | …",
  "values": [ { "id": "fn.k1", "v": 0.8 } ]
}
```

`delay` is δ in seconds, and `rate` is the measured playback rate ds/dt (1 = real time, < 1 slow, 0 frozen, < 0 rewinding).

**`notice`**: `{ "type": "notice", "level": "info" | "error", "text": "…" }`

## Minimal Python client

```python
import json, websocket  # pip install websocket-client
ws = websocket.create_connection("ws://192.168.1.23:8080/ws")
ws.send(json.dumps({"type": "hello", "role": "operator", "pin": ""}))
ws.send(json.dumps({"type": "preset", "name": "Bullet time"}))
ws.send(json.dumps({"type": "expr", "id": "u", "name": "on ? 0.9 * smoothstep(0, 1, ton) : 0"}))
ws.send(json.dumps({"type": "input", "hold": 1}))   # resend while holding
print(json.loads(ws.recv())["type"])
```

## Testing without a headset

**Remapping Time → Diagnostics → Run Web UI Smoke Test** serves the real pages and simulated telemetry from the Editor
on port 8099 for 60 s. Open `http://localhost:8099/operator` in a browser.
