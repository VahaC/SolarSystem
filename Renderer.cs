using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace SolarSystem;

public sealed class Renderer : IDisposable
{
    // Meshes
    private int _sphereVao, _sphereVbo, _sphereEbo, _sphereIndexCount;
    private int _orbitVao, _orbitVbo, _orbitVertexCount;
    private int[] _orbitOffsets = Array.Empty<int>();
    private int[] _orbitCounts = Array.Empty<int>();
    private int _starVao, _starVbo, _starCount;
    private int _ringVao, _ringVbo, _ringEbo, _ringIndexCount;
    private int _quadVao, _quadVbo;
    private int _axisVao, _axisVbo;
    private int _textVao, _textVbo;
    private int _trailVao, _trailVbo;
    private const int TextMaxQuads = 1024;

    // Shaders
    private ShaderProgram _planetShader = null!;
    private ShaderProgram _sunShader = null!;
    private ShaderProgram _orbitShader = null!;
    private ShaderProgram _starShader = null!;
    private ShaderProgram _ringShader = null!;
    private ShaderProgram _glowShader = null!;
    private ShaderProgram _textShader = null!;
    private ShaderProgram _starsShader = null!;
    private ShaderProgram _trailShader = null!;
    private ShaderProgram _cloudShader = null!;
    private ShaderProgram _lensFlareShader = null!;

    // Sun / Sky
    private int _sunTexture;
    private int _ringTexture;
    private int _starsTexture;

    // HDR + Bloom post-processing.
    // The 3D scene is rendered to a half-float HDR target so bright pixels (the Sun,
    // flares) can exceed 1.0; a bright-pass + separable Gaussian blur + additive
    // composite then brings those highlights back down to the default framebuffer
    // as a glow halo.
    private int _hdrFbo, _hdrColor, _hdrDepth;
    private int _bloomFboA, _bloomFboB, _bloomTexA, _bloomTexB;
    private int _ppWidth, _ppHeight;
    private ShaderProgram _brightShader = null!;
    private ShaderProgram _blurShader = null!;
    private ShaderProgram _compositeShader = null!;
    private ShaderProgram _fxaaShader = null!;
    // V11: LDR target the composite renders into when FXAA is enabled. The FXAA
    // pass then samples this and writes to the default framebuffer.
    private int _ldrFbo, _ldrTex;
    public bool BloomEnabled { get; set; } = true;
    /// <summary>V10: linear exposure multiplier applied before ACES tone mapping.
    /// 1.0 keeps the original brightness; lower values darken, higher brighten.</summary>
    public float Exposure { get; set; } = 1.0f;
    /// <summary>V10: when true the composite pass samples the 1x1 mip of the HDR
    /// scene to estimate average luminance and corrects exposure toward middle grey.</summary>
    public bool AutoExposureEnabled { get; set; } = true;
    /// <summary>V11: enables a fullscreen FXAA pass after composite. Cheap (~1ms)
    /// and kills sub-pixel shimmer in real-scale mode.</summary>
    public bool FxaaEnabled { get; set; } = true;
    /// <summary>V9: master toggle for the per-body atmospheric rim glow. When off,
    /// every planet renders without Rayleigh/Mie scattering regardless of its
    /// per-body coefficients.</summary>
    public bool AtmosphereEnabled { get; set; } = true;
    /// <summary>V8: master toggle for body-vs-body eclipse shadows. When off the
    /// shadow caster list is ignored even if it was uploaded for the frame.</summary>
    public bool EclipsesEnabled { get; set; } = true;
    /// <summary>V12: animate the Sun disc with a 3D fbm granulation field. Off keeps
    /// the Sun a static texture.</summary>
    public bool CoronaEnabled { get; set; } = true;
    /// <summary>V14: enable Cook-Torrance GGX shading on planets (per-body
    /// roughness/metallic). Off falls back to the legacy Phong specular term.</summary>
    public bool PbrEnabled { get; set; } = true;
    /// <summary>V15: when off the planet shader ignores the ocean mask and lets the
    /// whole surface glint uniformly (legacy look).</summary>
    public bool OceanMaskEnabled { get; set; } = true;

    // V8: shadow casters (xyz=center, w=radius). Uploaded once per frame and
    // consumed by every subsequent DrawPlanet call until cleared / overwritten.
    private const int MaxShadowCasters = 16;
    private readonly float[] _shadowSphereData = new float[MaxShadowCasters * 4];
    private int _shadowCount;

    public Vector2i FramebufferSize { get; set; } = new(1280, 800);

    /// <summary>Minimum on-screen radius (in pixels) the Sun and planets are forced to.
    /// Keeps real-scale bodies visible at any distance. Set to 0 to disable.</summary>
    public float MinPixelRadius { get; set; } = 1.0f;

    /// <summary>R3: scales the Milky Way sky colour after sampling. Lowered when the
    /// camera is in deep space so distant stars don't drown out a tiny planet.</summary>
    public float StarsBrightness { get; set; } = 0.7f;
    /// <summary>R3: 1.0 keeps the original star colour, &lt;1 desaturates toward grey,
    /// &gt;1 punches up the chromatic content for a "near-planet" cinematic feel.</summary>
    public float StarsSaturation { get; set; } = 1.0f;

    // Logarithmic-depth coefficient: Fcoef = 2 / log2(far + 1). Computed per draw
    // from the active camera's far plane and uploaded as `uFcoef` to every 3D shader.
    private static float Fcoef(Camera cam) => 2.0f / MathF.Log2(cam.Far + 1.0f);

