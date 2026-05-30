using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FbxControlNetConverter.Core.Rendering;

/// <summary>
/// Headless OpenGL 3.3 context backed by a hidden window, rendering into an offscreen
/// framebuffer. Single rendering engine shared by every <see cref="IRenderPass"/>.
/// (Standard GL so the layer ports to C++/GLFW with little change.)
/// </summary>
public sealed unsafe class GLContext : IDisposable
{
    private readonly IWindow _window;
    private uint _fbo, _colorTex, _depthRb;

    public GL Gl { get; }
    public int Width { get; }
    public int Height { get; }

    public GLContext(int width, int height)
    {
        Width = width;
        Height = height;

        var options = WindowOptions.Default;
        options.IsVisible = false;
        options.Size = new Vector2D<int>(width, height);
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core,
            ContextFlags.ForwardCompatible, new APIVersion(3, 3));

        _window = Window.Create(options);
        _window.Initialize();
        Gl = GL.GetApi(_window);

        CreateFramebuffer();
        Gl.Viewport(0, 0, (uint)width, (uint)height);
        Gl.Enable(EnableCap.DepthTest);
        Gl.Enable(EnableCap.Blend);
        Gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    private void CreateFramebuffer()
    {
        _fbo = Gl.GenFramebuffer();
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorTex = Gl.GenTexture();
        Gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        Gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);

        _depthRb = Gl.GenRenderbuffer();
        Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRb);
        Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)Width, (uint)Height);
        Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRb);

        var status = Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Framebuffer incomplete: {status}");
    }

    /// <summary>Binds the offscreen FBO and clears it to the given color.</summary>
    public void BeginFrame(float r, float g, float b, float a = 1f)
    {
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        Gl.Viewport(0, 0, (uint)Width, (uint)Height);
        Gl.ClearColor(r, g, b, a);
        Gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    }

    /// <summary>Reads the framebuffer back as RGBA bytes (GL bottom-left origin, not flipped).</summary>
    public byte[] ReadPixels()
    {
        var pixels = new byte[Width * Height * 4];
        Gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte,
            new Span<byte>(pixels));
        return pixels;
    }

    /// <summary>Encodes RGBA bytes (GL bottom-left origin) to a PNG, flipping vertically.</summary>
    public void WritePng(string path, byte[] pixels)
    {
        using var image = new Image<Rgba32>(Width, Height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < Height; y++)
            {
                int srcRow = (Height - 1 - y) * Width * 4; // flip vertically
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < Width; x++)
                {
                    int i = srcRow + x * 4;
                    row[x] = new Rgba32(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                }
            }
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        image.Save(path);
    }

    /// <summary>Reads the framebuffer back and writes a PNG (flips GL's bottom-left origin).</summary>
    public void SavePng(string path)
    {
        var pixels = new byte[Width * Height * 4];
        Gl.ReadPixels(0, 0, (uint)Width, (uint)Height, PixelFormat.Rgba, PixelType.UnsignedByte,
            new Span<byte>(pixels));

        using var image = new Image<Rgba32>(Width, Height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < Height; y++)
            {
                int srcRow = (Height - 1 - y) * Width * 4; // flip vertically
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < Width; x++)
                {
                    int i = srcRow + x * 4;
                    row[x] = new Rgba32(pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
                }
            }
        });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        image.Save(path);
    }

    /// <summary>Row-major element order of a System.Numerics (row-vector) matrix. Uploaded with
    /// transpose=false, GLSL reads it column-major => it sees the column-vector transform that
    /// makes gl_Position = U * vec4(world,1) correct.</summary>
    public static float[] ToGl(System.Numerics.Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };

    public uint CompileProgram(string vertexSrc, string fragmentSrc)
    {
        uint vs = CompileShader(ShaderType.VertexShader, vertexSrc);
        uint fs = CompileShader(ShaderType.FragmentShader, fragmentSrc);
        uint prog = Gl.CreateProgram();
        Gl.AttachShader(prog, vs);
        Gl.AttachShader(prog, fs);
        Gl.LinkProgram(prog);
        Gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException("Program link failed: " + Gl.GetProgramInfoLog(prog));
        Gl.DeleteShader(vs);
        Gl.DeleteShader(fs);
        return prog;
    }

    private uint CompileShader(ShaderType type, string src)
    {
        uint sh = Gl.CreateShader(type);
        Gl.ShaderSource(sh, src);
        Gl.CompileShader(sh);
        Gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException($"{type} compile failed: " + Gl.GetShaderInfoLog(sh));
        return sh;
    }

    public void Dispose()
    {
        Gl.DeleteFramebuffer(_fbo);
        Gl.DeleteTexture(_colorTex);
        Gl.DeleteRenderbuffer(_depthRb);
        _window.Dispose();
    }
}
