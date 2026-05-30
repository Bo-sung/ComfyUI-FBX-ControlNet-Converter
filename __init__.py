"""ComfyUI-FBX-ControlNet-Converter

Extract ControlNet conditioning passes (OpenPose / depth / normal / alpha / canny) from
rigged FBX/GLB animation, via a bundled native C#/OpenGL CLI.
"""
from .nodes import NODE_CLASS_MAPPINGS, NODE_DISPLAY_NAME_MAPPINGS

WEB_DIRECTORY = None
__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
