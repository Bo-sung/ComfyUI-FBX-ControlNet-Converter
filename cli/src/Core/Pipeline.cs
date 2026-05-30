using System.Numerics;
using System.Text.Json;
using FbxControlNetConverter.Core.Passes;
using FbxControlNetConverter.Core.Rendering;

namespace FbxControlNetConverter.Core;

/// <summary>How to handle root translation across frames.</summary>
public enum CenterMode
{
    /// <summary>Keep root motion; frame the whole path (figure may travel/shrink).</summary>
    Off,
    /// <summary>Strip horizontal (X,Z) root motion so the figure stays in place.</summary>
    Xz,
    /// <summary>Strip all (X,Y,Z) root motion (also removes vertical bob/jump).</summary>
    Xyz,
}

public enum UpAxis { Y, Z }

public sealed class PipelineOptions
{
    public required string InputPath { get; init; }
    public string? AnimPath { get; init; }   // optional separate animation clip (Mixamo rig+clip)
    public required string OutputDir { get; init; }
    public required string BoneMapPath { get; init; }
    public string[] Passes { get; init; } = { "openpose" };
    public double Fps { get; init; } = 30;
    public int FrameCount { get; init; } = 0;          // 0 => derive from duration

    // --- space / transform ---
    public CenterMode Center { get; init; } = CenterMode.Xz;
    public UpAxis Up { get; init; } = UpAxis.Y;
    public float Scale { get; init; } = 1f;
    public bool Mirror { get; init; }

    // --- camera ---
    public CameraPreset Camera { get; init; } = CameraPreset.Front;
    public float? Yaw { get; init; }      // overrides the preset's yaw if set
    public float? Pitch { get; init; }    // overrides the preset's pitch if set
    public float Zoom { get; init; } = 1f;
    public float FovDegrees { get; init; } = 45f;
    public float Margin { get; init; } = 0.1f;
    public bool Orthographic { get; init; }
    public float? Near { get; init; }
    public float? Far { get; init; }
    // Explicit world-space camera (overrides auto-frame entirely when CamPos is set).
    public Vector3? CamPos { get; init; }
    public Vector3? CamTarget { get; init; }
    public Vector3? CamUp { get; init; }

    // --- output image ---
    public Vector3 Background { get; init; } = Vector3.Zero;   // RGB 0..1, default black
    public PassConfig PassConfig { get; init; } = new();
    public string? JsonPath { get; init; }
}

/// <summary>Drives load -> per-frame evaluate -> per-pass render -> PNG sequence.</summary>
public static class Pipeline
{
    public static int Run(PipelineOptions o, TextWriter log)
    {
        var passes = o.Passes.Select(PassRegistry.Create).ToArray();
        bool needMesh = passes.Any(p => p.RequiresMesh);

        var mapper = BoneMapper.Load(o.BoneMapPath);
        var scene = SkeletonLoader.Load(o.InputPath, loadMeshes: needMesh, animPath: o.AnimPath);
        var evaluator = new PoseEvaluator(scene, mapper);

        if (needMesh && scene.Meshes.Length == 0)
            throw new NotSupportedException(
                "A mesh pass (depth/normal/alpha/canny) was requested but the file has no mesh. " +
                "Use a skinned FBX/GLB (e.g. Mixamo 'with skin'), not an animation-only export.");

        int frameCount = o.FrameCount > 0
            ? o.FrameCount
            : Math.Max(1, (int)Math.Round(scene.DurationSeconds * o.Fps));

        log.WriteLine($"Loaded '{Path.GetFileName(o.InputPath)}': " +
                      $"{(scene.HasAnimation ? $"{scene.DurationSeconds:F2}s anim" : "static pose")}, " +
                      $"{(needMesh ? $"{scene.Meshes.Length} mesh(es), " : "")}" +
                      $"rendering {frameCount} frame(s) at {o.Fps} fps.");

        // Up-axis remap (Z-up rigs -> Y-up) used for both points and directions (normals).
        Func<Vector3, Vector3> upRemap = o.Up == UpAxis.Z
            ? v => new Vector3(v.X, v.Z, -v.Y)
            : v => v;

        // Evaluate all frames first (cheap) so the camera can frame the whole motion.
        var frames = new FrameState[frameCount];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int f = 0; f < frameCount; f++)
        {
            double t = frameCount > 1 ? f / o.Fps : 0;
            evaluator.EvaluateFull(t, out var raw, out var nodeGlobals, out _);

            // Space transform: up-axis remap -> scale -> root-motion centering.
            Vector3 offset = Vector3.Zero;
            if (o.Center != CenterMode.Off && raw.TryGetValue("hips", out var rawHips))
            {
                Vector3 hipsT = upRemap(rawHips) * o.Scale;
                offset = o.Center == CenterMode.Xyz ? hipsT : new Vector3(hipsT.X, 0f, hipsT.Z);
            }
            Vector3 PointXf(Vector3 v) => upRemap(v) * o.Scale - offset;
            Vector3 DirXf(Vector3 v) => upRemap(v);

            var joints = new Dictionary<string, Vector3>(raw.Count, StringComparer.Ordinal);
            foreach (var (k, v) in raw) joints[k] = PointXf(v);

            SkinnedMeshState? mesh = needMesh
                ? Skinner.Skin(scene.Meshes, nodeGlobals, PointXf, DirXf)
                : null;

            frames[f] = new FrameState
            {
                FrameIndex = f, TimeSeconds = (float)t, JointWorld = joints, Mesh = mesh,
            };
            foreach (var p in joints.Values) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }

