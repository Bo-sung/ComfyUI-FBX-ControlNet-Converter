using System.Numerics;
using FbxControlNetConverter.Core.Rendering;
using Silk.NET.OpenGL;

namespace FbxControlNetConverter.Core.Passes;

/// <summary>
/// Base for skinned-mesh passes. Uploads interleaved (pos, normal) + indices each frame and
/// draws with the camera's MVP. Subclasses supply the fragment shader (depth/normal/alpha).
/// </summary>
public abstract unsafe class MeshPassBase : IRenderPass
{
    public abstract string Name { get; }
    public bool RequiresMesh => true;

    protected GLContext GlCtx = null!;
    protected PassConfig Cfg = null!;
    private uint _prog, _vao, _vbo, _ebo;
    private int _locMvp, _locView;
    protected uint Program => _prog;

    private const string VertSrc = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNormal;
        uniform mat4 uMVP;
        uniform mat4 uView;
        out vec3 vNormalView;
        out float vViewZ;
        void main() {
            gl_Position = uMVP * vec4(aPos, 1.0);
            vNormalView = mat3(uView) * aNormal;
            vViewZ = -(uView * vec4(aPos, 1.0)).z;   // positive distance in front of camera
        }
        """;

    protected abstract string FragmentSrc { get; }

    public void Initialize(GLContext gl, PassConfig config)
    {
        GlCtx = gl;
        Cfg = config;
        var g = gl.Gl;
        _prog = gl.CompileProgram(VertSrc, FragmentSrc);
        _locMvp = g.GetUniformLocation(_prog, "uMVP");
        _locView = g.GetUniformLocation(_prog, "uView");

        _vao = g.GenVertexArray();
        _vbo = g.GenBuffer();
        _ebo = g.GenBuffer();
        g.BindVertexArray(_vao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        uint stride = 6 * sizeof(float);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.BindVertexArray(0);

        OnInitialize(g);
    }

    protected virtual void OnInitialize(GL g) { }
    protected virtual void OnSetUniforms(GL g, FrameState frame, Camera camera) { }

    public void RenderFrame(FrameState frame, Camera camera)
    {
        if (frame.Mesh is not { } mesh || mesh.Indices.Length == 0) return;
        var g = GlCtx.Gl;

        // Interleave position + normal.
        int n = mesh.Positions.Length;
        var verts = new float[n * 6];
        for (int i = 0; i < n; i++)
        {
            var p = mesh.Positions[i]; var nm = mesh.Normals[i];
            int o = i * 6;
            verts[o] = p.X; verts[o + 1] = p.Y; verts[o + 2] = p.Z;
            verts[o + 3] = nm.X; verts[o + 4] = nm.Y; verts[o + 5] = nm.Z;
        }
        var idx = Array.ConvertAll(mesh.Indices, x => (uint)x);

        g.Enable(EnableCap.DepthTest);
        g.UseProgram(_prog);
        g.BindVertexArray(_vao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.DynamicDraw);
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.DynamicDraw);

        var mvp = GLContext.ToGl(camera.ViewProj);
        var view = GLContext.ToGl(camera.View);
        fixed (float* pm = mvp) g.UniformMatrix4(_locMvp, 1, false, pm);
        fixed (float* pv = view) g.UniformMatrix4(_locView, 1, false, pv);
        OnSetUniforms(g, frame, camera);

        g.DrawElements(PrimitiveType.Triangles, (uint)idx.Length, DrawElementsType.UnsignedInt, (void*)0);
        g.BindVertexArray(0);
    }

    public virtual void PostProcess(byte[] rgba, int width, int height) { }

    public virtual void Dispose()
    {
        var g = GlCtx?.Gl;
        if (g == null) return;
        if (_prog != 0) g.DeleteProgram(_prog);
        if (_vao != 0) g.DeleteVertexArray(_vao);
        if (_vbo != 0) g.DeleteBuffer(_vbo);
        if (_ebo != 0) g.DeleteBuffer(_ebo);
    }
}

/// <summary>Grayscale depth: nearer = brighter. Range is the figure's per-frame view-Z extent.</summary>
public sealed unsafe class DepthPass : MeshPassBase
{
    public override string Name => "depth";
    private int _locNear, _locFar;

    protected override string FragmentSrc => """
        #version 330 core
        in float vViewZ;
        uniform float uNear;
        uniform float uFar;
        out vec4 FragColor;
        void main() {
            float d = clamp((uFar - vViewZ) / max(uFar - uNear, 1e-4), 0.0, 1.0);
            FragColor = vec4(vec3(d), 1.0);
        }
        """;

    protected override void OnInitialize(GL g)
    {
        _locNear = g.GetUniformLocation(Program, "uNear");
        _locFar = g.GetUniformLocation(Program, "uFar");
    }

    protected override void OnSetUniforms(GL g, FrameState frame, Camera camera)
    {
        // Per-frame view-space depth range over the mesh for good contrast.
        float zmin = float.MaxValue, zmax = float.MinValue;
        var view = camera.View;
        foreach (var p in frame.Mesh!.Positions)
        {
            float z = -Vector3.Transform(p, view).Z;
            if (z < zmin) zmin = z;
            if (z > zmax) zmax = z;
        }
        g.Uniform1(_locNear, zmin);
        g.Uniform1(_locFar, zmax);
    }
}

/// <summary>View-space normals encoded as RGB (n*0.5+0.5).</summary>
public sealed class NormalPass : MeshPassBase
{
    public override string Name => "normal";
    protected override string FragmentSrc => """
        #version 330 core
        in vec3 vNormalView;
        out vec4 FragColor;
        void main() {
            vec3 n = normalize(vNormalView);
            FragColor = vec4(n * 0.5 + 0.5, 1.0);
        }
        """;
}

/// <summary>White silhouette on the (black) background — a matte mask.</summary>
public sealed class AlphaPass : MeshPassBase
{
    public override string Name => "alpha";
    protected override string FragmentSrc => """
        #version 330 core
        out vec4 FragColor;
        void main() { FragColor = vec4(1.0, 1.0, 1.0, 1.0); }
        """;
}

/// <summary>Edge map (Canny-like): renders view normals, then a Sobel + threshold in PostProcess.</summary>
public sealed class CannyPass : MeshPassBase
{
    public override string Name => "canny";
    protected override string FragmentSrc => """
        #version 330 core
        in vec3 vNormalView;
        out vec4 FragColor;
        void main() {
            vec3 n = normalize(vNormalView);
            FragColor = vec4(n * 0.5 + 0.5, 1.0);
        }
        """;

    public override void PostProcess(byte[] rgba, int width, int height)
    {
        // Sobel on luminance of the rendered normal image -> white edges on black.
        var lum = new float[width * height];
        for (int i = 0; i < width * height; i++)
            lum[i] = 0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2];

        var outBuf = new byte[rgba.Length];
        const float threshold = 48f;
        for (int y = 1; y < height - 1; y++)
        for (int x = 1; x < width - 1; x++)
        {
            int i = y * width + x;
            float gx = lum[i - width - 1] + 2 * lum[i - 1] + lum[i + width - 1]
                     - lum[i - width + 1] - 2 * lum[i + 1] - lum[i + width + 1];
            float gy = lum[i - width - 1] + 2 * lum[i - width] + lum[i - width + 1]
                     - lum[i + width - 1] - 2 * lum[i + width] - lum[i + width + 1];
            byte e = MathF.Sqrt(gx * gx + gy * gy) >= threshold ? (byte)255 : (byte)0;
            int o = i * 4;
            outBuf[o] = e; outBuf[o + 1] = e; outBuf[o + 2] = e; outBuf[o + 3] = 255;
        }
        Array.Copy(outBuf, rgba, rgba.Length);
    }
}
