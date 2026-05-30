using System.Numerics;
using FbxControlNetConverter.Core;
using FbxControlNetConverter.Core.Passes;
using FbxControlNetConverter.Core.Rendering;

namespace FbxControlNetConverter.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var a = new ArgMap(args);
            string input = a.Require("input");
            string outDir = a.Get("out") ?? "./out";
            int width = a.Int("width", 768);
            int height = a.Int("height", 768);

            var cfg = new PassConfig
            {
                Width = width,
                Height = height,
                DrawHands = !a.Flag("no-hands"),
                DrawFace = !a.Flag("no-face"),
                LineThicknessPx = a.Float("line-width", 4f),
                JointRadiusPx = a.Float("dot-radius", 4f),
            };

            string[] passes = (a.Get("passes") ?? "openpose")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in passes)
                if (!PassRegistry.IsKnown(p))
                {
                    Console.Error.WriteLine($"Unknown pass '{p}'. Known: {string.Join(", ", PassRegistry.Names)}");
                    return 2;
                }

            var opts = new PipelineOptions
            {
                InputPath = input,
                AnimPath = string.IsNullOrWhiteSpace(a.Get("anim")) ? null : a.Get("anim")!.Trim().Trim('"'),
                OutputDir = outDir,
                BoneMapPath = a.Get("bonemap") ?? DefaultBoneMap(),
                Passes = passes,
                Fps = a.Float("fps", 30f),
                FrameCount = a.Int("frames", 0),

                // space / transform
                Center = ParseCenter(a.Get("center") ?? "xz"),
                Up = (a.Get("up-axis") ?? "y").ToLowerInvariant() == "z" ? UpAxis.Z : UpAxis.Y,
                Scale = a.Float("scale", 1f),
                Mirror = a.Flag("mirror"),

                // camera
                Camera = ParseCam(a.Get("cam") ?? "front"),
                Yaw = a.Get("cam-yaw") is not null ? a.Float("cam-yaw", 0f) : null,
                Pitch = a.Get("cam-pitch") is not null ? a.Float("cam-pitch", 0f) : null,
                Zoom = a.Float("cam-zoom", 1f),
                FovDegrees = a.Float("cam-fov", 45f),
                Margin = a.Float("cam-margin", 0.1f),
                Orthographic = a.Flag("cam-ortho"),
                Near = a.Get("cam-near") is not null ? a.Float("cam-near", 0.01f) : null,
                Far = a.Get("cam-far") is not null ? a.Float("cam-far", 0f) : null,
                CamPos = ParseVec3(a.Get("cam-pos")),
                CamTarget = ParseVec3(a.Get("cam-target")),
                CamUp = ParseVec3(a.Get("cam-up")),

                // output image
                Background = ParseVec3(a.Get("bg")) is { } bg ? bg / 255f : Vector3.Zero,
                PassConfig = cfg,
                JsonPath = a.Get("json"),
            };

            if (!File.Exists(opts.InputPath))
            {
                Console.Error.WriteLine($"Input not found: {opts.InputPath}");
                return 2;
            }
            if (!File.Exists(opts.BoneMapPath))
            {
                Console.Error.WriteLine($"bone_map.json not found: {opts.BoneMapPath} (use --bonemap)");
                return 2;
            }

            Pipeline.Run(opts, Console.Out);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static Vector3? ParseVec3(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(',', StringSplitOptions.TrimEntries);
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length != 3 ||
            !float.TryParse(parts[0], ci, out var x) ||
            !float.TryParse(parts[1], ci, out var y) ||
            !float.TryParse(parts[2], ci, out var z))
            throw new ArgumentException($"Expected 'x,y,z' but got '{s}'");
        return new Vector3(x, y, z);
    }

    private static CenterMode ParseCenter(string s) => s.ToLowerInvariant() switch
    {
        "off" or "global" or "none" => CenterMode.Off,
        "xyz" => CenterMode.Xyz,
        _ => CenterMode.Xz,   // "xz" / "inplace" / default
    };

    private static CameraPreset ParseCam(string s) => s.ToLowerInvariant() switch
    {
        "front" => CameraPreset.Front,
        "back" => CameraPreset.Back,
        "left" => CameraPreset.Left,
        "right" => CameraPreset.Right,
        _ => CameraPreset.Front,
    };

    private static string DefaultBoneMap()
    {
        string dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "data", "bone_map.json");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        mixamo2pose — extract OpenPose (and later depth/normal/alpha/canny) from Mixamo FBX/GLB.

        Usage:
          mixamo2pose --input anim.fbx --out ./frames [options]

        Input / output:
          --input <path>       FBX/GLB/BVH file (required). Skinned rig for mesh passes.
          --anim <path>        Separate animation clip applied to --input's rig (Mixamo rig+clip)
          --out <dir>          Output directory (default ./out)
          --passes <list>      Comma list: openpose,depth,normal,alpha,canny (default: openpose)
          --json <path>        Also dump per-frame joint world positions
          --bonemap <path>     Override bone_map.json location

        Sequence:
          --fps <n>            Frames per second to sample (default 30)
          --frames <n>         Force frame count (default: derive from duration)

        Output image:
          --width <px>         Output width (default 768)
          --height <px>        Output height (default 768)
          --bg r,g,b           Background color 0..255 (default 0,0,0 black)
          --line-width <px>    Limb line thickness (default 4)
          --dot-radius <px>    Joint disk radius (default 4)
          --no-hands           Skip 21-point hands
          --no-face            Skip face keypoints

        Space / transform:
          --center <mode>      off|xz|xyz root-motion strip (default xz: in place)
          --up-axis <y|z>      Source up axis; z converts Z-up rigs to Y-up (default y)
          --scale <f>          Uniform scale on joint coordinates (default 1)
          --mirror             Flip output horizontally (left/right swap)

        Camera (auto-frame):
          --cam <preset>       front|back|left|right (default front)
          --cam-yaw <deg>      Orbit around Y (0=front,90=right,180=back); overrides --cam
          --cam-pitch <deg>    Elevation (+up / -down); overrides --cam
          --cam-zoom <f>       Figure size: 1=fit, <1 larger, >1 smaller (default 1)
          --cam-fov <deg>      Vertical FOV for perspective (default 45)
          --cam-margin <f>     Auto-frame padding fraction (default 0.1)
          --cam-ortho          Orthographic projection (no perspective distortion)
          --cam-near <f>       Near clip plane (default: auto)
          --cam-far <f>        Far clip plane (default: auto)

        Camera (explicit, overrides auto-frame):
          --cam-pos x,y,z      Camera world position (same space as --json)
          --cam-target x,y,z   Look-at point (default: figure center)
          --cam-up x,y,z       Up vector (default 0,1,0)
        """);
    }
}

/// <summary>Tiny --key value / --flag argument parser (no external dependency).</summary>
internal sealed class ArgMap
{
    private readonly Dictionary<string, string?> _kv = new(StringComparer.OrdinalIgnoreCase);

    public ArgMap(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            string key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                _kv[key] = args[i + 1];
                i++;
            }
            else _kv[key] = null; // flag
        }
    }

    public string? Get(string k) => _kv.TryGetValue(k, out var v) ? v : null;
    public bool Flag(string k) => _kv.ContainsKey(k);
    public string Require(string k) => Get(k) ?? throw new ArgumentException($"Missing required --{k}");
    public int Int(string k, int def) => int.TryParse(Get(k), out var v) ? v : def;
    public float Float(string k, float def) =>
        float.TryParse(Get(k), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
}
