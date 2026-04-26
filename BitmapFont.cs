using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace SolarSystem;

/// <summary>
/// Real TrueType font rasterized into an RGBA8 atlas via GDI+. Each glyph is rendered with
/// anti-aliased gridfit hinting; the alpha channel is the coverage mask. The shader samples
/// the alpha channel as the glyph mask and tints with a uniform color.
///
/// Each glyph stores tight pixel bounds inside the atlas, plus the offset from the pen
/// position and the advance, so text is properly proportional with correct kerning per char.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BitmapFont : IDisposable
{
    public readonly struct Glyph
    {
        public readonly Vector4 Uv;     // u0, v0, u1, v1 in atlas
        public readonly Vector2 Size;   // pixels in atlas (tight ink box)
        public readonly Vector2 Offset; // pixels from pen origin to top-left of ink box
        public readonly float Advance;  // pixels to advance pen for next glyph

        public Glyph(Vector4 uv, Vector2 size, Vector2 offset, float advance)
        {
            Uv = uv; Size = size; Offset = offset; Advance = advance;
        }
    }

    public int Texture { get; }
    public int AtlasW { get; }
    public int AtlasH { get; }

    /// <summary>Pixel size at which the font was rasterized. DrawText scales by pixelSize / FontPixelSize.</summary>
    public float FontPixelSize { get; }

    /// <summary>Logical line height in atlas pixels (used when DrawText encounters '\n').</summary>
    public float LineHeight { get; }

    private readonly Dictionary<char, Glyph> _glyphs;
    private readonly Glyph _fallback;

    public BitmapFont(string fontFamily = "Segoe UI", float fontPixelSize = 32f, int atlasW = 1024, int atlasH = 512)
    {
        FontPixelSize = fontPixelSize;
        AtlasW = atlasW;
        AtlasH = atlasH;
        _glyphs = new Dictionary<char, Glyph>(128);

        FontFamily family;
        try { family = new FontFamily(fontFamily); }
        catch { family = FontFamily.GenericSansSerif; }

        using (family)
        using (var font = new Font(family, fontPixelSize, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var atlas = new Bitmap(AtlasW, AtlasH, GdiPixelFormat.Format32bppArgb))
        using (var atlasG = Graphics.FromImage(atlas))
        {
            atlasG.Clear(Color.Transparent);
            LineHeight = font.GetHeight(atlasG);

            // Scratch bitmap for rendering one glyph at a time, then we scan the alpha
            // channel for the tight ink box and copy that sub-rect into the atlas.
            int scratchW = (int)Math.Ceiling(fontPixelSize * 2.5f) + 8;
            int scratchH = (int)Math.Ceiling(fontPixelSize * 2.0f) + 8;
            using var scratch = new Bitmap(scratchW, scratchH, GdiPixelFormat.Format32bppArgb);
            using var scratchG = Graphics.FromImage(scratch);
            scratchG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            scratchG.SmoothingMode = SmoothingMode.AntiAlias;

            // GenericTypographic gives much tighter / saner advance values than the default.
            var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            using var brush = new SolidBrush(Color.White);

            int penX = 4, penY = 4;

            // Simple shelf packer.
            int curX = 1, curY = 1, rowH = 0;

            for (int code = 32; code < 127; code++)
            {
                char ch = (char)code;
                string s = ch.ToString();

                // Advance from typographic measurement.
                SizeF measured = scratchG.MeasureString(s, font, PointF.Empty, sf);
                float advance = measured.Width;
                if (advance <= 0) advance = fontPixelSize * 0.5f; // sane fallback for whitespace

                // Render glyph.
                scratchG.CompositingMode = CompositingMode.SourceCopy;
                scratchG.Clear(Color.Transparent);
                scratchG.CompositingMode = CompositingMode.SourceOver;
                scratchG.DrawString(s, font, brush, penX, penY, sf);

                // Find tight ink box by scanning alpha.
                if (!FindAlphaBounds(scratch, out int minX, out int minY, out int maxX, out int maxY))
                {
                    // Empty glyph (space etc.).
                    _glyphs[ch] = new Glyph(Vector4.Zero, Vector2.Zero, Vector2.Zero, advance);
                    continue;
                }

                int gw = maxX - minX + 1;
                int gh = maxY - minY + 1;

                // Pack: new shelf if not enough horizontal space.
                if (curX + gw + 1 > AtlasW)
                {
                    curX = 1;
                    curY += rowH + 1;
                    rowH = 0;
                }
                if (curY + gh + 1 > AtlasH)
                    break; // Atlas full; stop adding glyphs.

                if (gh > rowH) rowH = gh;

                // Copy ink box from scratch into atlas.
                var srcRect = new Rectangle(minX, minY, gw, gh);
                var dstRect = new Rectangle(curX, curY, gw, gh);
                atlasG.CompositingMode = CompositingMode.SourceCopy;
                atlasG.DrawImage(scratch, dstRect, srcRect, GraphicsUnit.Pixel);

                _glyphs[ch] = new Glyph(
                    new Vector4(curX / (float)AtlasW, curY / (float)AtlasH,
                                (curX + gw) / (float)AtlasW, (curY + gh) / (float)AtlasH),
                    new Vector2(gw, gh),
                    new Vector2(minX - penX, minY - penY),
                    advance);

                curX += gw + 1;
            }

            sf.Dispose();

            // Upload to GL.
            var data = atlas.LockBits(new Rectangle(0, 0, AtlasW, AtlasH),
                ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
            try
            {
                Texture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, Texture);
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, data.Stride / 4);
                // GDI+ Format32bppArgb is BGRA in memory on little-endian.
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                    AtlasW, AtlasH, 0,
                    GlPixelFormat.Bgra,
                    PixelType.UnsignedByte, data.Scan0);
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            }
            finally
            {
                atlas.UnlockBits(data);
            }
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }

        // Pick a fallback for missing characters.
        _fallback = _glyphs.TryGetValue('?', out var q)
            ? q
            : _glyphs.TryGetValue(' ', out var sp)
                ? sp
                : new Glyph(Vector4.Zero, Vector2.Zero, Vector2.Zero, FontPixelSize * 0.5f);
    }

    public Glyph GetGlyph(char c) => _glyphs.TryGetValue(c, out var g) ? g : _fallback;

    private static unsafe bool FindAlphaBounds(Bitmap bmp, out int minX, out int minY, out int maxX, out int maxY)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
        try
        {
            int w = bmp.Width, h = bmp.Height, stride = data.Stride;
            byte* basep = (byte*)data.Scan0;
            minX = w; minY = h; maxX = -1; maxY = -1;
            for (int y = 0; y < h; y++)
            {
                byte* row = basep + y * stride;
                for (int x = 0; x < w; x++)
                {
                    byte a = row[x * 4 + 3]; // BGRA -> alpha at +3
                    if (a > 8)
                    {
                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX >= minX && maxY >= minY;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose() => GL.DeleteTexture(Texture);
}
