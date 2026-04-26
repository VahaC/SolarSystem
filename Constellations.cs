using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SolarSystem;

/// <summary>
/// S8: Constellation overlay. Loads RA/Dec line endpoints from
/// <c>data/constellations.json</c>, projects them onto a unit celestial sphere
/// and renders them skybox-style (translation stripped from the view matrix +
/// depth forced to the far plane) so the figures stay locked to the background
/// stars regardless of camera position or scale mode.
/// </summary>
public sealed class Constellations : IDisposable
{
    public bool Enabled { get; set; }

    private int _vao, _vbo, _vertexCount;
    private ShaderProgram _shader = null!;
    private Entry[] _entries = Array.Empty<Entry>();

    public IReadOnlyList<Entry> Entries => _entries;

    public readonly struct Entry
    {
        public readonly string Name;
        /// <summary>Unit-vector direction toward the constellation's label anchor on the celestial sphere.</summary>
        public readonly Vector3 LabelDir;
        public Entry(string name, Vector3 labelDir) { Name = name; LabelDir = labelDir; }
    }

    public void Initialize()
    {
        _shader = new ShaderProgram(Vs, Fs);

        var data = LoadFromJson();
        var verts = new List<float>(data.Sum(d => d.Lines.Length * 6));
        var entries = new List<Entry>(data.Count);
        foreach (var c in data)
        {
            foreach (var line in c.Lines)
            {
                if (line.Length < 2) continue;
                var a = RaDecToUnit(line[0][0], line[0][1]);
                var b = RaDecToUnit(line[1][0], line[1][1]);
                verts.Add(a.X); verts.Add(a.Y); verts.Add(a.Z);
                verts.Add(b.X); verts.Add(b.Y); verts.Add(b.Z);
            }
            entries.Add(new Entry(c.Name ?? "?", RaDecToUnit(c.LabelRaHours, c.LabelDecDeg)));
        }
        _entries = entries.ToArray();
        _vertexCount = verts.Count / 3;

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        var arr = verts.ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr,
            BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Convert (RA hours, Dec degrees) → unit direction in the same right-handed
    /// Y-up world frame the camera uses. Constellations sit on a unit celestial sphere
    /// and the renderer scales/strips translation so they appear at infinity.</summary>
    private static Vector3 RaDecToUnit(double raHours, double decDeg)
    {
        double ra = raHours * 15.0 * Math.PI / 180.0;
        double dec = decDeg * Math.PI / 180.0;
        double cd = Math.Cos(dec);
        // RA increases eastward; the chosen mapping below keeps north (+Dec) along +Y
        // and the vernal-equinox direction (RA=0) along +X.
        return new Vector3(
            (float)(cd * Math.Cos(ra)),
            (float)Math.Sin(dec),
            (float)(-cd * Math.Sin(ra)));
    }

    public void Draw(Camera cam)
    {
        if (!Enabled || _vertexCount < 2) return;

        // Skybox-style: strip translation so the constellations stay fixed to the
        // background no matter where the camera is.
        var view = cam.ViewMatrix;
        view.M41 = 0; view.M42 = 0; view.M43 = 0;

        _shader.Use();
        _shader.SetMatrix4("uView", view);
        _shader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _shader.SetVector4("uColor", new Vector4(0.55f, 0.75f, 1.0f, 0.55f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.Disable(EnableCap.DepthTest);
        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _vertexCount);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    private static List<ConstellationDto> LoadFromJson()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "data", "constellations.json");
        if (!File.Exists(path))
        {
            Debug.WriteLine($"[constellations.json] not found at '{path}'");
            return new List<ConstellationDto>();
        }
        try
        {
            using var fs = File.OpenRead(path);
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var doc = JsonSerializer.Deserialize<ConstellationsFile>(fs, opts);
            var list = doc?.Constellations ?? new List<ConstellationDto>();
            Debug.WriteLine($"[constellations.json] loaded {list.Count} constellations");
            return list;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[constellations.json] parse failed: {ex.GetType().Name}: {ex.Message}");
            return new List<ConstellationDto>();
        }
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _shader?.Dispose();
        _vao = _vbo = 0;
    }

    private sealed class ConstellationsFile
    {
        [JsonPropertyName("constellations")] public List<ConstellationDto>? Constellations { get; set; }
    }

    private sealed class ConstellationDto
    {
        public string? Name { get; set; }
        public double LabelRaHours { get; set; }
        public double LabelDecDeg { get; set; }
        /// <summary>Each line is two endpoints, each endpoint is [raHours, decDeg].</summary>
        public double[][][] Lines { get; set; } = Array.Empty<double[][]>();
    }

    // Skybox-style line shader. Translation has already been stripped from uView on the
    // CPU, and gl_Position.z = gl_Position.w forces the line to the far plane so the
    // constellations always render behind every other 3D body without z-fighting.
    private const string Vs = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uView; uniform mat4 uProj;
void main(){
    vec4 clip = uProj * uView * vec4(aPos, 1.0);
    clip.z = clip.w;
    gl_Position = clip;
}";

    private const string Fs = @"#version 330 core
out vec4 fragColor; uniform vec4 uColor;
void main(){ fragColor = uColor; }";
}
