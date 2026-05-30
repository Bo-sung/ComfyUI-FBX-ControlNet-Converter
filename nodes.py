"""FBX → ControlNet Converter — ComfyUI node.

Extracts ControlNet conditioning passes (OpenPose / depth / normal / alpha / canny) from a
rigged 3D animation (FBX/GLB) by shelling out to the bundled native CLI (fbxcontrolnet.exe,
C#/OpenGL) and loading the rendered PNG sequence into a ComfyUI IMAGE batch tensor
(B, H, W, C) float32 [0,1].

Mesh passes (depth/normal/alpha/canny) need a skinned model; pass a skinned rig as fbx_path
and the animation-only clip as anim_path (the standard Mixamo rig + animation-clip workflow).
"""
from __future__ import annotations

import glob
import os
import shutil
import subprocess
import tempfile

import numpy as np
import torch
from PIL import Image

_PKG_DIR = os.path.dirname(os.path.abspath(__file__))
# Bundled exe ships in ./bin; override with the FBXCN_EXE env var if needed.
_DEFAULT_EXE = os.environ.get("FBXCN_EXE") or os.path.join(_PKG_DIR, "bin", "fbxcontrolnet.exe")


def _load_png_sequence(folder: str, prefix: str) -> torch.Tensor:
    files = sorted(glob.glob(os.path.join(folder, f"{prefix}_*.png")))
    if not files:
        raise RuntimeError(f"No {prefix}_*.png produced in {folder}")
    arrs = [np.array(Image.open(f).convert("RGB"), dtype=np.float32) / 255.0 for f in files]
    return torch.from_numpy(np.stack(arrs, axis=0))


class FbxControlNetConverter:
    """Convert a rigged FBX/GLB animation into a ControlNet pass image batch."""

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "fbx_path": ("STRING", {"default": "", "multiline": False}),
                "render_pass": (["openpose", "depth", "normal", "alpha", "canny"],),
                "width": ("INT", {"default": 768, "min": 64, "max": 4096, "step": 8}),
                "height": ("INT", {"default": 768, "min": 64, "max": 4096, "step": 8}),
                "fps": ("FLOAT", {"default": 30.0, "min": 1.0, "max": 120.0, "step": 1.0}),
                "frames": ("INT", {"default": 0, "min": 0, "max": 100000,
                                   "tooltip": "0 = derive from animation duration"}),
                "cam": (["front", "back", "left", "right", "custom"],),
                "center": (["xz", "xyz", "off"],),
            },
            "optional": {
                "anim_path": ("STRING", {"default": "",
                              "tooltip": "Separate animation clip applied to fbx_path's rig "
                                         "(needed for mesh passes when fbx_path is a clip)"}),
                "cam_yaw": ("FLOAT", {"default": 0.0, "min": -360.0, "max": 360.0, "step": 1.0}),
                "cam_pitch": ("FLOAT", {"default": 0.0, "min": -89.0, "max": 89.0, "step": 1.0}),
                "cam_zoom": ("FLOAT", {"default": 1.0, "min": 0.05, "max": 10.0, "step": 0.05}),
                "cam_fov": ("FLOAT", {"default": 45.0, "min": 1.0, "max": 120.0, "step": 1.0}),
                "cam_ortho": ("BOOLEAN", {"default": False}),
                "mirror": ("BOOLEAN", {"default": False}),
                "up_axis": (["y", "z"],),
                "scale": ("FLOAT", {"default": 1.0, "min": 0.0001, "max": 1000.0, "step": 0.01}),
                "draw_hands": ("BOOLEAN", {"default": True}),
                "draw_face": ("BOOLEAN", {"default": True}),
                "line_width": ("FLOAT", {"default": 4.0, "min": 0.5, "max": 32.0, "step": 0.5}),
                "dot_radius": ("FLOAT", {"default": 4.0, "min": 0.5, "max": 32.0, "step": 0.5}),
                "bg": ("STRING", {"default": "0,0,0", "tooltip": "background r,g,b 0..255"}),
                "exe_path": ("STRING", {"default": _DEFAULT_EXE}),
            },
        }

    RETURN_TYPES = ("IMAGE", "INT")
    RETURN_NAMES = ("images", "frame_count")
    FUNCTION = "convert"
    CATEGORY = "FBX ControlNet"

    def convert(self, fbx_path, render_pass, width, height, fps, frames, cam, center,
                anim_path="", cam_yaw=0.0, cam_pitch=0.0, cam_zoom=1.0, cam_fov=45.0, cam_ortho=False,
                mirror=False, up_axis="y", scale=1.0, draw_hands=True, draw_face=True,
                line_width=4.0, dot_radius=4.0, bg="0,0,0", exe_path=_DEFAULT_EXE):

        fbx_path = fbx_path.strip().strip('"')
        anim_path = (anim_path or "").strip().strip('"')
        exe_path = (exe_path or _DEFAULT_EXE).strip().strip('"')

        if not os.path.isfile(exe_path):
            raise FileNotFoundError(
                f"fbxcontrolnet.exe not found: {exe_path}\n"
                "Build it (see repo README) or set the exe_path widget / FBXCN_EXE env var."
            )
        if not os.path.isfile(fbx_path):
            raise FileNotFoundError(f"Input model not found: {fbx_path}")
        if anim_path and not os.path.isfile(anim_path):
            raise FileNotFoundError(f"Animation clip not found: {anim_path}")

        out_dir = tempfile.mkdtemp(prefix="fbxcn_")
        try:
            cmd = [
                exe_path,
                "--input", fbx_path,
                "--passes", render_pass,
                "--out", out_dir,
                "--width", str(int(width)),
                "--height", str(int(height)),
                "--fps", str(float(fps)),
                "--center", center,
                "--up-axis", up_axis,
                "--scale", str(float(scale)),
                "--cam-zoom", str(float(cam_zoom)),
                "--cam-fov", str(float(cam_fov)),
                "--line-width", str(float(line_width)),
                "--dot-radius", str(float(dot_radius)),
                "--bg", bg.strip(),
            ]
            if anim_path:
                cmd += ["--anim", anim_path]
            if int(frames) > 0:
                cmd += ["--frames", str(int(frames))]
            if cam == "custom":
                cmd += ["--cam-yaw", str(float(cam_yaw)), "--cam-pitch", str(float(cam_pitch))]
            else:
                cmd += ["--cam", cam]
            if cam_ortho:
                cmd += ["--cam-ortho"]
            if mirror:
                cmd += ["--mirror"]
            if not draw_hands:
                cmd += ["--no-hands"]
            if not draw_face:
                cmd += ["--no-face"]

            proc = subprocess.run(cmd, capture_output=True, text=True)
            if proc.returncode != 0:
                raise RuntimeError(
                    f"fbxcontrolnet failed (exit {proc.returncode}).\n"
                    f"CMD: {' '.join(cmd)}\nSTDOUT: {proc.stdout}\nSTDERR: {proc.stderr}"
                )

            images = _load_png_sequence(out_dir, render_pass)
            return (images, images.shape[0])
        finally:
            shutil.rmtree(out_dir, ignore_errors=True)


NODE_CLASS_MAPPINGS = {"FbxControlNetConverter": FbxControlNetConverter}
NODE_DISPLAY_NAME_MAPPINGS = {"FbxControlNetConverter": "FBX → ControlNet Converter"}
