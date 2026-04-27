using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S14: heliocentric alignment indicator. Each frame the major planets' ecliptic
/// longitudes are computed; a simple union-find groups planets whose pairwise
/// angular separation is below <see cref="ThresholdDeg"/>. Components of size
/// ≥ 3 are reported as <see cref="ActiveGroups"/>; the renderer draws an additive
/// line from the Sun out through the outermost member of each group, plus a
/// banner naming the participants.
/// </summary>
public sealed class PlanetaryAlignment : IDisposable
{
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum heliocentric-longitude spread tolerated within a group.
    /// 12° is loose enough to catch the historic Mar-2023 quintet but tight
    /// enough to ignore everyday near-misses.</summary>
    public float ThresholdDeg { get; set; } = 12f;

    public readonly struct Group
    {
        public readonly int[] PlanetIndices;
        public readonly string Names;
        public Group(int[] idx, string names) { PlanetIndices = idx; Names = names; }
    }

    public IReadOnlyList<Group> ActiveGroups => _groups;
    private readonly List<Group> _groups = new();

    private int _vao, _vbo;
    private ShaderProgram _shader = null!;
    private readonly List<float> _scratch = new(64);

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

    /// <summary>Recompute alignment groups from the current planet world positions.
    /// <paramref name="planets"/> should contain only the major (non-dwarf) bodies.</summary>
    public void Update(Planet[] planets)
    {
        _groups.Clear();
        if (planets.Length == 0) return;

        // Heliocentric ecliptic longitude (deg) from world-space (x, _, z) where
        // OpenGL Z = -y_ecliptic. Sun assumed at world origin.
        var lon = new float[planets.Length];
        for (int i = 0; i < planets.Length; i++)
        {
            float x = planets[i].Position.X;
            float z = planets[i].Position.Z;
            lon[i] = MathHelper.RadiansToDegrees(MathF.Atan2(-z, x));
        }

        // Union-Find pairwise within ThresholdDeg.
        var parent = new int[planets.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }

        for (int i = 0; i < planets.Length; i++)
            for (int j = i + 1; j < planets.Length; j++)
            {
                float d = AngDiff(lon[i], lon[j]);
                if (d <= ThresholdDeg) Union(i, j);
            }

        var buckets = new Dictionary<int, List<int>>();
        for (int i = 0; i < planets.Length; i++)
        {
            int root = Find(i);
            if (!buckets.TryGetValue(root, out var lst)) { lst = new List<int>(); buckets[root] = lst; }
            lst.Add(i);
        }
        foreach (var kv in buckets)
        {
            if (kv.Value.Count < 3) continue;
            kv.Value.Sort((a, b) => planets[a].SemiMajorAxisAU.CompareTo(planets[b].SemiMajorAxisAU));
            var names = string.Join(", ", kv.Value.ConvertAll(i => planets[i].Name));
            _groups.Add(new Group(kv.Value.ToArray(), names));
        }
    }

    private static float AngDiff(float a, float b)
    {
        float d = ((a - b) % 360f + 540f) % 360f - 180f;
        return MathF.Abs(d);
    }

    public void Draw(Camera cam, Planet[] planets)
    {
        if (!Enabled || _groups.Count == 0) return;

        _scratch.Clear();
        foreach (var g in _groups)
        {
            // Build a single straight ray at the group's mean ecliptic longitude
            // instead of zig-zagging through each planet — union-find groups can
            // span up to (N-1)·threshold by transitivity, so connecting members
            // sequentially produced a visible kink. Vector-mean direction in XZ
            // gives the right "average alignment axis"; the ray is extended to
            // 1.15× the farthest member so every body in the group sits on it.
            float sx = 0f, sz = 0f, maxLen = 0f;
            foreach (var idx in g.PlanetIndices)
            {
                var pp = planets[idx].Position;
                float l = MathF.Sqrt(pp.X * pp.X + pp.Z * pp.Z);
                if (l > 1e-6f) { sx += pp.X / l; sz += pp.Z / l; }
                if (l > maxLen) maxLen = l;
            }
            float aLen = MathF.Sqrt(sx * sx + sz * sz);
            if (aLen < 1e-4f || maxLen < 1e-4f) continue;
            Vector3 dir = new Vector3(sx / aLen, 0f, sz / aLen);
            Vector3 end = dir * (maxLen * 1.15f);

            _scratch.Add(0f); _scratch.Add(0f); _scratch.Add(0f);
            _scratch.Add(end.X); _scratch.Add(end.Y); _scratch.Add(end.Z);
        }

        if (_scratch.Count == 0) return;

        _shader.Use();
        _shader.SetMatrix4("uView", cam.ViewMatrix);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetMatrix4("uModel", Matrix4.Identity);
        _shader.SetFloat("uFcoef", 2.0f / MathF.Log2(cam.Far + 1.0f));
        _shader.SetVector4("uColor", new Vector4(1f, 0.85f, 0.45f, 0.85f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        GL.LineWidth(1.5f);
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
