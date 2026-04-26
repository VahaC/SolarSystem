using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// V13: aurora ribbons drawn at a host planet's geomagnetic poles. The mesh is
/// a thin triangle-strip ring (top edge = bright, bottom edge = transparent)
/// rebuilt per-vertex in <c>aurora.vert</c> from an angle around the rotation
/// axis and a 0/1 side flag, so the same VBO renders both poles of any planet
/// at any scale.
///
/// Colour and intensity are passed in by the caller (Earth → cool green,
/// Jupiter → magenta/violet); the per-frame ripple in the shader is driven by
/// the global GLFW time clock so it keeps animating even while the simulation
/// is paused.
/// </summary>
public sealed class Aurora : IDisposable
{
    private const int Segments = 128;
    private int _vao, _vbo;
    private int _vertexCount;
    private ShaderProgram _shader = null!;

    /// <summary>Master toggle. When false every <see cref="DrawForBody"/> is a no-op.</summary>
    public bool Enabled { get; set; } = true;

    public void Initialize()
    {
        // Two vertices per angular sample: top edge (side=0) then bottom edge
        // (side=1). Triangle strip across consecutive samples builds the ribbon.
        _vertexCount = (Segments + 1) * 2;
        var data = new float[_vertexCount * 2];
        for (int i = 0; i <= Segments; i++)
        {
            float angle = (float)i / Segments * MathF.PI * 2f;
            int b = i * 4;
            data[b + 0] = angle; data[b + 1] = 0f; // top
            data[b + 2] = angle; data[b + 3] = 1f; // bottom
        }
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 1, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 2 * sizeof(float), sizeof(float));
        GL.BindVertexArray(0);

        _shader = ShaderSources.CreateProgram("aurora.vert", "aurora.frag");
    }

    /// <summary>Draws aurora ribbons at both poles of the host body.</summary>
    /// <param name="cam">Active camera (for view/proj + log-depth coefficient).</param>
    /// <param name="center">Host planet centre in world space.</param>
    /// <param name="radius">Host planet visual radius (the ribbon is at ~1.04 × this).</param>
    /// <param name="tiltDeg">Host planet axial tilt around Z so the ribbon tracks its pole.</param>
    /// <param name="color">RGBA tint; alpha scales the overall opacity.</param>
    /// <param name="intensity">Master intensity multiplier (lets callers fade with solar wind, etc.).</param>
    /// <param name="time">Animation phase in seconds — typically <c>GLFW.GetTime()</c>.</param>
    public void DrawForBody(Camera cam, Vector3 center, float radius, float tiltDeg,
                            Vector4 color, float intensity, float time)
    {
        if (!Enabled || intensity <= 0f) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetFloat("uFcoef", 2f / MathF.Log2(cam.Far + 1f));
        _shader.SetFloat("uTime", time);
        _shader.SetFloat("uIntensity", intensity);
        _shader.SetVector4("uColor", color);

        // Lift the ribbon slightly above the surface so it doesn't z-fight the
        // planet, even with logarithmic depth.
        var model = Matrix4.CreateScale(radius * 1.04f)
                    * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(tiltDeg))
                    * Matrix4.CreateTranslation(center);
        _shader.SetMatrix4("uModel", model);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive
        GL.DepthMask(false);
        GL.Disable(EnableCap.CullFace);
        GL.BindVertexArray(_vao);

        _shader.SetFloat("uHemisphere", 1f);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, _vertexCount);
        _shader.SetFloat("uHemisphere", -1f);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, _vertexCount);

        GL.BindVertexArray(0);
        GL.Enable(EnableCap.CullFace);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
    }
}
