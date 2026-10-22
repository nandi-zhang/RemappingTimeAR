# Remapping Time in AR

Real-time **time remapping** for video see-through AR on Meta Quest 3. The headset buffers what its
cameras see and chooses, every frame, *which moment of the world to show you*: slow motion, freeze,
rewind, fast-forward catch-up, time that only slows inside a virtual volume, or time that someone
else controls from their phone.

This is the demo system accompanying the UIST '26 paper
[*Remapping Time in Augmented Reality*](https://nandi-zhang.github.io/static/media/uist-26-remapping-time.e45be34afbda688b1d12.pdf)
(Nandi Zhang and Yukang Yan, University of Rochester). It adds head reprojection, smoother visuals, a
policy framework for building your own effects, an in-headset tuner, and a browser-based control panel.

---

## The model in one paragraph

The system tracks one number, the temporal offset **δ(t)**: how many seconds the displayed world lags
behind reality. It evolves as

```
dδ/dt = u(t) − α(t)·δ(t)        displayed time  s(t) = t − δ(t)        playback rate  r = 1 − u + α·δ
```

**u** (injection) adds delay; **α** (recovery) pulls it back to zero. `u = 0.8, α ≈ 0` gives 5× slow motion,
`u = 1` freezes, `u > 1` rewinds, and a large α after a slowdown fast-forwards back to live. Constant u and α
only settle into a constant lag (δ → u/α, r → 1), so interesting effects come from **switching u and α in
response to events**. That is what a *policy* does.

---

## Features

| | |
|---|---|
| **Head reprojection** | Every buffered frame stores the pose of the camera that captured it and is drawn *where it was captured*. Rotation is fully compensated and translation is exact at a chosen depth, so the past stays put in the world as you move your head. Toggle it off to compare with a head-locked image. |
| **Soft canvas** | Feathered edges blend into the live passthrough. When nothing is remapped the canvas fades out entirely, leaving the full-quality system passthrough. |
| **Smooth slow motion** | Neighbouring frames are cross-faded, so 5× slow motion is not a 12 fps slideshow. |
| **u(t) and α(t) as formulas** | Type the trigger, u and α as formulas on the operator page (e.g. `on ? k1 * smoothstep(0, k3, ton) : 0`) and see the result instantly. 22 presets cover hold-to-slow, freeze, rewind, referee replay, motion slow-mo, hitstop, water, oscillation and more. |
| **Effects, organised by the design space** | *Representation*: replace, ghost (semi-transparent) or echo trails (chronophotography). *Scope*: the whole view or a *time lens* around your hand. *Transparency*: speed colour and an edge glow signal the remapping (one toggle), or switch them off for low-transparency designs. |
| **Timeline gallery** | Several presets side by side on floating screens, all fed by the same live camera and reacting to the same input. |
| **Water / honey** | A refractive block of virtual water (or amber honey); formulas can read how deep a hand is inside it (`depth`, `inzone`) for pseudo-haptic "thick material" effects. |
| **Status HUD** | A status display (delay, rate, phase, web address) that expands into a thumbstick-driven tuner with a live δ/r graph. |
| **Web control** | The headset serves its own control pages. `/operator` (laptop): presets, formulas, all parameters, live telemetry. `/` (any phone): a big *hold to slow their world* button for onlookers. |
| **Presets** | 22 built-in presets plus user presets saved on the device (formulas, knob names and settings). |

---

## Requirements

- Meta Quest 3 / 3S running Horizon OS v74 or later (Passthrough Camera API).
- Unity **6000.0.61f1** with Android Build Support (OpenJDK, SDK & NDK).
- Packages (resolved automatically from `Packages/manifest.json`): Meta MR Utility Kit 83 (pulls in Meta XR Core SDK), XR Plugin Management, Oculus XR Plugin.

## Quick start

1. Open this folder in Unity Hub with Unity 6000.0.61f1. The first import takes a few minutes.
2. Menu **Remapping Time → Create Demo Scene**. This generates `Assets/RemappingTime/Scenes/RemappingTimeDemo.unity` and adds it to Build Settings.
3. Menu **Remapping Time → Build APK**, then `adb install -r Builds/RemappingTime.apk`. You can also use *File → Build And Run* with the headset connected.
4. Launch **Remapping Time** on the headset and allow the camera permission on first run.
5. The HUD shows the control URL, e.g. `http://192.168.1.23:8080/`. Open it on a phone, or add `/operator` on a laptop.

Batch-mode equivalents (useful for CI):

```
Unity -batchmode -projectPath . -buildTarget Android -executeMethod RemappingTime.EditorTools.DemoSceneBuilder.BuildFromCommandLine
Unity -batchmode -projectPath . -buildTarget Android -executeMethod RemappingTime.EditorTools.DemoBuild.BuildFromCommandLine
```

---

## Controls

**Right controller: time.** Hand tracking works too. Motion formulas read the hand speed of **both hands**: whichever
index fingertip (or controller, if held) is moving fastest *inside the cameras' view* counts. Motion outside the view
is ignored, since the remapping could not be seen there.

| Input | Action |
|---|---|
| Index trigger | `hold` (what it does depends on the formulas) |
| Thumbstick ←/→ | `scrub` |
| A | Trigger: resets `tap` to 0 (e.g. a manual freeze in *Hitstop punch*) |
| B | Snap back to live |
| Grip (hold) | Move the water block to your right hand |

**Left controller: status HUD and tuner**

| Input | Action |
|---|---|
| Y | HUD: minimal (default) → hidden → full |
| Thumbstick ↑/↓ | Select a setting (knobs k1–k3, effects, display, …) |
| Thumbstick ←/→ | Change value (hold to accelerate) |
| Thumbstick click | Reset to default |
| X | Next preset |
| Grip (hold) | **Bypass**: show live for an instant A/B comparison |

**Editor keyboard** (play mode over Link): Space = hold, ←/→ = scrub, Enter = trigger, Backspace = live.

## Web control

The headset runs a small HTTP + WebSocket server on port **8080** (no internet, no app install).

- `http://<headset-ip>:8080/operator` **operator** (laptop): pick presets, **type the formulas for u(t) and α(t)**,
  tune knobs and effects, save presets, and watch δ(t), r(t), u(t) live. Set an **operator PIN** on the
  *Remote Control Bridge* component before a public demo, so visitors cannot reconfigure things.
- `http://<headset-ip>:8080/` **remote**, for onlookers: it shows only the controls the current formulas use. That is
  a hold button if they read `hold`, a scrub slider for `scrub`, and a Trigger button for `tap`.

**No phone?** A laptop works for everything: hold **Space** (on either page) to hold, **P** = trigger, **L** = snap to live.

**Networking tip:** venue Wi-Fi usually isolates clients from each other. Bring a small travel router (or use a phone
hotspot) and join the headset, laptop and visitors' phones to it. Remote inputs time out after 0.6 s, so a phone that
drops off the network never leaves the wearer stuck in slow motion.

The protocol is documented in [`Docs/PROTOCOL.md`](Docs/PROTOCOL.md) if you want to drive the system from your own
code (Python, TouchDesigner, a game server, …).

---

## Designing u(t) and α(t)

Everything the system does is described by three formulas, editable live on the operator page:

| Formula | Meaning | Example |
|---|---|---|
| **when** | trigger condition; while true `on` = 1 and `ton` counts up, afterwards `toff` counts up | `hold`, `speed > 0.9`, `inzone` |
| **u =** | injection: 0 = real time, 0.8 = 5× slower, 1 = freeze, > 1 = rewind | `on ? k1 : 0` |
| **α =** | recovery: pulls the delay back; catch-up takes ≈ 3/α s | `on ? 0.05 : k2` |

**Variables:** `t` time · `d` current delay δ · `B` buffer budget · `hold` 0–1 · `scrub` −1…1 · `speed` fastest hand in
view (m/s) · `lspeed`, `rspeed` per hand · `depth` how deep a hand is in the water (m) · `inzone` · `on` · `ton` · `toff`
· `tap` seconds since the last Trigger · `k1 k2 k3` knobs (0–10, tunable from the HUD and web; each preset names them).

**Language:** `+ − * / % ^`, comparisons, `&& || !`, `c ? a : b`, `pi`, and `sin cos tan abs sqrt exp log floor ceil
round sign sat min max pow step clamp lerp smoothstep noise`. A value counts as true when it is above 0.5.
Knobs that no formula uses are hidden.

**Drawn shapes:** two curves, `c1(x)` (a u shape, 0–2) and `c2(x)` (an α shape, 0–20), each over 0–4 s, are drawn on the
operator page: drag points, double-click to add or remove. A dashed playhead shows the current `ton` / `toff`. Use them in
any formula, e.g. *Draw your own*: u = `on ? c1(ton) : 0`, α = `on ? 0.05 : c2(toff)`. Curves are saved with presets.

| Preset | when | u = | α = |
|---|---|---|---|
| Hold to slow | `hold` | `on ? k1 : 0` | `on ? 0.05 : k2` |
| Hold to slow (analog) | `hold > 0.05` | `on ? k1 * hold : 0` | `on ? 0.05 : k2` |
| Draw your own | `hold` | `on ? c1(ton) : 0` | `on ? 0.05 : c2(toff)` |
| Looping shape | `1` | `c1(t % k1)` | `c1(t % k1) > 0.5 ? 0.05 : k2` |
| Freeze frame | `hold` | `on ? 1 : 0` | `on ? 0 : k2` |
| Gradual brake | `hold` | `on ? k1 * smoothstep(0, k3, ton) : 0` | `on ? 0.05 : k2` |
| Rewind while held | `hold` | `on ? 1 + k1 : 0` | `on ? 0 : k2` |
| Referee replay | `hold \|\| abs(scrub) > 0.15` | `on ? 1 - k1 * scrub : 0` | `on ? 0 : k2` |
| Motion slow-mo | `speed > k3` | `on \|\| toff < 0.15 ? k1 : 0` | `on \|\| toff < 0.15 ? 0.05 : k2` |
| Outrun yourself | `speed > k3` | `on ? min(0.95, k1 * (speed - k3)) : 0` | `on ? 0.05 : k2` |
| Hitstop punch | `speed > k3 \|\| tap < 0.3 \|\| (on && ton < k1)` | `on ? 0.95 : 0` | `on ? 0 : k2` |
| Water / Honey | `inzone` | `on ? k1 * sat(depth / 0.03) : 0` | `on ? k3 : k2` |
| Time wobble | `1` | `max(0, k1 + 0.6 * sin(2 * pi * k3 * t))` | `k2` |
| Stutter | `1` | `0.98 * step(k1, sin(2 * pi * k3 * t))` | `k2` |
| Drifting time | `1` | `k1 * noise(k3 * t)` | `k2` |
| Constant u, α | `0` | `k1` | `k2` |

Also built in: *Live, Bullet time, Echo trails, Time lens, Ghost* (hold-to-slow combined with different
representations). Presets saved from the operator page live in
`/sdcard/Android/data/org.remappingtime.demo/files/remapping_presets.json`.

### Head reprojection

*Display → Head reprojection* has three modes:

- **Depth (experimental)**: each buffered frame stores the depth map from Meta's Depth API, and the frame is drawn as a grid
  whose points are pushed out to the scene's measured depth along their camera rays. Near and far things both stay put
  as you move or turn your head. Limitations: the depth map is low resolution and smoothed, so sharp depth edges (a hand in
  front of a far wall) stretch slightly, and fast-moving objects lag in the depth sensor. Where there is no depth, the
  plane below is used. In practice alignment is very good, but fine details break up along the coarse depth mesh. Requires the *spatial data* permission (asked on first launch); the operator page's Diagnostics
  card shows `depth: … frames with depth N%`.
- **Flat plane** (default): frames are drawn on one plane at the **projection distance** D. Exact for surfaces at D; elsewhere any
  camera movement between capture and display shows as parallax ∝ movement × |1/D − 1/depth|, including during head
  rotation, because the cameras sit in front of the neck's pivot. Set D where the action is (≈0.6 m for hands over a
  table, 1.5–2 m for watching another person).
