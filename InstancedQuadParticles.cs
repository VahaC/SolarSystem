using OpenTK.Graphics.OpenGL4;

namespace SolarSystem;

/// <summary>
/// A4: shared "instanced billboard" mesh used by every CPU particle system. A static
/// 4-vertex unit-quad VBO is paired per-system with a per-instance VBO of
/// <c>vec4(pos.xyz, life01)</c>. The shader (<c>particle.vert</c>) consumes these via
/// vertex-attribute divisors and rasterises the quads as screen-space billboards,
/// replacing the legacy <c>GL_POINTS</c> + <c>gl_PointSize</c> path which was capped at
/// surprisingly small values on some drivers.
/// </summary>
public sealed class InstancedQuadParticles : IDisposable
{
    public int MaxInstances { get; }

    private int _quadVbo;          // 4 verts, location=0
    private int _instanceVbo;      // MaxInstances * vec4, locations=1,2 (divisor=1)
    private int _vao;

    private static int s_sharedQuadVbo; // Shared across every InstancedQuadParticles.

    public InstancedQuadParticles(int maxInstances)
    {
        MaxInstances = maxInstances;
    }

    public int Vao => _vao;
    public int InstanceVbo => _instanceVbo;

    public void Initialize()
    {
        if (s_sharedQuadVbo == 0)
        {
            // Triangle-strip ordered corners: BL, BR, TL, TR. Drawn with GL_TRIANGLE_STRIP.
            float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
            s_sharedQuadVbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, s_sharedQuadVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * sizeof(float), quad, BufferUsageHint.StaticDraw);
        }
        _quadVbo = s_sharedQuadVbo;

        _instanceVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, MaxInstances * 4 * sizeof(float),
            IntPtr.Zero, BufferUsageHint.DynamicDraw);

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        // Corner attribute (per-vertex).
        GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.VertexAttribDivisor(0, 0);

        // Per-instance: vec3 position + float life01, packed as vec4.
        GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceVbo);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.VertexAttribDivisor(1, 1);
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
        GL.VertexAttribDivisor(2, 1);

        GL.BindVertexArray(0);
    }

    /// <summary>Upload the first <paramref name="count"/> packed instances (vec4-each) and draw them.</summary>
    public void UploadInstances(float[] packed, int count)
    {
        if (count <= 0) return;
        GL.BindBuffer(BufferTarget.ArrayBuffer, _instanceVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, count * 4 * sizeof(float), packed);
    }

    public void DrawInstanced(int count)
    {
        if (count <= 0) return;
        GL.BindVertexArray(_vao);
        GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, count);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_instanceVbo != 0) GL.DeleteBuffer(_instanceVbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _instanceVbo = _vao = 0;
        // Note: we deliberately don't delete the shared quad VBO. It outlives any
        // single particle system and is cleaned up when the GL context dies.
    }
}