    public void Initialize()
    {
        BuildSphere(64, 64);
        BuildStars(2000);
        BuildRing(64);
        BuildQuad();
        BuildAxisLine();
        BuildTextBuffer();
        BuildTrailBuffer();
        CompileShaders();

        _sunTexture = TextureManager.TryLoadFile("8k_sun.jpg", out int sunTex)
            ? sunTex : TextureManager.CreateProcedural(255, 220, 110);
        _ringTexture = TextureManager.TryLoadFile("8k_saturn_ring_alpha.png", out int ringTex)
            ? ringTex : TextureManager.CreateRingTexture();
        // V7: Milky Way sky. Try the underscored filename first (current convention),
        // fall back to the no-underscore variant referenced in the roadmap. If neither
        // ships, we keep a tiny solid-colour procedural texture as a last resort.
        if (TextureManager.TryLoadFile("8k_stars_milky_way.jpg", out int skyTex)
            || TextureManager.TryLoadFile("8k_stars_milkyway.jpg", out skyTex))
            _starsTexture = skyTex;
        else
            _starsTexture = TextureManager.CreateProcedural(8, 8, 18);

        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        GL.Enable(EnableCap.ProgramPointSize);
        GL.ClearColor(0.01f, 0.01f, 0.025f, 1f);
    }

    public void BuildOrbits(Planet[] planets, int samples = 256)
    {
        var verts = new List<float>(planets.Length * samples * 3);
        _orbitOffsets = new int[planets.Length];
        _orbitCounts = new int[planets.Length];
        for (int i = 0; i < planets.Length; i++)
        {
            _orbitOffsets[i] = verts.Count / 3;
            float s = OrbitalMechanics.OrbitWorldScale(planets[i].SemiMajorAxisAU);
            var pts = OrbitalMechanics.SampleOrbit(planets[i], samples);
            foreach (var p in pts)
            {
                verts.Add((float)(p.X * s));
                verts.Add((float)(p.Y * s));
                verts.Add((float)(p.Z * s));
            }
            _orbitCounts[i] = samples;
        }
        _orbitVertexCount = verts.Count / 3;
        if (_orbitVao == 0)
        {
            _orbitVao = GL.GenVertexArray();
            _orbitVbo = GL.GenBuffer();
        }
        GL.BindVertexArray(_orbitVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _orbitVbo);
        var arr = verts.ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    private void BuildSphere(int stacks, int slices)
    {
        var verts = new List<float>();
        var idx = new List<uint>();
        for (int i = 0; i <= stacks; i++)
        {
            float v = (float)i / stacks;
            float phi = v * MathF.PI;
            for (int j = 0; j <= slices; j++)
            {
                float u = (float)j / slices;
                float theta = u * MathF.PI * 2f;
                float x = MathF.Sin(phi) * MathF.Cos(theta);
                float y = MathF.Cos(phi);
                float z = MathF.Sin(phi) * MathF.Sin(theta);
                verts.Add(x); verts.Add(y); verts.Add(z); // pos
                verts.Add(x); verts.Add(y); verts.Add(z); // normal
                // U is mirrored (1-u) because our θ parameterisation walks the
                // equator clockwise as seen from +Y (the planet's north pole),
                // while equirectangular Earth textures store longitude growing
                // eastward (= counter-clockwise from north). Without the flip
                // every planet shows up east-west mirrored — most obvious on
                // Earth (the West African bulge ends up on the wrong side).
                // V is flipped (1-v) for the usual top-left vs bottom-left
                // texture-origin reason. The flip also makes the surface rotate
                // the correct way (eastward), which is what the deliberate
                // -RotationAngleRad in DrawPlanet was always written assuming.
                verts.Add(1f - u); verts.Add(1f - v);     // uv
            }
        }
        for (int i = 0; i < stacks; i++)
        {
            for (int j = 0; j < slices; j++)
            {
                uint a = (uint)(i * (slices + 1) + j);
                uint b = (uint)((i + 1) * (slices + 1) + j);
                // CCW winding when viewed from outside the sphere, so the default
                // GL_BACK face culling discards the far (inside) hemisphere and we
                // render the near hemisphere. Swapping these orders is what causes
                // the "inverted lighting" symptom: with the wrong winding only the
                // far side is rasterized, so the lit/dark sides appear flipped.
                idx.Add(a); idx.Add(a + 1); idx.Add(b);
                idx.Add(a + 1); idx.Add(b + 1); idx.Add(b);
            }
        }
        _sphereIndexCount = idx.Count;
        _sphereVao = GL.GenVertexArray();
        _sphereVbo = GL.GenBuffer();
        _sphereEbo = GL.GenBuffer();
        GL.BindVertexArray(_sphereVao);
        var va = verts.ToArray();
        var ia = idx.ToArray();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _sphereVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _sphereEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * sizeof(uint), ia, BufferUsageHint.StaticDraw);
        int stride = 8 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.BindVertexArray(0);
    }