- **Off**: the image follows the head (no reprojection).

The planar-only version of the project is tagged `v0.1-planar-reprojection` in git.

---

## Architecture

```
PassthroughCameraAccess ×2 ──► StereoPassthroughCapture ──► FrameRingBuffer  (texture + camera pose per frame)
                                         │                        ▲
                                    CameraClock (t)               │ sample s(t), blend, pose
                                         ▼                        │
 InputHub (controllers + web) ─┐   TimeRemapEngine ──► RemapChannel (δ, s, r) ──► ReprojectedFrameView ─► eyes
 TrackedPoint (hand/controller)┼─►  RemapContext  ──► RemapPolicy → (u, α)        TimelineGallery (more channels)
 ZoneVolume (depth) ───────────┘         │
                                Tunables / Presets ◄──► HeadsetHud (left controller)
                                                    ◄──► RemoteControlBridge ◄─ WebSocket ─► browser pages
```

| Folder | Contents |
|---|---|
| `Runtime/Core` | `RemapDynamics` (the ODE and helpers), `FrameRingBuffer`, `CameraClock`, `RemapChannel`, `Tunable`, `PresetLibrary` |
| `Runtime/Capture` | Stereo camera capture with per-frame poses |
| `Runtime/Signals` | Tracked hand/controller kinematics, input merging |
| `Runtime/Policies` | `FunctionPolicy` (formula-driven u, α), the policy base class and `PolicyCatalog` |
| `Runtime/Rendering` | Reprojected view, frame quads, gallery, zone, per-eye layer setup |
| `Runtime/UI` | In-headset HUD |
| `Runtime/Remote` | Minimal HTTP/WebSocket server, protocol, engine bridge |
| `Shaders` | Frame compositing (blend, feather, lens, cues), unlit UI, zone volume |
| `Resources/RemappingTime` | `remote.html`, `operator.html` (served from the headset) |
| `Editor` | Scene builder, APK build, web UI smoke test |

