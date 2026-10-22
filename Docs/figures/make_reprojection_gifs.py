"""Animated explainers for the three head-reprojection modes (Off / Flat plane / Depth).

Top-down 2D model: one camera 9 cm in front of the neck pivot the head turns around, a frozen camera frame captured at pose H_cap, and the head
moving afterwards (H_now). For three scene points (near hand, cup at the plane distance, far wall) we compute where
the remapped frame *shows* them, and compare with where they really are (the live passthrough).
"""
import math, sys, os
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.patches import Polygon, Circle, FancyArrowPatch
from PIL import Image

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
FRAMES, FPS = 120, 15
HALF_FOV = math.radians(40)
D = 1.0                                   # flat-plane distance (m)

# Scene: segments (x0, z0, x1, z1) and labelled feature points.
WALL = (-2.2, 2.6, 2.2, 2.6)
CUP = (-0.25, 1.0, -0.05, 1.0)
HAND = (0.10, 0.5, 0.22, 0.5)
SEGMENTS = [WALL, CUP, HAND]
FEATURES = [  # name, world point, colour
    ("hand 0.5 m", np.array([0.16, 0.5]), "#E69F00"),
    ("cup 1.0 m", np.array([-0.15, 1.0]), "#009E73"),
    ("wall 2.6 m", np.array([-0.9, 2.6]), "#0072B2"),
]
INK, MUTED, GRID = "#1f2430", "#7a8294", "#d9dde5"
FRAME_C = "#8e6cd8"                      # remapped-frame colour (plane / mesh)

# ── Poses: θ > 0 turns left. forward = (−sinθ, cosθ), right = (cosθ, sinθ). ──────────────────────────
def axes(theta):
    return np.array([math.cos(theta), math.sin(theta)]), np.array([-math.sin(theta), math.cos(theta)])

def to_local(p, pose):
    c, th = pose; r, f = axes(th); d = p - c
    return np.array([d @ r, d @ f])

def to_world(l, pose):
    c, th = pose; r, f = axes(th)
    return c + l[0] * r + l[1] * f

def view_angle(p, pose):
    l = to_local(p, pose)
    return math.degrees(math.atan2(l[0], l[1]))

H_CAP = (np.array([0.0, 0.0]), 0.0)

def ease(s):
    return 0.5 - 0.5 * math.cos(2 * math.pi * s)   # 0 → 1 → 0

NECK = 0.09   # the cameras sit about this far in front of the neck, which is what the head turns around

def camera_pose(neck, theta):
    """Camera pose for a head turned by theta about a neck pivot (so turning also moves the camera a little)."""
    return (neck + NECK * axes(theta)[1], theta)

