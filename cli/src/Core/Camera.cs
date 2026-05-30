using System.Numerics;

namespace FbxControlNetConverter.Core;

public enum CameraPreset { Front, Back, Left, Right }

/// <summary>
/// Right-handed camera (perspective or orthographic) with auto-framing. Projects world
/// points to NDC (for GL line submission) or to pixel coordinates.
/// </summary>
public sealed class Camera
{
    public Vector3 Position { get; private set; }
    public Vector3 Target { get; private set; }
    public Vector3 Up { get; private set; } = Vector3.UnitY;
    public float FovDegrees { get; set; } = 45f;
    public float Near { get; set; } = 0.01f;
    public float Far { get; set; } = 2000f;
    public bool Orthographic { get; set; }
    public float OrthoHeight { get; set; } = 1f;   // world units visible vertically (ortho only)
    public bool Mirror { get; set; }               // horizontal flip in image space
    public int Width { get; }
    public int Height { get; }

    private Matrix4x4 _viewProj;
    private Matrix4x4 _view;

    /// <summary>Row-vector view matrix (worldRow * View = viewRow).</summary>
    public Matrix4x4 View => _view;
    /// <summary>Row-vector view*projection matrix.</summary>
    public Matrix4x4 ViewProj => _viewProj;

    public Camera(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public float Aspect => (float)Width / Height;

    /// <summary>Maps a preset to a (yaw, pitch) pair in degrees.</summary>
    public static (float Yaw, float Pitch) PresetAngles(CameraPreset p) => p switch
    {
        CameraPreset.Front => (0f, 0f),
        CameraPreset.Back => (180f, 0f),
        CameraPreset.Left => (-90f, 0f),
        CameraPreset.Right => (90f, 0f),
        _ => (0f, 0f),
    };

    /// <summary>Positions the camera to frame all <paramref name="points"/> from an arbitrary
    /// orbit angle. <paramref name="yawDeg"/> orbits around Y (0=front, 90=right, 180=back),
    /// <paramref name="pitchDeg"/> raises/lowers the camera, <paramref name="zoom"/> scales the
    /// fit (1=fit, &lt;1=closer/larger figure, &gt;1=farther/smaller).</summary>
    public void AutoFrame(IReadOnlyCollection<Vector3> points, float yawDeg, float pitchDeg,
                          float margin = 0.1f, float zoom = 1f)
    {
        if (points.Count == 0) { BuildMatrices(); return; }

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Vector3 center = (min + max) * 0.5f;
        Vector3 size = max - min;
        float z = MathF.Max(zoom, 0.01f);

        float fovRad = FovDegrees * MathF.PI / 180f;
        float halfH = MathF.Max(size.Y, 0.001f) * 0.5f * (1f + margin);
        float halfW = MathF.Max(size.X, 0.001f) * 0.5f * (1f + margin);
        float distH = halfH / MathF.Tan(fovRad * 0.5f);
        float distW = (halfW / Aspect) / MathF.Tan(fovRad * 0.5f);
        float dist = (MathF.Max(distH, distW) + size.Z * 0.5f) * z;

        // Orthographic size fits the figure regardless of distance.
        OrthoHeight = MathF.Max(size.Y, size.X / Aspect) * (1f + margin) * z;

        float yaw = yawDeg * MathF.PI / 180f;
        float pitch = pitchDeg * MathF.PI / 180f;
        Vector3 dir = new(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));
        Up = MathF.Abs(pitchDeg) > 89.5f ? new Vector3(0, 0, -MathF.Sign(pitchDeg)) : Vector3.UnitY;

        Target = center;
        Position = center + dir * dist;
        Far = dist * 4f + size.Length();
        BuildMatrices();
    }

    /// <summary>Sets the camera from explicit world-space coordinates, bypassing auto-frame.
    /// Coordinates are in the same space as the dumped joint positions (post-centering).</summary>
    public void SetView(Vector3 position, Vector3 target, Vector3 up, float far)
    {
        Position = position;
        Target = target;
        Up = up.LengthSquared() > 1e-6f ? Vector3.Normalize(up) : Vector3.UnitY;
        Far = MathF.Max(far, 1f);
        BuildMatrices();
    }

    public void BuildMatrices()
    {
        _view = Matrix4x4.CreateLookAt(Position, Target, Up);
        var proj = Orthographic
            ? Matrix4x4.CreateOrthographic(MathF.Max(OrthoHeight, 1e-3f) * Aspect,
                                           MathF.Max(OrthoHeight, 1e-3f), Near, Far)
            : Matrix4x4.CreatePerspectiveFieldOfView(FovDegrees * MathF.PI / 180f, Aspect, Near, Far);
        // Bake horizontal mirror into projection so GPU mesh passes and CPU pose projection
        // flip consistently.
        if (Mirror) proj *= Matrix4x4.CreateScale(-1f, 1f, 1f);
        _viewProj = _view * proj;
    }

    /// <summary>Projects a world point to normalized device coords. Returns false if behind camera.</summary>
    public bool ToNdc(Vector3 world, out Vector2 ndc)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), _viewProj);
        if (clip.W <= 1e-6f) { ndc = default; return false; }
        ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        return true;
    }

    /// <summary>Projects to pixel coordinates (origin top-left, +Y down). Mirror is already
    /// baked into the projection matrix.</summary>
    public bool ToPixel(Vector3 world, out Vector2 px)
    {
        if (!ToNdc(world, out var ndc)) { px = default; return false; }
        px = new Vector2((ndc.X * 0.5f + 0.5f) * Width, (1f - (ndc.Y * 0.5f + 0.5f)) * Height);
        return true;
    }
}