### Writing a policy in code

Most designs fit in the three formulas. When you need something they can't express (another sensor, networking, game
state), write a policy class. It appears as a choice on the operator page, and its knobs show up in the HUD and web UI.

```csharp
using RemappingTime;
using UnityEngine;

// Time slows while a hand is raised above the head.
public sealed class RaiseHandPolicy : RemapPolicy
{
    public override string Id => "raise";
    public override string DisplayName => "Raise hand to slow";
    public override string Description => "Lift your hand above your head to slow the world.";

    private readonly Tunable _u, _catchUp;

    public RaiseHandPolicy()
    {
        _u = Range("u", "Slowdown u", 0f, 1f, 0.75f, 0.05f);           // shows up in the HUD and web UI
        _catchUp = Range("alpha", "Catch-up α", 0f, 20f, 5f, 0.1f);
    }

    public override RemapCommand Evaluate(in RemapContext ctx)
    {
        bool raised = ctx.Motion.Tracked && ctx.Motion.Position.y > ctx.Head.position.y + 0.1f;
        return raised ? new RemapCommand(_u.Value, 0.05f, "raised")
                      : new RemapCommand(0f, _catchUp.Value, ctx.Delay > 0.01 ? "catching up" : "idle");
    }
}

// Register once at startup, before the engine's Awake:
// [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
// static void Register() => PolicyCatalog.Register(() => new RaiseHandPolicy());
```

