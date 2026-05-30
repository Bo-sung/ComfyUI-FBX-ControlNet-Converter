using System.Numerics;

namespace FbxControlNetConverter.Core.Rendering;

/// <summary>Per-frame evaluated rig state handed to every pass.</summary>
public sealed class FrameState
{
    public int FrameIndex { get; init; }
    public float TimeSeconds { get; init; }

    /// <summary>Canonical joint name -> world-space position for this frame.
    /// Populated by <see cref="PoseEvaluator"/>. Drives OpenPose/hand passes.</summary>
    public required IReadOnlyDictionary<string, Vector3> JointWorld { get; init; }

    /// <summary>Skinned mesh vertices/normals for mesh passes (depth/normal/alpha).
    /// Null in the OpenPose-only phase 1; populated when the mesh module lands.</summary>
    public SkinnedMeshState? Mesh { get; init; }
}

/// <summary>Placeholder for phase-2 mesh passes. Kept so the plugin contract is stable now.</summary>
public sealed class SkinnedMeshState
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required int[] Indices { get; init; }
}

/// <summary>Shared, pass-agnostic render configuration.</summary>
public sealed class PassConfig
{
    public int Width { get; init; } = 768;
    public int Height { get; init; } = 768;
    public bool DrawBody { get; init; } = true;
    public bool DrawHands { get; init; } = true;
    public bool DrawFace { get; init; } = true;
    public float LineThicknessPx { get; init; } = 4f;
    public float JointRadiusPx { get; init; } = 4f;
}

/// <summary>
/// A render pass plugin. Every output (OpenPose now; depth/normal/alpha/canny later)
/// implements this and draws into the bound offscreen framebuffer.
/// </summary>
public interface IRenderPass : IDisposable
{
    /// <summary>Stable identifier used on the CLI (e.g. "openpose", "depth").</summary>
    string Name { get; }

    /// <summary>True if this pass needs <see cref="FrameState.Mesh"/> (skinned geometry).</summary>
    bool RequiresMesh { get; }

    /// <summary>Called once after the GL context exists.</summary>
    void Initialize(GLContext gl, PassConfig config);

    /// <summary>Draws one frame into the currently bound framebuffer (already cleared).</summary>
    void RenderFrame(FrameState frame, Camera camera);

    /// <summary>Optional CPU post-process on the read-back RGBA buffer before PNG encode
    /// (GL bottom-left origin). Default: no-op. Used by edge passes like Canny.</summary>
    void PostProcess(byte[] rgba, int width, int height) { }
}
