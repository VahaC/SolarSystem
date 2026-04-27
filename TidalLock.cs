using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S13: tidal-locking visualisation. For every moon that is tidally locked to its
/// host (Earth's Moon, the Galileans, Titan) we draw a short additive arrow on
/// the moon's near-side hub-axis, always pointing at its host planet. The arrow
/// is just a 3-segment line (shaft + two arrowhead diagonals) generated each
/// frame because both the moon and the host are constantly moving.
/// </summary>
public sealed class TidalLock : IDisposable
{
    public bool Enabled { get; set; }

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;
    private readonly List<float> _scratch = new(256);

    public void Initialize()
    {
        _shader = ShaderSources.CreateProgram("orbit.vert", "orbit.frag");
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Draw a tidal-lock arrow on every (moon, host) pair. <paramref name="pairs"/>
    /// is enumerated each frame so the host position is always current.</summary>
    public void Draw(Camera cam, IEnumerable<(Planet moon, Planet host)> pairs)
    {
        if (!Enabled) return;

        _scratch.Clear();
        foreach (var (moon, host) in pairs)
        {
            Vector3 dir = host.Position - moon.Position;
            float len = dir.Length;
            if (len < 1e-4f) continue;
            dir /= len;

            // Shaft from just outside the moon's surface to ~2.4×radius outward.
            float r = MathF.Max(moon.VisualRadius, 0.0001f);
            Vector3 a = moon.Position + dir * (r * 1.0f);
            Vector3 b = moon.Position + dir * (r * 2.4f);

            // Build a perpendicular for the arrowhead diagonals. Ecliptic-up usually
            // works; fall back to X-axis when the moon is near the celestial pole.
            Vector3 up = Vector3.UnitY;
            Vector3 perp = Vector3.Cross(dir, up);
            if (perp.LengthSquared < 1e-6f) perp = Vector3.Cross(dir, Vector3.UnitX);
            perp.Normalize();

            float head = r * 0.45f;
            Vector3 hL = b - dir * head + perp * head;
            Vector3 hR = b - dir * head - perp * head;

            void Seg(Vector3 p, Vector3 q)
            {
                _scratch.Add(p.X); _scratch.Add(p.Y); _scratch.Add(p.Z);
                _scratch.Add(q.X); _scratch.Add(q.Y); _scratch.Add(q.Z);
            }
            Seg(a, b);
            Seg(b, hL);
            Seg(b, hR);
        }

        if (_scratch.Count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetMatrix4("uModel", Matrix4.Identity);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetVector4("uColor", new Vector4(1.0f, 0.55f, 0.25f, 0.95f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        GL.LineWidth(1.6f);
        GL.DepthMask(false);

        var arr = _scratch.ToArray();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, _scratch.Count / 3);
        GL.BindVertexArray(0);

        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _shader?.Dispose();
        _vao = _vbo = 0;
    }
}
