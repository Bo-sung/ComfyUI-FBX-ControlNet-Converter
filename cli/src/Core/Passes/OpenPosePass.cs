using System.Numerics;
using FbxControlNetConverter.Core.Rendering;
using Silk.NET.OpenGL;

namespace FbxControlNetConverter.Core.Passes;

/// <summary>
/// OpenPose stick-figure pass. Projects rig joints to screen and submits limbs (as
/// thick quads) and joints (as disks) to GL. No mesh required — pure keypoint
/// reconstruction (strategy B). Body (BODY_18) + both hands; face 70 is a later
/// FacePass, but the BODY_18 face points (nose/eyes/ears) are approximated from the head joint.
/// </summary>
public sealed unsafe class OpenPosePass : IRenderPass
{
    public string Name => "openpose";
    public bool RequiresMesh => false;

    private GLContext _gl = null!;
    private PassConfig _cfg = null!;
    private uint _prog, _vao, _vbo;
    private readonly List<float> _verts = new(4096);

    private const string VertSrc = """
        #version 330 core
        layout(location=0) in vec2 aPos;   // NDC
        layout(location=1) in vec3 aColor;
        out vec3 vColor;
        void main() { vColor = aColor; gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    private const string FragSrc = """
        #version 330 core
        in vec3 vColor;
        out vec4 FragColor;
        void main() { FragColor = vec4(vColor, 1.0); }
        """;

    public void Initialize(GLContext gl, PassConfig config)
    {
        _gl = gl;
        _cfg = config;
        var g = gl.Gl;

        _prog = gl.CompileProgram(VertSrc, FragSrc);
        _vao = g.GenVertexArray();
        _vbo = g.GenBuffer();
        g.BindVertexArray(_vao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        uint stride = 5 * sizeof(float);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        g.BindVertexArray(0);
    }

    public void RenderFrame(FrameState frame, Camera camera)
    {
        _verts.Clear();
        BuildBody(frame, camera);
        if (_cfg.DrawHands)
        {
            BuildHand(frame, camera, OpenPoseSkeleton.BuildHand("lefthand"));
            BuildHand(frame, camera, OpenPoseSkeleton.BuildHand("righthand"));
        }
        // DrawFace (full 70 landmarks) handled by a dedicated FacePass later.

        if (_verts.Count == 0) return;

        var g = _gl.Gl;
        g.Disable(EnableCap.DepthTest); // flat 2D overlay
        g.UseProgram(_prog);
        g.BindVertexArray(_vao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        ReadOnlySpan<float> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verts);
        g.BufferData(BufferTargetARB.ArrayBuffer, span, BufferUsageARB.DynamicDraw);
        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_verts.Count / 5));
        g.BindVertexArray(0);
        g.Enable(EnableCap.DepthTest);
    }

    private void BuildBody(FrameState frame, Camera camera)
    {
        var px = new Vector2?[18];
        for (int i = 0; i < 18; i++)
        {
            var kp = OpenPoseSkeleton.Body[i];
            Vector3? world = kp.CanonicalBone is { } bone
                ? (frame.JointWorld.TryGetValue(bone, out var w) ? w : null)
                : ApproxFace(i, frame, camera);
            if (world is { } wp && camera.ToPixel(wp, out var p)) px[i] = p;
        }

        foreach (var limb in OpenPoseSkeleton.BodyLimbs)
            if (px[limb.A] is { } a && px[limb.B] is { } b)
                AddLine(a, b, limb.Color, _cfg.LineThicknessPx);

        for (int i = 0; i < 18; i++)
            if (px[i] is { } p) AddDisk(p, OpenPoseSkeleton.Body[i].Color, _cfg.JointRadiusPx);
    }

    private void BuildHand(FrameState frame, Camera camera, Keypoint[] hand)
    {
        var px = new Vector2?[hand.Length];
        for (int i = 0; i < hand.Length; i++)
            if (hand[i].CanonicalBone is { } bone &&
                frame.JointWorld.TryGetValue(bone, out var w) && camera.ToPixel(w, out var p))
                px[i] = p;

        foreach (var limb in OpenPoseSkeleton.HandLimbs)
            if (px[limb.A] is { } a && px[limb.B] is { } b)
                AddLine(a, b, limb.Color, _cfg.LineThicknessPx * 0.6f);

        for (int i = 0; i < hand.Length; i++)
            if (px[i] is { } p) AddDisk(p, hand[i].Color, _cfg.JointRadiusPx * 0.6f);
    }

    /// <summary>Rough nose/eye/ear placement from the head joint, spread along the
    /// camera-horizontal axis. Stand-in until FacePass; pure-FBX has no facial mocap.</summary>
    private Vector3? ApproxFace(int bodyIndex, FrameState frame, Camera camera)
    {
        if (!frame.JointWorld.TryGetValue("head", out var head)) return null;
        Vector3 up = frame.JointWorld.TryGetValue("neck", out var neck)
            ? Vector3.Normalize(head - neck) : Vector3.UnitY;
        float hs = frame.JointWorld.TryGetValue("neck", out var nk)
            ? Vector3.Distance(head, nk) * 0.5f : 0.1f;
        Vector3 fwd = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(up, fwd));

        return bodyIndex switch
        {
            0 => head + up * (hs * 0.2f),                 // nose
            14 => head + right * (hs * 0.3f) + up * hs,   // right eye
            15 => head - right * (hs * 0.3f) + up * hs,   // left eye
            16 => head + right * (hs * 0.6f) + up * hs,   // right ear
            17 => head - right * (hs * 0.6f) + up * hs,   // left ear
            _ => null,
        };
    }

    // ---- geometry helpers (build triangles in NDC) -----------------------------
    private Vector2 ToNdc(Vector2 px) =>
        new(2f * px.X / _cfg.Width - 1f, 1f - 2f * px.Y / _cfg.Height);

    private void Push(Vector2 ndc, Rgb c)
    {
        _verts.Add(ndc.X); _verts.Add(ndc.Y);
        _verts.Add(c.R / 255f); _verts.Add(c.G / 255f); _verts.Add(c.B / 255f);
    }

    private void AddLine(Vector2 a, Vector2 b, Rgb color, float thicknessPx)
    {
        Vector2 d = b - a;
        if (d.LengthSquared() < 1e-6f) return;
        d = Vector2.Normalize(d);
        Vector2 n = new Vector2(-d.Y, d.X) * (thicknessPx * 0.5f);
        Vector2 p0 = ToNdc(a + n), p1 = ToNdc(a - n), p2 = ToNdc(b - n), p3 = ToNdc(b + n);
        Push(p0, color); Push(p1, color); Push(p2, color);
        Push(p0, color); Push(p2, color); Push(p3, color);
    }

    private void AddDisk(Vector2 center, Rgb color, float radiusPx, int segments = 14)
    {
        Vector2 c = ToNdc(center);
        Vector2 prev = ToNdc(center + new Vector2(radiusPx, 0));
        for (int i = 1; i <= segments; i++)
        {
            float ang = i / (float)segments * MathF.PI * 2f;
            Vector2 cur = ToNdc(center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radiusPx);
            Push(c, color); Push(prev, color); Push(cur, color);
            prev = cur;
        }
    }

    public void Dispose()
    {
        if (_prog != 0) _gl.Gl.DeleteProgram(_prog);
        if (_vao != 0) _gl.Gl.DeleteVertexArray(_vao);
        if (_vbo != 0) _gl.Gl.DeleteBuffer(_vbo);
    }
}
