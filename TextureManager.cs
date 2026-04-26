using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using StbImageSharp;

namespace SolarSystem;

public static class TextureManager
{
    public static int LoadOrProcedural(string filename, byte r, byte g, byte b, out bool fromFile)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "textures", filename);
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                StbImage.stbi_set_flip_vertically_on_load(1);
                var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                int tex = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, tex);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                    img.Width, img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, img.Data);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                SetParams(true);
                fromFile = true;
                Debug.WriteLine($"[Texture] Loaded {filename} ({img.Width}x{img.Height})");
                return tex;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Texture] Failed to load {filename}: {ex.Message}");
            }
        }
        fromFile = false;
        Debug.WriteLine($"[Texture] Procedural fallback for {filename}");
        return CreateProcedural(r, g, b);
    }

    /// <summary>Try to load a texture file from textures/. Returns false if missing/invalid.</summary>
    public static bool TryLoadFile(string filename, out int tex, bool generateMipmap = true)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "textures", filename);
        tex = 0;
        if (!File.Exists(path))
        {
            Debug.WriteLine($"[Texture] Not found: {filename}");
            return false;
        }
        try
        {
            using var stream = File.OpenRead(path);
            StbImage.stbi_set_flip_vertically_on_load(1);
            var img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                img.Width, img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, img.Data);
            if (generateMipmap) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            SetParams(generateMipmap);
            Debug.WriteLine($"[Texture] Loaded {filename} ({img.Width}x{img.Height})");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Texture] Failed to load {filename}: {ex.Message}");
            tex = 0;
            return false;
        }
    }

    public static int CreateProcedural(byte r, byte g, byte b)
    {
        const int W = 64, H = 64;
        byte[] data = new byte[W * H * 4];
        var rng = new Random(r * 65536 + g * 256 + b);
        for (int y = 0; y < H; y++)
        {
            float vy = (float)y / (H - 1);
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                float n = (float)(rng.NextDouble() * 0.15 - 0.075);
                float shade = 0.85f + 0.15f * MathF.Sin(vy * MathF.PI) + n;
                data[i + 0] = (byte)Math.Clamp(r * shade, 0, 255);
                data[i + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                data[i + 2] = (byte)Math.Clamp(b * shade, 0, 255);
                data[i + 3] = 255;
            }
        }
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, W, H, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, data);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        SetParams(true);
        return tex;
    }

    public static int CreateRingTexture()
    {
        const int W = 256;
        byte[] data = new byte[W * 4];
        for (int x = 0; x < W; x++)
        {
            float t = (float)x / (W - 1);
            float band = 0.5f + 0.5f * MathF.Sin(t * 40f) * MathF.Cos(t * 13f);
            byte v = (byte)(180 + 60 * band);
            byte a = (t < 0.05f || t > 0.98f) ? (byte)0 : (byte)(220 * MathF.Min(1f, MathF.Sin(t * MathF.PI) * 1.4f));
            data[x * 4 + 0] = (byte)(v);
            data[x * 4 + 1] = (byte)(v * 0.92f);
            data[x * 4 + 2] = (byte)(v * 0.78f);
            data[x * 4 + 3] = a;
        }
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, W, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, data);
        SetParams(false);
        return tex;
    }

    private static void SetParams(bool mipmap)
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)(mipmap ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
    }
}