def head_at(i):
    """Three acts: turn only, sideways only, both."""
    act, s = divmod(i, FRAMES // 3)
    s /= FRAMES // 3
    e = ease(s)
    neck0 = np.array([0.0, -NECK])
    if act == 0: return camera_pose(neck0, math.radians(22) * e), "1 / 3  turning the head"
    if act == 1: return camera_pose(neck0 + [0.30 * e, 0.0], 0.0), "2 / 3  moving the head sideways"
    return camera_pose(neck0 + [-0.25 * e, 0.06 * e], math.radians(-18) * e), "3 / 3  turning + moving"

# ── Depth map in the capture camera: true depth per image column, then low-res + smoothing ──────────
def raycast_depth(u):
    """Forward depth (capture-camera z) of the first surface along image coordinate u = x/z."""
    best = 9.0
    for (x0, z0, x1, z1) in SEGMENTS:     # all segments are fronto-parallel: z constant
        z = z0
        x = u * z
        if min(x0, x1) <= x <= max(x0, x1) and z < best: best = z
    return best

U_MAX = math.tan(HALF_FOV)
DEPTH_COLS = 48                           # the sensor's coarse resolution across the view
du = np.linspace(-U_MAX, U_MAX, DEPTH_COLS)
raw = np.array([np.mean([raycast_depth(u + k * (du[1] - du[0]) / 4) for k in (-1.5, -0.5, 0.5, 1.5)]) for u in du])
kernel = np.array([0.25, 0.5, 0.25])
smooth = np.convolve(np.pad(raw, 1, mode="edge"), kernel, mode="valid")   # the depth map is smoothed
def depth_map(u): return float(np.interp(u, du, smooth))

MESH_U = np.linspace(-U_MAX, U_MAX, 49)   # vertex columns of the reprojection grid
MESH_Z = np.array([depth_map(u) for u in MESH_U])
MESH_LOCAL = np.stack([MESH_U * MESH_Z, MESH_Z], axis=1)

def on_mesh(u):
    k = int(np.clip(np.searchsorted(MESH_U, u) - 1, 0, len(MESH_U) - 2))
    s = (u - MESH_U[k]) / (MESH_U[k + 1] - MESH_U[k])
    return (1 - s) * MESH_LOCAL[k] + s * MESH_LOCAL[k + 1]

# ── Where each mode shows a world point ─────────────────────────────────────────────────────────────
def shown(mode, p, now):
    l = to_local(p, H_CAP)                 # camera-space point at capture time
    u = l[0] / l[1]                        # its image coordinate (K⁻¹ ray: [u, 1])
    if mode == "off":   return to_world(l, now)                     # image pinned to the head
    if mode == "flat":  return to_world(np.array([u * D, D]), H_CAP)  # ray hits the plane at D
    return to_world(on_mesh(u), H_CAP)                               # ray hits the depth mesh

MODES = {
    "off": dict(
        title="Head reprojection: Off",
        sub="The frame is pinned to the head, so it moves with every head motion.",
        eq=[r"$\mathbf{x}_{shown} \;=\; H_{now}\, H_{cap}^{-1}\, \mathbf{x}$",
            r"camera-space points are reused as if the head had not moved"],
        verdict="Wrong for any head motion; fine only while the head is still."),
    "flat": dict(
        title="Head reprojection: Flat plane",
        sub="The frame is a world-fixed quad at the capture pose, 1 m in front of where the camera was.",
        eq=[r"$\mathbf{v} \;=\; H_{cap}\,\left(D\; K^{-1}\,[u\;\; v\;\; 1]^{\top}\right),\quad D = 1\,\mathrm{m}$",
            r"drawn from $H_{now}$ by the usual view-projection $P\, H_{now}^{-1}\, \mathbf{v}$"],
        verdict="Things at D stay put; nearer/farther things slip (parallax) whenever the camera moves, a little even on a turn."),
    "depth": dict(
        title="Head reprojection: Depth (experimental)",
        sub="The frame is a grid whose vertices are pushed out to the depth measured with that frame.",
        eq=[r"$\mathbf{v} \;=\; H_{cap}\,\left(z(u,v)\; K^{-1}\,[u\;\; v\;\; 1]^{\top}\right)$",
            r"$z \leftarrow z \cdot \dfrac{d_{map}(M_{depth}\,\mathbf{v})}{d(\mathbf{v})}$   (4 steps;  $M_{depth}$: world → depth-map clip space)"],
        verdict="Near and far stay put; edges and thin things smear (the depth map is coarse and smoothed)."),
}

def draw_head(ax, pose, color, label=None, alpha=1.0, fov_len=3.2, dashed=False):
    c, th = pose; r, f = axes(th)
    a = to_world(np.array([-U_MAX * fov_len, fov_len]), pose)
    b = to_world(np.array([U_MAX * fov_len, fov_len]), pose)
    ax.add_patch(Polygon([c, a, b], closed=True, fc=color, ec="none", alpha=0.06 * alpha, zorder=1))
    ls = (0, (4, 3)) if dashed else "-"
    ax.plot([c[0], a[0]], [c[1], a[1]], color=color, lw=0.8, alpha=0.5 * alpha, ls=ls, zorder=2)
    ax.plot([c[0], b[0]], [c[1], b[1]], color=color, lw=0.8, alpha=0.5 * alpha, ls=ls, zorder=2)
    ax.add_patch(Circle(c, 0.075, fc="white", ec=color, lw=1.6, alpha=alpha, zorder=6))
    nose = c + 0.11 * f
    ax.add_patch(Polygon([c + 0.04 * r + 0.06 * f, c - 0.04 * r + 0.06 * f, nose], fc=color, ec="none", alpha=alpha, zorder=7))
    if label:
        ax.text(c[0] - 0.1, c[1] - 0.12, label, fontsize=8.5, color=color, zorder=8, ha="right", va="top")

def render(mode, path):
    cfg = MODES[mode]
    frames = []
    fig = plt.figure(figsize=(10.0, 5.9), dpi=90)
    for i in range(FRAMES):
        fig.clf()
        fig.patch.set_facecolor("white")
        now, act = head_at(i)
        fig.text(0.03, 0.955, cfg["title"], fontsize=15, weight="bold", color=INK)
        fig.text(0.03, 0.915, cfg["sub"], fontsize=9.5, color=MUTED)
        fig.text(0.03, 0.88, cfg["verdict"], fontsize=9.5, color=INK, style="italic")
        fig.text(0.97, 0.955, act, fontsize=10, color=INK, ha="right")

        # ── Top-down world view ───────────────────────────────────────────────────────────────────
        ax = fig.add_axes([0.02, 0.19, 0.52, 0.66])
        ax.set_xlim(-1.45, 1.45); ax.set_ylim(-0.5, 2.85); ax.set_aspect("equal"); ax.axis("off")
        ax.text(-1.42, 2.8, "top view", fontsize=8.5, color=MUTED, va="top")
        for (x0, z0, x1, z1), lw in zip(SEGMENTS, (2.5, 5, 5)):
            ax.plot([x0, x1], [z0, z1], color="#b8bfcc", lw=lw, solid_capstyle="butt", zorder=3)
        ax.text(1.42, 2.66, "wall", fontsize=8, color=MUTED, ha="right")

        draw_head(ax, H_CAP, MUTED, None, dashed=True)
        draw_head(ax, now, INK, None)
        cn = now[0]
        ax.text(-1.42, -0.3, "grey, dashed:  $H_{cap}$, the head when the frame was captured", fontsize=8.5, color=MUTED, va="center")
        ax.text(-1.42, -0.44, "dark:  $H_{now}$, the head now", fontsize=8.5, color=INK, va="center")

        # The remapped frame's surface.
        if mode == "off":
            a = to_world(np.array([-U_MAX * D, D]), now); b = to_world(np.array([U_MAX * D, D]), now)
            ax.plot([a[0], b[0]], [a[1], b[1]], color=FRAME_C, lw=3, zorder=4)
            ax.text(b[0] + 0.03, b[1], "frame\n(moves with head)", fontsize=8, color=FRAME_C, va="center")
        elif mode == "flat":
            a = to_world(np.array([-U_MAX * D, D]), H_CAP); b = to_world(np.array([U_MAX * D, D]), H_CAP)
            ax.plot([a[0], b[0]], [a[1], b[1]], color=FRAME_C, lw=3, zorder=4)
            ax.text(b[0] + 0.03, b[1], "frame quad\nfixed at D = 1 m", fontsize=8, color=FRAME_C, va="center")
        else:
            pts = np.array([to_world(p, H_CAP) for p in MESH_LOCAL])
            ax.plot(pts[:, 0], pts[:, 1], color=FRAME_C, lw=2.2, zorder=4)
            ax.scatter(pts[:, 0], pts[:, 1], s=7, color=FRAME_C, zorder=4)
            ax.text(1.43, 2.42, "depth mesh", fontsize=8, color=FRAME_C, ha="right")
            # The steepest stretch right of the hand: where the smoothed depth map bridges hand and wall.
            k = max((j for j in range(len(MESH_U) - 1) if MESH_U[j] > 0.3), key=lambda j: abs(MESH_Z[j + 1] - MESH_Z[j]))
            m = to_world((MESH_LOCAL[k] + MESH_LOCAL[k + 1]) / 2, H_CAP)
            ax.annotate("stretched across the\ndepth edge (smears)", m, xytext=(m[0] + 0.35, m[1] - 0.15), fontsize=8,
                        color=FRAME_C, arrowprops=dict(arrowstyle="-", color=FRAME_C, lw=0.8), zorder=8)

        errs = []
        for name, p, col in FEATURES:
            s = shown(mode, p, now)
            # capture ray (grey): which pixel recorded the point
            ax.plot([H_CAP[0][0], p[0]], [H_CAP[0][1], p[1]], color=MUTED, lw=0.6, alpha=0.6, zorder=2)
            # what the wearer looks along to see it in the frame
            far = cn + (s - cn) / max(np.linalg.norm(s - cn), 1e-6) * max(np.linalg.norm(s - cn), np.linalg.norm(p - cn))
            ax.plot([cn[0], far[0]], [cn[1], far[1]], color=col, lw=1.3, zorder=5)
            ax.scatter([p[0]], [p[1]], s=60, facecolor="white", edgecolor=col, lw=1.8, zorder=6)
            ax.scatter([s[0]], [s[1]], s=34, color=col, zorder=7)
            e = (view_angle(s, now) - view_angle(p, now) + 180) % 360 - 180
            errs.append((name, col, e))

        # ── Eye view: angle across the wearer's view ────────────────────────────────────────────────
        ex = fig.add_axes([0.6, 0.46, 0.37, 0.31])
        ex.set_xlim(-45, 45); ex.set_ylim(-0.5, len(FEATURES) - 0.4)
        for sp in ("top", "right", "left"): ex.spines[sp].set_visible(False)
        ex.spines["bottom"].set_color(GRID); ex.tick_params(colors=MUTED, labelsize=8, left=False, labelleft=False)
        ex.set_xticks([-40, -20, 0, 20, 40]); ex.set_xticklabels(["40° L", "20°", "0°", "20°", "40° R"])
        ex.set_title("what the wearer sees  (direction in view)", fontsize=9.5, color=INK, loc="left", pad=6)
        for k, (name, p, col) in enumerate(FEATURES):
            y = len(FEATURES) - 1 - k
            real = view_angle(p, now)
            sh = view_angle(shown(mode, p, now), now)
            ex.plot([-45, 45], [y, y], color=GRID, lw=0.8, zorder=1)
            if abs(sh - real) > 0.4:
                ex.annotate("", xy=(sh, y), xytext=(real, y),
                            arrowprops=dict(arrowstyle="-|>", color=col, lw=1.4, shrinkA=5, shrinkB=4), zorder=3)
            ex.scatter([real], [y], s=80, facecolor="white", edgecolor=col, lw=1.8, zorder=4)
            ex.scatter([sh], [y], s=42, color=col, zorder=5)
            ex.text(-44, y + 0.18, name, fontsize=8, color=col)

        # legend + errors
        lg = fig.add_axes([0.6, 0.2, 0.37, 0.2]); lg.axis("off"); lg.set_xlim(0, 1); lg.set_ylim(0, 1)
        lg.scatter([0.02], [0.88], s=60, facecolor="white", edgecolor=INK, lw=1.5)
        lg.text(0.05, 0.88, "where it really is (live passthrough)", fontsize=8.5, color=INK, va="center")
        lg.scatter([0.02], [0.68], s=34, color=INK)
        lg.text(0.05, 0.68, "where the remapped frame shows it", fontsize=8.5, color=INK, va="center")
        lg.text(0.0, 0.40, "misalignment:", fontsize=8.5, color=MUTED, va="center")
        for k, (name, col, e) in enumerate(errs):
            lg.text(0.0 + k * 0.33, 0.18, f"{name.split()[0]} {abs(e):4.1f}°", fontsize=10, color=col, va="center",
                    weight="bold" if abs(e) > 1 else "normal", family="DejaVu Sans Mono")
        dx = np.linalg.norm(now[0] - H_CAP[0]); dth = math.degrees(now[1])
        lg.text(1.0, 0.40, f"camera moved {100 * dx:4.1f} cm, turned {abs(dth):4.1f}°", fontsize=8.5, color=MUTED,
                va="center", ha="right", family="DejaVu Sans Mono")

        # ── Formula strip ─────────────────────────────────────────────────────────────────────────
        fig.add_artist(plt.Line2D([0.03, 0.97], [0.175, 0.175], color=GRID, lw=1))
        fig.text(0.03, 0.125, cfg["eq"][0], fontsize=13, color=INK, va="center")
        fig.text(0.03, 0.06, cfg["eq"][1], fontsize=9.5, color=MUTED, va="center")
        fig.text(0.97, 0.125, r"$H$: head/camera pose (world ← camera)   $K$: camera intrinsics", fontsize=8.5,
                 color=MUTED, va="center", ha="right")

        fig.canvas.draw()
        img = Image.frombuffer("RGBA", fig.canvas.get_width_height(), fig.canvas.buffer_rgba()).convert("RGB")
        frames.append(img)
    plt.close(fig)
    pal = frames[0].quantize(colors=128, method=Image.Quantize.MEDIANCUT)
    q = [f.quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
    q[0].save(path, save_all=True, append_images=q[1:], duration=int(1000 / FPS), loop=0, optimize=True)
    for k in (FRAMES // 6, FRAMES // 2, 5 * FRAMES // 6): frames[k].save(path.replace(".gif", f"_still{k}.png"))
    print(path, os.path.getsize(path) // 1024, "KB")

if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    for i, m in enumerate(("off", "flat", "depth"), 1):
        render(m, os.path.join(OUT, f"reprojection_{i}_{m}.gif"))
