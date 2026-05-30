# ComfyUI-FBX-ControlNet-Converter

Turn a rigged 3D animation (**FBX / GLB**) into **ControlNet conditioning passes** — OpenPose,
depth, normal, alpha (silhouette), and canny — directly inside ComfyUI.

A bundled native **C# / OpenGL** CLI does the rig evaluation, skinning, and rendering; the node
loads the resulting frame sequence as an `IMAGE` batch you can wire straight into ControlNet /
AnimateDiff. Because the passes come from the actual skeleton and mesh, they are geometrically
exact and temporally stable — no Python 3D stack, no preprocessor guesswork.

![passes](docs/passes.png)

---

## Passes

| `render_pass` | Output | Needs a mesh? |
|---|---|---|
| `openpose` | Standard OpenPose stick figure (BODY‑18 body + 21‑pt hands) | No (skeleton only) |
| `depth`    | Grayscale depth, near = bright | Yes |
| `normal`   | View‑space normal map (RGB) | Yes |
| `alpha`    | White silhouette / matte mask | Yes |
| `canny`    | Edge map (Sobel over the normal pass) | Yes |

---

## Requirements

| | |
|---|---|
| **OS** | Windows **x64** (the bundled binary is win‑x64 only) |
| **ComfyUI** | any recent version |
| **Git + Git LFS** | needed to clone the bundled binary (~80 MB) |
| **GPU** | any GPU/driver with **OpenGL 3.3** (offscreen rendering) |
| **.NET runtime** | **none** — the build is self‑contained (runtime is bundled) |
| **Python packages** | `numpy`, `Pillow`, `torch` — already provided by ComfyUI |
| **Build from source (optional)** | **.NET 8 SDK** |

Bundled native libraries (shipped in `bin/`): Assimp (FBX/GLB import), Silk.NET (OpenGL + GLFW),
SixLabors.ImageSharp (PNG encode), and the .NET runtime. See `THIRD-PARTY-NOTICES.txt`.

---

## Installation

```bash
git lfs install                       # one-time; required so the binary downloads
cd ComfyUI/custom_nodes
git clone https://github.com/Bo-sung/ComfyUI-FBX-ControlNet-Converter
```

Restart ComfyUI. The node **FBX → ControlNet Converter** appears under the **FBX ControlNet**
category.

> **If the node errors that the exe is missing / 1 KB:** you cloned without Git LFS. Run
> `git lfs pull` inside the repo folder, or [build from source](#build-from-source).

---

## Usage

### Quick start
1. Add the node **FBX → ControlNet Converter** (category *FBX ControlNet*).
2. Set **`fbx_path`** to your animation file, e.g. `C:\anims\Walk.fbx`.
3. Choose **`render_pass`** (`openpose` for pose control; `depth`/`normal`/`canny`/`alpha` need a
   mesh — see below).
4. Set `width` / `height` to your ControlNet resolution, pick a `cam` view.
5. Wire **`images`** → an OpenPose/Depth/etc. **ControlNet Apply** node. `frame_count` (INT) tells
   you how many frames came out (for batch/AnimateDiff sizing).

### Mixamo workflow (rig + animation clip)
Mixamo can export an animation **with** or **without** skin. The mesh passes
(depth/normal/alpha/canny) need geometry, so the usual setup is two files:

- **`fbx_path`** → a **skinned** character (e.g. `Y Bot.fbx`, downloaded *with skin*)
- **`anim_path`** → the **animation‑only** clip (e.g. `Zombie Walk.fbx`)

The clip's motion is retargeted onto the rig by matching bone names. `openpose` works from either
file (it only needs the skeleton), so for pose you can just point `fbx_path` at the clip.

### Frame rate
`fps` and `frames` are independent of the file's authored fps — frames are interpolated, so you
can resample freely. Examples (clip length 4.0 s):
- `fps 30`, `frames 0` → 120 frames over the whole clip.
- `fps 6`,  `frames 0` → 24 frames (downsampled), motion speed unchanged.
- `frames N` forces exactly N frames (spaced at `1/fps` s from the start).

---

## Node inputs

**Required**

| Input | Notes |
|---|---|
| `fbx_path` | Path to the `.fbx` / `.glb` (rigged). |
| `render_pass` | `openpose` \| `depth` \| `normal` \| `alpha` \| `canny`. |
| `width`, `height` | Output resolution (px). |
| `fps` | Sampling rate (interpolated; independent of source fps). |
| `frames` | `0` = whole clip at `fps`; otherwise force a frame count. |
| `cam` | `front` \| `back` \| `left` \| `right` \| `custom`. |
| `center` | `xz` (in place, default) \| `xyz` (also lock vertical) \| `off` (keep root motion). |

**Optional**

| Input | Notes |
|---|---|
| `anim_path` | Separate animation clip applied to `fbx_path`'s rig. |
| `cam_yaw`, `cam_pitch` | Orbit angles (used when `cam = custom`). |
| `cam_zoom` | 1 = fit, <1 larger, >1 smaller. |
| `cam_fov` | Vertical FOV (perspective). |
| `cam_ortho` | Orthographic projection (no perspective distortion). |
| `up_axis` | `y` (default) or `z` (converts Z‑up rigs). |
| `scale` | Uniform scale on joint coordinates. |
| `mirror` | Flip output horizontally. |
| `draw_hands`, `draw_face` | OpenPose only. |
| `line_width`, `dot_radius` | OpenPose stick/joint thickness (px). |
| `bg` | Background `r,g,b` 0..255 (default `0,0,0`). |
| `exe_path` | Override the bundled exe (or set the `FBXCN_EXE` env var). |

**Outputs:** `images` (`IMAGE` batch, `frames × H × W × 3`), `frame_count` (`INT`).

---

## CLI (standalone, optional)

The same engine is a normal command‑line tool:

```powershell
bin\fbxcontrolnet.exe --input "Y Bot.fbx" --anim "Zombie Walk.fbx" `
  --passes openpose,depth,normal,canny --out .\frames --cam left
```

Writes `openpose_0000.png`, `depth_0000.png`, … Run `fbxcontrolnet.exe --help` for every option.

---

## Build from source

Requires the **.NET 8 SDK**:

```powershell
./build.ps1     # publishes the self-contained win-x64 build into ./bin
```

Layout: `cli/` holds the C# solution (`Core` library + `Cli` executable) and
`cli/data/bone_map.json` (the bone‑name normalization table). Every output is an `IRenderPass`
plugin and the CLI is the only contract the node depends on, so the native core could be
re‑implemented (e.g. in C++) without touching the node.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Node: *"fbxcontrolnet.exe not found"* or exe is ~1 KB | `git lfs pull`, or build from source, or set `exe_path` / `FBXCN_EXE`. |
| *"requested a mesh pass but the file has no mesh"* | Your file is animation‑only. Use a skinned model, or set `fbx_path` to a skinned rig and `anim_path` to the clip. |
| Figure too small / off‑centre | Use `center = xz`, adjust `cam_zoom`, or `cam = custom` with `cam_yaw`/`cam_pitch`. |
| Pose looks rotated / upside down | Try `up_axis = z` (some non‑Mixamo rigs are Z‑up). |

---

## License

Original source is **MIT** (see `LICENSE`). The bundled binary includes third‑party libraries
under their own terms — see `THIRD-PARTY-NOTICES.txt`. Note **SixLabors.ImageSharp** uses the Six
Labors Split License (free for open‑source projects and organizations under USD $1M annual gross
revenue; a commercial license is required otherwise).

The bone‑name mapping table and all source here are original work. The concept was inspired by
ComfyUI‑Yedp‑Action‑Director; no third‑party code is reused.