    private void BuildStars(int count)
    {
        _starCount = count;
        var rng = new Random(42);
        var data = new float[count * 4]; // x,y,z,brightness
        for (int i = 0; i < count; i++)
        {
            // Uniformly distributed on a large sphere shell
            double u = rng.NextDouble() * 2 - 1;
            double t = rng.NextDouble() * Math.PI * 2;
            double s = Math.Sqrt(1 - u * u);
            float r = 1500f;
            data[i * 4 + 0] = (float)(s * Math.Cos(t)) * r;
            data[i * 4 + 1] = (float)u * r;
            data[i * 4 + 2] = (float)(s * Math.Sin(t)) * r;
            data[i * 4 + 3] = 0.4f + (float)rng.NextDouble() * 0.6f;
        }
        _starVao = GL.GenVertexArray();
        _starVbo = GL.GenBuffer();
        GL.BindVertexArray(_starVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _starVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    private void BuildRing(int segments)
    {
        // Flat annulus in XZ plane. inner radius 1.4, outer radius 2.4 (relative to planet radius).
        const float inner = 1.35f, outer = 2.4f;
        var verts = new List<float>();
        var idx = new List<uint>();
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float a = t * MathF.PI * 2f;
            float c = MathF.Cos(a), s = MathF.Sin(a);
            verts.Add(c * inner); verts.Add(0); verts.Add(s * inner); verts.Add(0f); verts.Add(0.5f);
            verts.Add(c * outer); verts.Add(0); verts.Add(s * outer); verts.Add(1f); verts.Add(0.5f);
        }
        for (int i = 0; i < segments; i++)
        {
            uint a = (uint)(i * 2);
            idx.Add(a); idx.Add(a + 1); idx.Add(a + 2);
            idx.Add(a + 2); idx.Add(a + 1); idx.Add(a + 3);
        }
        _ringIndexCount = idx.Count;
        _ringVao = GL.GenVertexArray();
        _ringVbo = GL.GenBuffer();
        _ringEbo = GL.GenBuffer();
        GL.BindVertexArray(_ringVao);
        var va = verts.ToArray();
        var ia = idx.ToArray();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _ringVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, va.Length * sizeof(float), va, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ringEbo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, ia.Length * sizeof(uint), ia, BufferUsageHint.StaticDraw);
        int stride = 5 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    private void BuildQuad()
    {
        // Unit centered quad with uvs.
        float[] q =
        {
            -1,-1, 0,0,
             1,-1, 1,0,
             1, 1, 1,1,
            -1,-1, 0,0,
             1, 1, 1,1,
            -1, 1, 0,1,
        };
        _quadVao = GL.GenVertexArray();
        _quadVbo = GL.GenBuffer();
        GL.BindVertexArray(_quadVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, q.Length * sizeof(float), q, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.BindVertexArray(0);
    }

    private void BuildAxisLine()
    {
        // Two-vertex line segment along local Y from -1.5 to +1.5 (in planet-radius units).
        float[] line = { 0f, -1.5f, 0f, 0f, 1.5f, 0f };
        _axisVao = GL.GenVertexArray();
        _axisVbo = GL.GenBuffer();
        GL.BindVertexArray(_axisVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _axisVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, line.Length * sizeof(float), line, BufferUsageHint.StaticDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.BindVertexArray(0);
    }

    /// <summary>Draws each planet's rotation axis as a line, applying ONLY the axial tilt and translation
    /// (no spin, no view-dependent transform). Useful to visually verify the axis is fixed in world space.</summary>
    public void DrawPlanetAxes(Camera cam, Planet[] planets)
    {
        _orbitShader.Use();
        _orbitShader.SetMatrix4("uView", cam.ViewMatrix);
        _orbitShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _orbitShader.SetFloat("uFcoef", Fcoef(cam));
        _orbitShader.SetVector4("uColor", new Vector4(1f, 0.2f, 0.2f, 0.9f));
        GL.BindVertexArray(_axisVao);
        foreach (var p in planets)
        {
            // Same scale + tilt + translate as DrawPlanet, but no spin.
            // Make axis line a bit longer than the planet so it's clearly visible.
            var model = Matrix4.CreateScale(p.VisualRadius * 1.6f)
                        * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(p.AxisTiltDeg))
                        * Matrix4.CreateTranslation(p.Position);
            _orbitShader.SetMatrix4("uModel", model);
            GL.DrawArrays(PrimitiveType.Lines, 0, 2);
        }
        GL.BindVertexArray(0);
    }

    private void BuildTextBuffer()
    {
        _textVao = GL.GenVertexArray();
        _textVbo = GL.GenBuffer();
        GL.BindVertexArray(_textVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _textVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, TextMaxQuads * 6 * 4 * sizeof(float),
            IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.BindVertexArray(0);
    }

    // 4 floats per vertex: x, y, z, age (0 = oldest, 1 = newest). Sized for up to
    // 16 bodies ? Planet.TrailCapacity samples to keep the upload to a single buffer.
    private const int TrailMaxVertices = 16 * Planet.TrailCapacity;
    private void BuildTrailBuffer()
    {
        _trailVao = GL.GenVertexArray();
        _trailVbo = GL.GenBuffer();
        GL.BindVertexArray(_trailVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _trailVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, TrailMaxVertices * 4 * sizeof(float),
            IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), 3 * sizeof(float));
        GL.BindVertexArray(0);
    }

    /// <summary>Draws each planet's recorded trail as a fading line strip from the oldest
    /// sample (alpha 0) to the most recent (full alpha). Samples are uploaded fresh each
    /// frame from the planet's ring buffer.</summary>
    public void DrawTrails(Camera cam, Planet[] planets)
    {
        // Pack all line strips into a single VBO upload. Each strip is drawn separately
        // via glMultiDraw-style offsets to avoid stitching the last vertex of one trail
        // to the first vertex of the next.
        Span<int> offsets = stackalloc int[planets.Length];
        Span<int> counts = stackalloc int[planets.Length];

        int totalVerts = 0;
        for (int i = 0; i < planets.Length; i++) totalVerts += planets[i].TrailCount;
        if (totalVerts < 2) return;
        if (totalVerts > TrailMaxVertices) totalVerts = TrailMaxVertices;

        var data = new float[totalVerts * 4];
        int v = 0;
        for (int i = 0; i < planets.Length; i++)
        {
            var p = planets[i];
            offsets[i] = v;
            int count = p.TrailCount;
            if (count < 2) { counts[i] = 0; continue; }
            // Walk from oldest to newest. When the buffer is full, oldest sits at TrailHead.
            int start = (p.TrailHead - count + Planet.TrailCapacity) % Planet.TrailCapacity;
            for (int j = 0; j < count; j++)
            {
                if (v >= TrailMaxVertices) break;
                var pos = p.Trail[(start + j) % Planet.TrailCapacity];
                float age = count == 1 ? 1f : (float)j / (count - 1);
                data[v * 4 + 0] = pos.X;
                data[v * 4 + 1] = pos.Y;
                data[v * 4 + 2] = pos.Z;
                data[v * 4 + 3] = age;
                v++;
            }
            counts[i] = v - offsets[i];
        }
        if (v < 2) return;

        GL.BindVertexArray(_trailVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _trailVbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, v * 4 * sizeof(float), data);

        _trailShader.Use();
        _trailShader.SetMatrix4("uView", cam.ViewMatrix);
        _trailShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _trailShader.SetFloat("uFcoef", Fcoef(cam));
        _trailShader.SetVector4("uColor", new Vector4(0.85f, 0.9f, 1.0f, 0.9f));

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        for (int i = 0; i < planets.Length; i++)
        {
            if (counts[i] >= 2)
                GL.DrawArrays(PrimitiveType.LineStrip, offsets[i], counts[i]);
        }
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.BindVertexArray(0);
    }

    // ---------------- DRAW ----------------

    public void Clear()
    {
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>Begin rendering the 3D scene into the HDR offscreen framebuffer.
    /// Must be paired with <see cref="EndSceneAndApplyBloom"/> which composites the
    /// result (plus a bloom halo on bright pixels) onto the default framebuffer.</summary>
    public void BeginScene()
    {
        if (!BloomEnabled) { Clear(); return; }
        EnsurePostProcessTargets();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);
        GL.Viewport(0, 0, FramebufferSize.X, FramebufferSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
    }

    /// <summary>Bright-pass extract ? 6-pass separable Gaussian blur ? additive
    /// composite of HDR scene + blurred bloom into the default framebuffer.
    /// After this call the default framebuffer is bound and ready for 2D overlays.</summary>
    public void EndSceneAndApplyBloom()
    {
        if (!BloomEnabled) return;

        int bw = Math.Max(1, _ppWidth / 2);
        int bh = Math.Max(1, _ppHeight / 2);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(false);

        // 1. Bright-pass: extract luminance above threshold into _bloomTexA at half res.
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _bloomFboA);
        GL.Viewport(0, 0, bw, bh);
        _brightShader.Use();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _hdrColor);
        _brightShader.SetInt("uTex", 0);
        _brightShader.SetFloat("uThreshold", 0.9f);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // 2. Separable Gaussian blur: ping-pong horizontal/vertical N times.
        _blurShader.Use();
        _blurShader.SetInt("uTex", 0);
        const int blurPasses = 6;
        bool horizontal = true;
        int srcTex = _bloomTexA;
        for (int i = 0; i < blurPasses; i++)
        {
            int dstFbo = horizontal ? _bloomFboB : _bloomFboA;
            int dstTex = horizontal ? _bloomTexB : _bloomTexA;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, dstFbo);
            GL.Viewport(0, 0, bw, bh);
            _blurShader.SetVector2("uTexel",
                horizontal ? new Vector2(1f / bw, 0f) : new Vector2(0f, 1f / bh));
            GL.BindTexture(TextureTarget.Texture2D, srcTex);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            srcTex = dstTex;
            horizontal = !horizontal;
        }
        int finalBloom = srcTex;

        // V10: regenerate the HDR mipmap chain so the composite shader can sample
        // the 1x1 mip for auto-exposure. Cheap on a half-float framebuffer.
        if (AutoExposureEnabled)
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _hdrColor);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        // 3. Composite HDR scene + blurred bloom + ACES tone-map. When FXAA is on
        // we render to the LDR target; otherwise straight to the default framebuffer.
        bool fxaa = FxaaEnabled;
        if (fxaa)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _ldrFbo);
        }
        else
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }
        GL.Viewport(0, 0, _ppWidth, _ppHeight);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        _compositeShader.Use();
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _hdrColor);
        _compositeShader.SetInt("uScene", 0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, finalBloom);
        _compositeShader.SetInt("uBloom", 1);
        _compositeShader.SetFloat("uBloomStrength", 1.1f);
        _compositeShader.SetFloat("uExposure", Exposure);
        _compositeShader.SetInt("uAutoExposure", AutoExposureEnabled ? 1 : 0);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);

        // V11: FXAA from the LDR target into the default framebuffer.
        if (fxaa)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, _ppWidth, _ppHeight);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            _fxaaShader.Use();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _ldrTex);
            _fxaaShader.SetInt("uTex", 0);
            _fxaaShader.SetVector2("uTexel", new Vector2(1f / _ppWidth, 1f / _ppHeight));
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }
        GL.BindVertexArray(0);
        GL.ActiveTexture(TextureUnit.Texture0);

        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
    }

    /// <summary>V8: uploads a list of opaque body bounding spheres consumed by
    /// every subsequent <see cref="DrawPlanet"/> call's eclipse loop. Pass
    /// `count = 0` to disable shadows. Centers are world-space; radii match the
    /// body's <c>VisualRadius</c>.</summary>
    public void SetShadowCasters(ReadOnlySpan<Vector4> spheres)
    {
        int n = Math.Min(spheres.Length, MaxShadowCasters);
        for (int i = 0; i < n; i++)
        {
            _shadowSphereData[i * 4 + 0] = spheres[i].X;
            _shadowSphereData[i * 4 + 1] = spheres[i].Y;
            _shadowSphereData[i * 4 + 2] = spheres[i].Z;
            _shadowSphereData[i * 4 + 3] = spheres[i].W;
        }
        _shadowCount = n;
    }

    private void EnsurePostProcessTargets()
    {
        if (_hdrFbo != 0 && _ppWidth == FramebufferSize.X && _ppHeight == FramebufferSize.Y)
            return;

        _ppWidth = Math.Max(1, FramebufferSize.X);
        _ppHeight = Math.Max(1, FramebufferSize.Y);
        int bw = Math.Max(1, _ppWidth / 2);
        int bh = Math.Max(1, _ppHeight / 2);

        if (_hdrFbo == 0) _hdrFbo = GL.GenFramebuffer();
        if (_hdrColor == 0) _hdrColor = GL.GenTexture();
        if (_hdrDepth == 0) _hdrDepth = GL.GenRenderbuffer();

        GL.BindTexture(TextureTarget.Texture2D, _hdrColor);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
            _ppWidth, _ppHeight, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
        // V10: mipmap-complete so the composite shader can sample the 1x1 mip
        // for auto-exposure. Mips are regenerated each frame in EndSceneAndApplyBloom.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _hdrDepth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,
            RenderbufferStorage.DepthComponent24, _ppWidth, _ppHeight);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _hdrFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _hdrColor, 0);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _hdrDepth);

        if (_bloomFboA == 0) { _bloomFboA = GL.GenFramebuffer(); _bloomTexA = GL.GenTexture(); }
        if (_bloomFboB == 0) { _bloomFboB = GL.GenFramebuffer(); _bloomTexB = GL.GenTexture(); }

        InitBloomTarget(_bloomFboA, _bloomTexA, bw, bh);
        InitBloomTarget(_bloomFboB, _bloomTexB, bw, bh);

        // V11: LDR target for the composite output when FXAA is enabled.
        if (_ldrFbo == 0) { _ldrFbo = GL.GenFramebuffer(); _ldrTex = GL.GenTexture(); }
        GL.BindTexture(TextureTarget.Texture2D, _ldrTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
            _ppWidth, _ppHeight, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _ldrFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _ldrTex, 0);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void InitBloomTarget(int fbo, int tex, int w, int h)
    {
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
            w, h, 0, PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
    }

    public void DrawStars(Camera cam)
    {
        // Fullscreen-quad sky. Per-pixel we reconstruct the world-space view direction
        // from clip-space coordinates using the inverse of (view-without-translation * proj),
        // then sample an equirectangular texture. No sphere, no culling, no xyww trick.
        _starsShader.Use();
        var view = cam.ViewMatrix;
        view.M41 = 0; view.M42 = 0; view.M43 = 0; // strip translation
        var vp = view * cam.ProjectionMatrix;
        Matrix4.Invert(vp, out var invVp);
        _starsShader.SetMatrix4("uInvViewProj", invVp);
        _starsShader.SetFloat("uBrightness", StarsBrightness);
        _starsShader.SetFloat("uSaturation", StarsSaturation);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _starsTexture);
        _starsShader.SetInt("uTex", 0);
        GL.DepthMask(false);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
    }

    public void DrawOrbits(Camera cam, Planet[] planets)
    {
        _orbitShader.Use();
        _orbitShader.SetMatrix4("uView", cam.ViewMatrix);
        _orbitShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _orbitShader.SetMatrix4("uModel", Matrix4.Identity);
        _orbitShader.SetFloat("uFcoef", Fcoef(cam));
        _orbitShader.SetVector4("uColor", new Vector4(0.4f, 0.4f, 0.55f, 0.6f));
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.BindVertexArray(_orbitVao);
        for (int i = 0; i < planets.Length; i++)
            GL.DrawArrays(PrimitiveType.LineLoop, _orbitOffsets[i], _orbitCounts[i]);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
    }

    public void DrawSun(Camera cam, Vector3 sunPos, float radius)
    {
        // Solid bright sphere
        _sunShader.Use();
        _sunShader.SetMatrix4("uView", cam.ViewMatrix);
        _sunShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        var model = Matrix4.CreateScale(radius) * Matrix4.CreateTranslation(sunPos);
        _sunShader.SetMatrix4("uModel", model);
        _sunShader.SetFloat("uFcoef", Fcoef(cam));
        _sunShader.SetVector3("uPlanetCenter", sunPos);
        _sunShader.SetFloat("uPlanetRadius", radius);
        _sunShader.SetVector2("uViewportSize", new Vector2(FramebufferSize.X, FramebufferSize.Y));
        _sunShader.SetFloat("uMinPixelRadius", MinPixelRadius);
        _sunShader.SetVector3("uColor", new Vector3(1.0f, 0.85f, 0.45f));
        // V12: animate granulation. uTime in seconds, uCorona toggles the noise field.
        _sunShader.SetFloat("uTime", (float)GLFW.GetTime());
        _sunShader.SetInt("uCorona", CoronaEnabled ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _sunTexture);
        _sunShader.SetInt("uTex", 0);
        GL.BindVertexArray(_sphereVao);
        GL.DrawElements(PrimitiveType.Triangles, _sphereIndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);

        // Halo billboard, additive blending
        _glowShader.Use();
        _glowShader.SetMatrix4("uView", cam.ViewMatrix);
        _glowShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _glowShader.SetFloat("uFcoef", Fcoef(cam));
        _glowShader.SetVector3("uCenter", sunPos);
        _glowShader.SetFloat("uSize", radius * 4.5f);
        _glowShader.SetVector3("uColor", new Vector3(1.0f, 0.7f, 0.3f));
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        GL.DepthMask(false);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    public void DrawPlanet(Camera cam, Planet p, Vector3 sunPos)
    {
        _planetShader.Use();
        _planetShader.SetMatrix4("uView", cam.ViewMatrix);
        _planetShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _planetShader.SetFloat("uFcoef", Fcoef(cam));
        _planetShader.SetFloat("uPlanetRadius", p.VisualRadius);
        _planetShader.SetVector2("uViewportSize", new Vector2(FramebufferSize.X, FramebufferSize.Y));
        _planetShader.SetFloat("uMinPixelRadius", MinPixelRadius);
        // Order (row-vector convention): scale -> spin around Y -> axial tilt around Z -> translate.
        // Positive RotationPeriodHours (Earth/Mars/Jupiter/...) yields the correct
        // prograde spin (CCW viewed from the planet's north pole); negative periods
        // (Venus, Uranus) automatically become retrograde. Earlier this term was
        // negated to compensate for an E-W mirrored sphere texture; Phase 7a fixed
        // that at the mesh level (1-u in BuildSphere), so the negation is gone now.
        var model = Matrix4.CreateScale(p.VisualRadius)
                    * Matrix4.CreateRotationY(p.RotationAngleRad)
                    * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(p.AxisTiltDeg))
                    * Matrix4.CreateTranslation(p.Position);
        _planetShader.SetMatrix4("uModel", model);
        _planetShader.SetVector3("uPlanetCenter", p.Position);
        _planetShader.SetVector3("uLightPos", sunPos);
        _planetShader.SetVector3("uViewPos", cam.Eye);
        _planetShader.SetVector3("uLightColor", new Vector3(1.0f, 0.96f, 0.88f));
        // Tint is only used to colorize procedural fallback textures.
        // For file-loaded textures use white so colors are reproduced faithfully.
        _planetShader.SetVector3("uTint", p.TextureFromFile ? Vector3.One : p.ProceduralColor);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, p.TextureId);
        _planetShader.SetInt("uTex", 0);
        // V4: optional night-side city-lights map on texture unit 1. Always bind
        // something (0 = the default texture) so undefined behaviour can't leak in.
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, p.NightTextureId);
        _planetShader.SetInt("uNightTex", 1);
        _planetShader.SetInt("uHasNight", p.NightTextureId != 0 ? 1 : 0);

        // V5: Saturn ring shadow on the planet. For non-Saturn bodies we leave
        // uHasRing = 0 so the shader skips the ring-shadow branch entirely. For
        // Saturn we feed the same inner/outer/normal that DrawSaturnRing uses, plus
        // bind the ring texture (TU2) so the per-fragment ray-vs-disk test can
        // attenuate direct sunlight by the ring's per-radius opacity.
        bool hasRing = p.Name == "Saturn";
        GL.ActiveTexture(TextureUnit.Texture2);
        GL.BindTexture(TextureTarget.Texture2D, hasRing ? _ringTexture : 0);
        _planetShader.SetInt("uRingTex", 2);
        _planetShader.SetInt("uHasRing", hasRing ? 1 : 0);
        if (hasRing)
        {
            float tilt = MathHelper.DegreesToRadians(p.AxisTiltDeg);
            // Ring is built in the local XZ plane, so its world normal is RotZ(tilt)*(0,1,0)
            // = (-sin tilt, cos tilt, 0) — same transform DrawSaturnRing applies to the disk.
            var ringNormal = new Vector3(-MathF.Sin(tilt), MathF.Cos(tilt), 0f);
            _planetShader.SetVector3("uRingNormal", ringNormal);
            _planetShader.SetFloat("uRingInner", p.VisualRadius * 1.35f);
            _planetShader.SetFloat("uRingOuter", p.VisualRadius * 2.4f);
        }

        // V8: eclipse / body-shadow casters. The list was uploaded once per frame
        // via SetShadowCasters; the shader skips the body matching uPlanetCenter.
        int shadowCount = EclipsesEnabled ? _shadowCount : 0;
        _planetShader.SetInt("uShadowCount", shadowCount);
        if (shadowCount > 0)
            _planetShader.SetVector4Array("uShadowSpheres", _shadowSphereData, shadowCount);

        // V15: ocean / specular mask on TU3. uHasOceanMask = 0 → uniform glint.
        GL.ActiveTexture(TextureUnit.Texture3);
        GL.BindTexture(TextureTarget.Texture2D, p.OceanMaskTextureId);
        _planetShader.SetInt("uOceanMask", 3);
        _planetShader.SetInt("uHasOceanMask",
            (OceanMaskEnabled && p.OceanMaskTextureId != 0) ? 1 : 0);

        // V14: per-body PBR coefficients. PbrEnabled = 0 falls back to Phong in-shader.
        GetPbr(p.Name, out float roughness, out float metallic);
        _planetShader.SetInt("uPbrEnabled", PbrEnabled ? 1 : 0);
        _planetShader.SetFloat("uRoughness", roughness);
        _planetShader.SetFloat("uMetallic", metallic);

        // V9: per-body atmosphere coefficients. Earth, Mars, Venus, Titan and
        // Neptune get a Rayleigh+Mie rim glow; everything else stays opaque.
        GetAtmosphere(p.Name, out bool hasAtm, out Vector3 atmColor, out float atmStrength);
        if (!AtmosphereEnabled) hasAtm = false;
        _planetShader.SetInt("uHasAtmosphere", hasAtm ? 1 : 0);
        if (hasAtm)
        {
            _planetShader.SetVector3("uAtmosphereColor", atmColor);
            _planetShader.SetFloat("uAtmosphereStrength", atmStrength);
        }

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindVertexArray(_sphereVao);
        GL.DrawElements(PrimitiveType.Triangles, _sphereIndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    /// <summary>V14: per-body GGX roughness / metallic. Tuned by feel:
    /// gas giants are smoother (cleaner specular lobes), rocky bodies rougher,
    /// the Moon nearly Lambertian. Metallic stays near 0 except for tiny hints
    /// on Mercury/Moon to shift their grey toward a cooler reflectance.</summary>
    private static void GetPbr(string name, out float roughness, out float metallic)
    {
        switch (name)
        {
            case "Mercury": roughness = 0.85f; metallic = 0.10f; return;
            case "Venus":   roughness = 0.55f; metallic = 0.00f; return;
            case "Earth":   roughness = 0.45f; metallic = 0.00f; return;
            case "Mars":    roughness = 0.80f; metallic = 0.05f; return;
            case "Jupiter": roughness = 0.65f; metallic = 0.00f; return;
            case "Saturn":  roughness = 0.60f; metallic = 0.00f; return;
            case "Uranus":  roughness = 0.40f; metallic = 0.00f; return;
            case "Neptune": roughness = 0.40f; metallic = 0.00f; return;
            case "Moon":    roughness = 0.95f; metallic = 0.05f; return;
            default:        roughness = 0.70f; metallic = 0.00f; return;
        }
    }

    private static void GetAtmosphere(string name, out bool has, out Vector3 color, out float strength)
    {
        switch (name)
        {
            case "Earth":   has = true; color = new Vector3(0.30f, 0.55f, 1.00f); strength = 1.00f; return;
            case "Mars":    has = true; color = new Vector3(0.95f, 0.55f, 0.35f); strength = 0.40f; return;
            case "Venus":   has = true; color = new Vector3(0.95f, 0.85f, 0.55f); strength = 0.70f; return;
            case "Titan":   has = true; color = new Vector3(0.95f, 0.65f, 0.30f); strength = 0.65f; return;
            case "Neptune": has = true; color = new Vector3(0.30f, 0.45f, 0.90f); strength = 0.55f; return;
            default:        has = false; color = Vector3.Zero; strength = 0f; return;
        }
    }

    /// <summary>V3: draws a slightly-larger alpha-blended cloud sphere around
    /// <paramref name="p"/> using <see cref="Planet.CloudTextureId"/>. No-op if the
    /// planet has no cloud texture. Uses the planet shader's vertex stage so the
    /// log-depth + min-pixel-size pipeline applies, plus a dedicated FS that
    /// derives alpha from cloud luminance.</summary>
    public void DrawClouds(Camera cam, Planet p, Vector3 sunPos)
    {
        if (p.CloudTextureId == 0) return;
        _cloudShader.Use();
        _cloudShader.SetMatrix4("uView", cam.ViewMatrix);
        _cloudShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _cloudShader.SetFloat("uFcoef", Fcoef(cam));
        float cloudRadius = p.VisualRadius * 1.012f;
        _cloudShader.SetFloat("uPlanetRadius", cloudRadius);
        _cloudShader.SetVector2("uViewportSize", new Vector2(FramebufferSize.X, FramebufferSize.Y));
        // Skip the screen-space minimum-size expansion: the cloud sphere should never
        // pop out beyond the planet silhouette when the body shrinks to a few pixels.
        _cloudShader.SetFloat("uMinPixelRadius", 0f);
        var model = Matrix4.CreateScale(cloudRadius)
                    * Matrix4.CreateRotationY(p.CloudRotationAngleRad)
                    * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(p.AxisTiltDeg))
                    * Matrix4.CreateTranslation(p.Position);
        _cloudShader.SetMatrix4("uModel", model);
        _cloudShader.SetVector3("uPlanetCenter", p.Position);
        _cloudShader.SetVector3("uLightPos", sunPos);
        _cloudShader.SetVector3("uLightColor", new Vector3(1.0f, 0.96f, 0.88f));
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, p.CloudTextureId);
        _cloudShader.SetInt("uTex", 0);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.BindVertexArray(_sphereVao);
        GL.DrawElements(PrimitiveType.Triangles, _sphereIndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
    }

    /// <summary>Draws the comet's pre-built orbit polyline using the shared orbit
    /// shader. Kept as a forwarding helper so the comet doesn't need to own its own
    /// shader program just to render a single line loop.</summary>
    public void DrawCometOrbit(Camera cam, Comet comet) => comet.DrawOrbit(cam, _orbitShader);

    public void DrawSaturnRing(Camera cam, Planet saturn, Vector3 sunPos)
    {
        _ringShader.Use();
        _ringShader.SetMatrix4("uView", cam.ViewMatrix);
        _ringShader.SetMatrix4("uProj", cam.ProjectionMatrix);
        _ringShader.SetFloat("uFcoef", Fcoef(cam));
        var model = Matrix4.CreateScale(saturn.VisualRadius)
                    * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(saturn.AxisTiltDeg))
                    * Matrix4.CreateTranslation(saturn.Position);
        _ringShader.SetMatrix4("uModel", model);
        // V5: Saturn shadow on the ring. Per-fragment ray-vs-sphere from the ring
        // surface toward the Sun; if the ray hits Saturn before reaching the Sun
        // (t in (0,1)) we darken the ring fragment.
        _ringShader.SetVector3("uPlanetCenter", saturn.Position);
        _ringShader.SetFloat("uPlanetRadius", saturn.VisualRadius);
        _ringShader.SetVector3("uLightPos", sunPos);
        _ringShader.SetInt("uHasShadow", 1);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _ringTexture);
        _ringShader.SetInt("uTex", 0);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.BindVertexArray(_ringVao);
        GL.DrawElements(PrimitiveType.Triangles, _ringIndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
    }

    /// <summary>V6: screen-space lens flare. Projects <paramref name="sunPos"/> to NDC,
    /// fades by alignment with the camera's view forward (so the ghosts only ignite
    /// when the Sun is roughly looked-at) and draws an additive fullscreen quad of
    /// coloured ghosts along the Sun-through-centre axis. No-op when the Sun is
    /// behind the camera or off-screen by a large margin.</summary>
    public void DrawLensFlare(Camera cam, Vector3 sunPos)
    {
        var fwd = (cam.Target - cam.Eye);
        var toSun = sunPos - cam.Eye;
        if (fwd.LengthSquared < 1e-8f || toSun.LengthSquared < 1e-8f) return;
        fwd.Normalize();
        toSun.Normalize();
        float align = Vector3.Dot(fwd, toSun);
        if (align <= 0.05f) return; // Sun behind / nearly behind the camera

        // Project Sun world position to NDC. OpenTK uses row-vector convention,
        // mirroring DrawLabel.
        var clip = new Vector4(sunPos, 1f) * cam.ViewMatrix * cam.ProjectionMatrix;
        if (clip.W <= 0f) return;
        var ndc = clip.Xyz / clip.W;
        // Soft cutoff outside the screen so flares don't pop in/out abruptly.
        float ndcLen = MathF.Sqrt(ndc.X * ndc.X + ndc.Y * ndc.Y);
        float screenFactor = 1f - MathHelper.Clamp((ndcLen - 0.6f) / 1.4f, 0f, 1f);
        float alignFactor = MathHelper.Clamp((align - 0.4f) / 0.6f, 0f, 1f);
        float intensity = screenFactor * alignFactor * 0.6f;
        if (intensity <= 0.001f) return;

        _lensFlareShader.Use();
        _lensFlareShader.SetVector2("uSunNdc", new Vector2(ndc.X, ndc.Y));
        _lensFlareShader.SetFloat("uIntensity", intensity);
        _lensFlareShader.SetFloat("uAspect",
            FramebufferSize.Y > 0 ? (float)FramebufferSize.X / FramebufferSize.Y : 1f);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        GL.BindVertexArray(_quadVao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);
        GL.Disable(EnableCap.Blend);
        GL.DepthMask(true);
        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
    }

    /// <summary>Draws a string in screen space. Origin = top-left of screen, x grows right, y grows down.
    /// `pixelSize` is the desired line height (em-height) in screen pixels; glyph metrics from
    /// the rasterized font are scaled by pixelSize / FontPixelSize so text remains crisp.</summary>
    public void DrawText(BitmapFont font, string text, float x, float y, float pixelSize, Vector4 color)
    {
        if (string.IsNullOrEmpty(text)) return;

        float scale = pixelSize / font.FontPixelSize;
        float lineStep = font.LineHeight * scale;

        var verts = new List<float>(text.Length * 24);
        float cx = x;
        float cy = y;
        foreach (char ch in text)
        {
            if (ch == '\n') { cx = x; cy += lineStep; continue; }
            var g = font.GetGlyph(ch);
            if (g.Size.X > 0 && g.Size.Y > 0)
            {
                float x0 = cx + g.Offset.X * scale;
                float y0 = cy + g.Offset.Y * scale;
                float x1 = x0 + g.Size.X * scale;
                float y1 = y0 + g.Size.Y * scale;
                float u0 = g.Uv.X, v0 = g.Uv.Y, u1 = g.Uv.Z, v1 = g.Uv.W;
                verts.AddRange(new[] {
                    x0,y0, u0,v0,
                    x1,y0, u1,v0,
                    x1,y1, u1,v1,
                    x0,y0, u0,v0,
                    x1,y1, u1,v1,
                    x0,y1, u0,v1,
                });
            }
            cx += g.Advance * scale;
        }
        if (verts.Count == 0) return;

        _textShader.Use();
        var ortho = Matrix4.CreateOrthographicOffCenter(0, FramebufferSize.X, FramebufferSize.Y, 0, -1, 1);
        _textShader.SetMatrix4("uProj", ortho);
        _textShader.SetVector4("uColor", color);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, font.Texture);
        _textShader.SetInt("uTex", 0);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        // The orthographic matrix flips Y (screen y=0 -> NDC y=+1), which makes our
        // text quads clockwise in NDC and therefore back-facing under the global
        // CullFaceMode.Back state. Disable culling for the text pass.
        GL.Disable(EnableCap.CullFace);

        GL.BindVertexArray(_textVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _textVbo);
        var arr = verts.ToArray();
        int byteLen = Math.Min(arr.Length, TextMaxQuads * 6 * 4) * sizeof(float);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, byteLen, arr);
        GL.DrawArrays(PrimitiveType.Triangles, 0, Math.Min(arr.Length / 4, TextMaxQuads * 6));
        GL.BindVertexArray(0);

        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
    }

    /// <summary>Draws a label at a world position by projecting to screen.</summary>
    public void DrawLabel(BitmapFont font, Camera cam, Vector3 worldPos, string text, float pixelSize, Vector4 color)
    {
        var clip = new Vector4(worldPos, 1f) * cam.ViewMatrix * cam.ProjectionMatrix;
        if (clip.W <= 0) return;
        var ndc = clip.Xyz / clip.W;
        if (ndc.Z < -1 || ndc.Z > 1) return;
        float sx = (ndc.X * 0.5f + 0.5f) * FramebufferSize.X;
        float sy = (1f - (ndc.Y * 0.5f + 0.5f)) * FramebufferSize.Y;
        DrawText(font, text, sx + 8, sy - pixelSize * 0.5f, pixelSize, color);
    }

    // ---------------- Shaders ----------------
    private void CompileShaders()
    {
        // A2: every shader source ships as Resources/Shaders/*.glsl. The planet VS
        // is reused by the Sun and cloud passes (only the FS differs).
        _planetShader    = ShaderSources.CreateProgram("planet.vert",  "planet.frag");
        _fxaaShader      = ShaderSources.CreateProgram("post.vert",    "fxaa.frag");
        _sunShader       = ShaderSources.CreateProgram("planet.vert",  "sun.frag");
        _orbitShader     = ShaderSources.CreateProgram("orbit.vert",   "orbit.frag");
        _starShader      = ShaderSources.CreateProgram("star.vert",    "star.frag");
        _ringShader      = ShaderSources.CreateProgram("ring.vert",    "ring.frag");
        _glowShader      = ShaderSources.CreateProgram("glow.vert",    "glow.frag");
        _textShader      = ShaderSources.CreateProgram("text.vert",    "text.frag");
        _starsShader     = ShaderSources.CreateProgram("sky.vert",     "sky.frag");
        _trailShader     = ShaderSources.CreateProgram("trail.vert",   "trail.frag");
        _cloudShader     = ShaderSources.CreateProgram("planet.vert",  "cloud.frag");
        _lensFlareShader = ShaderSources.CreateProgram("post.vert",    "lensflare.frag");
        _brightShader    = ShaderSources.CreateProgram("post.vert",    "bright.frag");
        _blurShader      = ShaderSources.CreateProgram("post.vert",    "blur.frag");
        _compositeShader = ShaderSources.CreateProgram("post.vert",    "composite.frag");
    }

    public void Dispose()
    {
        _planetShader?.Dispose();
        _sunShader?.Dispose();
        _orbitShader?.Dispose();
        _starShader?.Dispose();
        _ringShader?.Dispose();
        _glowShader?.Dispose();
        _textShader?.Dispose();
        _starsShader?.Dispose();
        _trailShader?.Dispose();
        _cloudShader?.Dispose();
        _lensFlareShader?.Dispose();
        _brightShader?.Dispose();
        _blurShader?.Dispose();
        _compositeShader?.Dispose();
        _fxaaShader?.Dispose();
        GL.DeleteVertexArray(_sphereVao); GL.DeleteBuffer(_sphereVbo); GL.DeleteBuffer(_sphereEbo);
        GL.DeleteVertexArray(_orbitVao); GL.DeleteBuffer(_orbitVbo);
        GL.DeleteVertexArray(_starVao); GL.DeleteBuffer(_starVbo);
        GL.DeleteVertexArray(_ringVao); GL.DeleteBuffer(_ringVbo); GL.DeleteBuffer(_ringEbo);
        GL.DeleteVertexArray(_quadVao); GL.DeleteBuffer(_quadVbo);
        GL.DeleteVertexArray(_axisVao); GL.DeleteBuffer(_axisVbo);
        GL.DeleteVertexArray(_textVao); GL.DeleteBuffer(_textVbo);
        GL.DeleteVertexArray(_trailVao); GL.DeleteBuffer(_trailVbo);
        GL.DeleteTexture(_sunTexture);
        GL.DeleteTexture(_ringTexture);
        GL.DeleteTexture(_starsTexture);
        if (_hdrFbo != 0) GL.DeleteFramebuffer(_hdrFbo);
        if (_hdrColor != 0) GL.DeleteTexture(_hdrColor);
        if (_hdrDepth != 0) GL.DeleteRenderbuffer(_hdrDepth);
        if (_bloomFboA != 0) GL.DeleteFramebuffer(_bloomFboA);
        if (_bloomFboB != 0) GL.DeleteFramebuffer(_bloomFboB);
        if (_bloomTexA != 0) GL.DeleteTexture(_bloomTexA);
        if (_bloomTexB != 0) GL.DeleteTexture(_bloomTexB);
        if (_ldrFbo != 0) GL.DeleteFramebuffer(_ldrFbo);
        if (_ldrTex != 0) GL.DeleteTexture(_ldrTex);
    }
}