### Building a game or study on top

Drive everything through `TimeRemapEngine`: `ApplyPreset`, `SetExpression("u", "...", out err)`, `SetTunable`,
`Inputs.QueueAction()` (the Trigger), and read `Main.Delay`, `Main.Rate`, `Main.Phase`. For example, a *spot-the-warp*
round could set when = `noise(t) > 0.8`, log when `Main.Phase == "on"`, and score the wearer's trigger presses against it.

---

## Running a demo booth

- **Power and heat:** two cameras at 60 Hz plus a 1.2 GB frame buffer is a heavy load. Use a battery strap or a
  long USB-C cable to a power bank, and give the headset breaks. Lower *Buffer seconds* or *Storage scale* on
  `StereoPassthroughCapture` if it throttles.
- **Network:** bring your own router (see above), and set the operator PIN.
- **Lighting:** hand tracking needs decent light. For fast motions (hitstop, slashes), hand visitors the right controller.
- **Reset:** B snaps to live; the operator page has *Snap to live* and per-policy *Reset*. Re-centre with the Meta button.
- **Comfort:** keep slowdowns to a few seconds, use the *Bypass* grip to show "before/after", and remind visitors that the
  world they see during remapping is not the present one (don't walk while slowed).

## Known limitations

- Depth reprojection follows a low-resolution, smoothed depth map: depth edges stretch a little and fast motion lags.
- The buffer only holds the past, so δ ≥ 0. Forward sampling (showing predicted future frames, paper Sec. 6.2) is not yet included.
- The remapped canvas covers the cameras' field of view, which is slightly smaller than the display's. Feathering hides the edge, but it is there.
- Hand tracking can lose very fast motion; controllers are more reliable for impact effects.

## Citation

```bibtex
@inproceedings{zhang2026remapping,
  title     = {Remapping Time in Augmented Reality},
  author    = {Zhang, Nandi and Yan, Yukang},
  booktitle = {Proceedings of the 39th Annual ACM Symposium on User Interface Software and Technology (UIST '26)},
  year      = {2026},
  doi       = {10.1145/3830398.3830686}
}
```

## License

*To be decided by the authors before publishing.* Note that the Meta XR SDK packages are covered by the
Meta Platform Technologies SDK license.
