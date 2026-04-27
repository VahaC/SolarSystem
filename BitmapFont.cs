using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SkiaSharp;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace SolarSystem;

/// <summary>
/// A1: cross-platform TrueType font rasterized into an RGBA8 atlas via SkiaSharp.
/// Replaces the previous Windows-only GDI+ implementation; SkiaSharp ships native
/// renderers for Windows / Linux (NoDependencies) / macOS so the same code path
/// works on every supported runtime.
///
/// Each glyph is rendered with anti-aliased subpixel hinting; the alpha channel of
/// the atlas is the coverage mask. The shader samples that channel and tints with a
/// uniform colour. Per-glyph metrics (UV box, ink-box pixel size, pen offset and
/// horizontal advance) are stored so DrawText produces properly proportional text
/// with kerning correct enough for HUD use.
/// </summary>
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

    public BitmapFont(string fontFamily = "Segoe UI", float fontPixelSize = 32f, int atlasW = 1024, int atlasH = 1024)
    {
        FontPixelSize = fontPixelSize;
        AtlasW = atlasW;
        AtlasH = atlasH;
        _glyphs = new Dictionary<char, Glyph>(128);

        // Resolve the typeface. We try the requested family first; if the platform
        // doesn't have it (common on Linux when asking for "Segoe UI"), fall through
        // to a couple of well-known cross-platform sans-serif families and finally
        // SkiaSharp's default. Skia returns a non-null typeface even for unknown
        // names — it picks the platform default — so this chain is mostly cosmetic.
        SKTypeface? typeface =
            SKTypeface.FromFamilyName(fontFamily) ??
            SKTypeface.FromFamilyName("Segoe UI") ??
            SKTypeface.FromFamilyName("DejaVu Sans") ??
            SKTypeface.FromFamilyName("Arial") ??
            SKTypeface.Default;

        // Cache fallback typefaces (one per codepoint via SKFontManager.MatchCharacter)
        // so that scripts not present in the primary typeface (e.g. Cyrillic when the
        // resolved family is Latin-only) still render with real glyphs instead of '?'.
        var fontManager = SKFontManager.Default;
        var fallbackFonts = new Dictionary<string, SKFont>(StringComparer.Ordinal);
        var fallbackTypefaces = new List<SKTypeface>();

        using (typeface)
        using (var font = new SKFont(typeface, fontPixelSize) { Edging = SKFontEdging.SubpixelAntialias, Hinting = SKFontHinting.Normal })
        using (var paint = new SKPaint(font) { IsAntialias = true, Color = SKColors.White, TextSize = fontPixelSize })
        using (var atlas = new SKBitmap(new SKImageInfo(AtlasW, AtlasH, SKColorType.Rgba8888, SKAlphaType.Premul)))
        using (var atlasCanvas = new SKCanvas(atlas))
        {
            atlasCanvas.Clear(SKColors.Transparent);

            var metrics = font.Metrics;
            // SKFont metrics use signed values (descent positive, ascent negative).
            LineHeight = metrics.Descent - metrics.Ascent + metrics.Leading;
            if (LineHeight <= 0f) LineHeight = fontPixelSize * 1.25f;

            // Scratch bitmap for rendering one glyph at a time, then we scan its
            // alpha channel for the tight ink box and copy that sub-rect into the atlas.
            int scratchW = (int)Math.Ceiling(fontPixelSize * 2.5f) + 8;
            int scratchH = (int)Math.Ceiling(fontPixelSize * 2.5f) + 8;
            using var scratch = new SKBitmap(new SKImageInfo(scratchW, scratchH, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var scratchCanvas = new SKCanvas(scratch);

            int penX = 4;
            int penY = (int)Math.Ceiling(-metrics.Ascent) + 4;

            // Simple shelf packer.
            int curX = 1, curY = 1, rowH = 0;

            // Bake printable ASCII (32..126), Latin-1 supplement (160..255), the
            // full Cyrillic block (U+0400..U+04FF), General Punctuation
            // (U+2000..U+206F — em/en dash, ellipsis, smart quotes), Arrows
            // (U+2190..U+21FF), Box Drawing (U+2500..U+257F — ─ used in the
            // bookmarks "now" separator), Block Elements (U+2580..U+259F — █ ░),
            // Geometric Shapes (U+25A0..U+25FF — ▶ ◀) and Miscellaneous Symbols
            // (U+2600..U+26FF — ☄ ☀ etc.) so localized HUD strings render real
            // glyphs instead of falling back to '?'.
            var codepoints = new List<int>(1024);
            for (int c = 32; c < 127; c++) codepoints.Add(c);
            for (int c = 160; c < 256; c++) codepoints.Add(c);
            for (int c = 0x0400; c <= 0x04FF; c++) codepoints.Add(c);
            for (int c = 0x2000; c <= 0x206F; c++) codepoints.Add(c);
            for (int c = 0x2190; c <= 0x21FF; c++) codepoints.Add(c);
            for (int c = 0x2500; c <= 0x257F; c++) codepoints.Add(c);
            for (int c = 0x2580; c <= 0x259F; c++) codepoints.Add(c);
            for (int c = 0x25A0; c <= 0x25FF; c++) codepoints.Add(c);
            for (int c = 0x2600; c <= 0x26FF; c++) codepoints.Add(c);

            foreach (int code in codepoints)
            {
                if (code > char.MaxValue) continue;
                char ch = (char)code;
                string s = ch.ToString();

                // If the primary typeface has no glyph for this codepoint, ask
                // SKFontManager for a system fallback that does (e.g. Segoe UI
                // Symbol / Noto / DejaVu). Skipped glyphs would otherwise render
                // as '?' even if a localized translation supplies the codepoint.
                SKPaint drawPaint = paint;
                SKFont? fallbackFont = null;
                if (typeface.GetGlyph(code) == 0)
                {
                    var fbTypeface = fontManager.MatchCharacter(code);
                    if (fbTypeface != null)
                    {
                        string fbKey = fbTypeface.FamilyName ?? "?";
                        if (!fallbackFonts.TryGetValue(fbKey, out fallbackFont))
                        {
                            fallbackTypefaces.Add(fbTypeface);
                            fallbackFont = new SKFont(fbTypeface, fontPixelSize)
                            {
                                Edging = SKFontEdging.SubpixelAntialias,
                                Hinting = SKFontHinting.Normal
                            };
                            fallbackFonts[fbKey] = fallbackFont;
                        }
                        else
                        {
                            fbTypeface.Dispose();
                        }
                    }
                    else
                    {
                        // No system glyph at all — leave dictionary empty (GetGlyph → '?').
                        continue;
                    }
                }

                // SKPaint.MeasureText(string) is the cross-version-stable string overload.
                float advance;
                if (fallbackFont != null)
                {
                    using var fbMeasurePaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = SKColors.White,
                        Typeface = fallbackFont.Typeface,
                        TextSize = fontPixelSize
                    };
                    advance = fbMeasurePaint.MeasureText(s);
                }
                else
                {
                    advance = paint.MeasureText(s);
                }
                if (advance <= 0) advance = fontPixelSize * 0.5f;

                scratchCanvas.Clear(SKColors.Transparent);
                if (fallbackFont != null)
                {
                    using var fbPaint = new SKPaint
                    {
                        IsAntialias = true,
                        Color = SKColors.White,
                        Typeface = fallbackFont.Typeface,
                        TextSize = fontPixelSize
                    };
                    scratchCanvas.DrawText(s, penX, penY, fbPaint);
                }
                else
                {
                    scratchCanvas.DrawText(s, penX, penY, drawPaint);
                }

                if (!FindAlphaBounds(scratch, out int minX, out int minY, out int maxX, out int maxY))
                {
                    _glyphs[ch] = new Glyph(Vector4.Zero, Vector2.Zero, Vector2.Zero, advance);
                    continue;
                }

                int gw = maxX - minX + 1;
                int gh = maxY - minY + 1;

                if (curX + gw + 1 > AtlasW)
                {
                    curX = 1;
                    curY += rowH + 1;
                    rowH = 0;
                }
                if (curY + gh + 1 > AtlasH)
                    break;

                if (gh > rowH) rowH = gh;

                // Copy the ink box from scratch into the atlas.
                var srcRect = new SKRectI(minX, minY, maxX + 1, maxY + 1);
                var dstRect = new SKRect(curX, curY, curX + gw, curY + gh);
                atlasCanvas.DrawBitmap(scratch, srcRect, dstRect);

                _glyphs[ch] = new Glyph(
                    new Vector4(curX / (float)AtlasW, curY / (float)AtlasH,
                                (curX + gw) / (float)AtlasW, (curY + gh) / (float)AtlasH),
                    new Vector2(gw, gh),
                    new Vector2(minX - penX, minY - penY),
                    advance);

                curX += gw + 1;
            }

            // Upload the atlas to GL. SkiaSharp gives us tightly-packed RGBA8 pixels.
            atlasCanvas.Flush();
            byte[] pixels = atlas.Bytes;
            Texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, Texture);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                AtlasW, AtlasH, 0, GlPixelFormat.Rgba, PixelType.UnsignedByte, pixels);
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

        foreach (var f in fallbackFonts.Values) f.Dispose();
        foreach (var t in fallbackTypefaces) t.Dispose();
    }

    public Glyph GetGlyph(char c) => _glyphs.TryGetValue(c, out var g) ? g : _fallback;

    /// <summary>Measure the rendered pixel width of <paramref name="text"/> at the
    /// given pixel size (single-line; '\n' is treated as a regular missing glyph).</summary>
    public float MeasureWidth(string text, float pixelSize)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float scale = pixelSize / FontPixelSize;
        float w = 0f;
        foreach (char ch in text) w += GetGlyph(ch).Advance;
        return w * scale;
    }

    private static unsafe bool FindAlphaBounds(SKBitmap bmp, out int minX, out int minY, out int maxX, out int maxY)
    {
        int w = bmp.Width, h = bmp.Height;
        int rowBytes = bmp.RowBytes;
        byte* basep = (byte*)bmp.GetPixels();
        minX = w; minY = h; maxX = -1; maxY = -1;
        for (int y = 0; y < h; y++)
        {
            byte* row = basep + y * rowBytes;
            for (int x = 0; x < w; x++)
            {
                // SkiaSharp Rgba8888: bytes are R,G,B,A.
                byte a = row[x * 4 + 3];
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

    public void Dispose() => GL.DeleteTexture(Texture);
}