        var camera = new Camera(o.PassConfig.Width, o.PassConfig.Height)
        {
            FovDegrees = o.FovDegrees,
            Orthographic = o.Orthographic,
            Mirror = o.Mirror,
        };
        Vector3 center = (min + max) * 0.5f, size = max - min;
        if (o.CamPos is { } camPos)
        {
            Vector3 target = o.CamTarget ?? center;
            Vector3 up = o.CamUp ?? Vector3.UnitY;
            float far = Vector3.Distance(camPos, target) * 4f + size.Length() + 1f;
            camera.OrthoHeight = MathF.Max(size.Y, size.X / camera.Aspect) * (1f + o.Margin) * o.Zoom;
            camera.SetView(camPos, target, up, far);
        }
        else
        {
            var (presetYaw, presetPitch) = Camera.PresetAngles(o.Camera);
            float yaw = o.Yaw ?? presetYaw;
            float pitch = o.Pitch ?? presetPitch;
            camera.AutoFrame(new List<Vector3> { min, max }, yaw, pitch, o.Margin, o.Zoom);
        }
        // Explicit near/far overrides (default: auto from framing).
        if (o.Near is { } nv) camera.Near = nv;
        if (o.Far is { } fv) camera.Far = fv;
        if (o.Near is not null || o.Far is not null) camera.BuildMatrices();

        Directory.CreateDirectory(o.OutputDir);
        using var gl = new GLContext(o.PassConfig.Width, o.PassConfig.Height);

        foreach (var pass in passes)
            pass.Initialize(gl, o.PassConfig);

        for (int f = 0; f < frameCount; f++)
        {
            foreach (var pass in passes)
            {
                gl.BeginFrame(o.Background.X, o.Background.Y, o.Background.Z);
                pass.RenderFrame(frames[f], camera);
                byte[] pixels = gl.ReadPixels();
                pass.PostProcess(pixels, o.PassConfig.Width, o.PassConfig.Height);
                string file = Path.Combine(o.OutputDir, $"{pass.Name}_{f:D4}.png");
                gl.WritePng(file, pixels);
            }
        }

        foreach (var pass in passes) pass.Dispose();

        if (o.JsonPath is not null) WriteJson(o.JsonPath, frames);

        log.WriteLine($"Done. Wrote {frameCount * passes.Length} image(s) to {o.OutputDir}");
        return frameCount;
    }

    private static void WriteJson(string path, FrameState[] frames)
    {
        var doc = frames.Select(fr => new
        {
            frame = fr.FrameIndex,
            time = fr.TimeSeconds,
            joints = fr.JointWorld.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.X, kv.Value.Y, kv.Value.Z }),
        });
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
