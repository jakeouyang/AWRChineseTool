using System.Buffers.Binary;
using System.Text;

namespace AWR.Tools;

/// <summary>
/// Builds an Alan Wake Remastered .binfnt (customer_facing) file from a TrueType font.
/// Format verified against the original EN/CHT and intergra fonts:
///   u32 magic = 4
///   u32 numVerts = 4 * numGlyphs
///   verts: 4 per glyph (BL, BR, TR, TL), each (x, y, u, v) float32 LE; y-up, v-down (D3D)
///   u32 numIndices = 6 * numGlyphs; LOCAL u16 indices (0,1,2, 0,2,3) for every glyph
///   u32 numGlyphs
///   entries (44 B): u16 vertexOffset, vertexCount, indexOffset, indexCount;
///                  u32 texture-channel; 8 float32 layout coordinates
///   charlist: 65536 x u16 LE  (char code -> glyph id, 0 = .notdef box)
///   u32 kerningCount; kerningCount x 12 bytes; u32 ddsSize; DDS including mipmaps
/// Layout units: 2.72 per em (matches shipped cht font; CJK fullwidth advance = 2.72).
/// </summary>
public static class BinfntBuilder
{
    public const float UnitsPerEm = 2.72f;
    public const float BaselineOffset = 0.52f; // shipped font draws ink ~0.52 units above the pen origin

    // Compatibility entry point: rebuild instead of overwriting unknown atlas layouts.
    public static byte[] ReplaceAtlas(byte[] originalBinfnt, TrueTypeFont font, IEnumerable<int> charset, int emPixels = 48)
        => Build(font, charset, emPixels);

    public static byte[] Build(TrueTypeFont font, IEnumerable<int> codePoints, int emPixels = 48, int atlasWidth = 2048, bool chtLayout = false, TrueTypeFont? fallback = null, Action<string>? report = null)
    {
        var cps = codePoints.Distinct().OrderBy(c => c).ToList();
        if (cps.Any(c => c < 0 || c >= 65536)) throw new InvalidDataException("binfnt 只支持 BMP 字符。");
        if (cps.Count > 10921) throw new InvalidDataException("字符集超过 16 位索引偏移容量（10921 字符）。");
        int count = cps.Count + 1; // glyph 0 = .notdef box

        // Reduce raster size only when the packed atlas exceeds the configured cap.
        byte[]? result;
        int maxH = EnvInt("AWR_FONT_MAXATLAS", 4096);
        while (true)
        {
            result = BuildInternal(font, cps, count, emPixels, atlasWidth <= maxH ? atlasWidth : maxH, maxH, chtLayout, fallback);
            if (result != null) { report?.Invoke($"实际烘焙：{emPixels}px/em，图集 {Math.Min(atlasWidth,maxH)}×{Math.Min(atlasWidth,maxH)}。"); return result; }
            emPixels -= 4;
            if (emPixels < 20) throw new InvalidOperationException($"字符集太大，无法在 {maxH} 高度图集内容纳。");
        }
    }

    internal static int EnvInt(string name, int def) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : def;

    private static byte[]? BuildInternal(TrueTypeFont font, List<int> cps, int count, int emPixels, int atlasWidth, int maxAtlasHeight, bool chtLayout, TrueTypeFont? fallback)
    {
        float pxPerFontUnit = (float)emPixels / font.UnitsPerEm;
        float fontToGame = UnitsPerEm / font.UnitsPerEm; // font units -> game units

        // ---- rasterize glyphs & pack ----
        // per image: cp, alpha, w, h, x0f (image left edge in font units), y0f (image bottom edge in font units), advance (game units)
        var images = new List<(int Cp, byte[] Alpha, int W, int H, float X0f, float Y0f, float AdvU)>(count);
        images.Add(NotdefBox(emPixels));

        foreach (int cp in cps)
        {
            var selected = font.HasGlyph(cp) ? font : fallback ?? font;
            int gid = selected.GetGlyphIndex(cp);
            var outline = selected.GetOutline(gid);
            ushort advanceFu = outline.Advance;
            float advGame = advanceFu * fontToGame;

            if (outline.Points.Count == 0)
            {
                // no contours: blank glyph (e.g. space) — 1px empty image
                images.Add((cp, new byte[1], 1, 1, 0f, 0f, advGame));
                continue;
            }
            // image bounds in pixels (font units rasterized), 1px padding
            int x0px = (int)Math.Floor(outline.XMin * pxPerFontUnit) - 1;
            int y0px = (int)Math.Floor(outline.YMin * pxPerFontUnit) - 1;
            int x1px = (int)Math.Ceiling(outline.XMax * pxPerFontUnit) + 1;
            int y1px = (int)Math.Ceiling(outline.YMax * pxPerFontUnit) + 1;
            int w = x1px - x0px, h = y1px - y0px;
            byte[] alpha = Rasterize(outline, w, h, x0px, y0px, pxPerFontUnit);
            float x0f = x0px / pxPerFontUnit;             // font units
            float y0f = y0px / pxPerFontUnit;
            images.Add((cp, alpha, w, h, x0f, y0f, advGame));
        }

        // ---- shelf packing ----
        const int pad = 1;
        int cursorX = pad, cursorY = pad, rowH = 0;
        var placed = new List<(int X, int Y)>();
        for (int i = 0; i < images.Count; i++)
        {
            var (_, _, w, h, _, _, _) = images[i];
            if (w + pad * 2 > atlasWidth) return null;
            if (cursorX + w + pad > atlasWidth)
            {
                cursorX = pad;
                cursorY += rowH + pad;
                rowH = 0;
            }
            placed.Add((cursorX, cursorY));
            cursorX += w + pad;
            rowH = Math.Max(rowH, h);
        }
        int required = cursorY + rowH + pad;
        int atlasHeight = atlasWidth;
        if (required > atlasHeight || atlasHeight > maxAtlasHeight) return null;

        var atlas = new byte[atlasWidth * atlasHeight * 4];
        for (int i = 0; i < images.Count; i++)
        {
            var (_, alpha, w, h, _, _, _) = images[i];
            var (px, py) = placed[i];
            for (int y = 0; y < h; y++)
            {
                int src = y * w;
                int dst = ((py + y) * atlasWidth + px) * 4;
                for (int x = 0; x < w; x++)
                {
                    byte a = alpha[src + x];
                    atlas[dst] = a; atlas[dst + 1] = a; atlas[dst + 2] = a; atlas[dst + 3] = a;
                    dst += 4;
                }
            }
        }

        // ---- assemble binfnt ----
        using var ms = new MemoryStream();
        using (var wtr = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            wtr.Write((uint)4);                    // magic
            wtr.Write((uint)(4 * count));          // numVerts
            for (int i = 0; i < images.Count; i++)
            {
                var (_, _, w, h, x0f, y0f, _) = images[i];
                var (px, py) = placed[i];
                float x0, x1, yb, yt;
                if (i == 0)
                {
                    x0 = 0; x1 = 1.0f; yb = BaselineOffset; yt = BaselineOffset + 1.6f;
                }
                else
                {
                    // image spans font units [x0f, x0f + w/pxPerFontUnit] horizontally,
                    // [y0f, y0f + h/pxPerFontUnit] vertically (y-up, baseline at 0)
                    x0 = x0f * fontToGame;
                    x1 = x0f * fontToGame + w / pxPerFontUnit * fontToGame;
                    yb = BaselineOffset + y0f * fontToGame;
                    yt = BaselineOffset + y0f * fontToGame + h / pxPerFontUnit * fontToGame;
                }
                float u0 = px / (float)atlasWidth, u1 = (px + w) / (float)atlasWidth;
                float v0 = py / (float)atlasHeight, v1 = (py + h) / (float)atlasHeight; // v-down
                WriteVert(wtr, x0, yb, u0, v1); // BL
                WriteVert(wtr, x1, yb, u1, v1); // BR
                WriteVert(wtr, x1, yt, u1, v0); // TR
                WriteVert(wtr, x0, yt, u0, v0); // TL
            }
            wtr.Write((uint)(6 * count));
            for (int i = 0; i < count; i++)
            {
                ushort b = 0; // relative to the glyph's vertexOffset; the engine adds it
                wtr.Write(b); wtr.Write((ushort)(b + 1)); wtr.Write((ushort)(b + 2));
                wtr.Write(b); wtr.Write((ushort)(b + 2)); wtr.Write((ushort)(b + 3));
            }
            wtr.Write((uint)count);
            for (int i = 0; i < images.Count; i++)
            {
                var (_, _, _, _, _, _, advU) = images[i];
                wtr.Write(checked((ushort)(4 * i)));
                wtr.Write((ushort)4);
                wtr.Write(checked((ushort)(6 * i)));
                wtr.Write((ushort)6);
                wtr.Write(0u); // texture channel (red), NOT a coordinate
                // Layout rectangle: 8 floats. Horizontal advance is the right edge.
                wtr.Write(0f); wtr.Write(-0.8f);
                wtr.Write(advU); wtr.Write(-0.8f);
                wtr.Write(advU); wtr.Write(1.8f);
                wtr.Write(0f); wtr.Write(1.8f);
            }
            // charlist
            var charlist = new ushort[65536];
            for (int i = 1; i < images.Count; i++)
            {
                int cp = images[i].Cp;
                if (cp is > 0 and < 65536) charlist[cp] = (ushort)i;
            }
            foreach (ushort g in charlist) wtr.Write(g);
            // empty kerning table (shipped EN fonts carry one; the loader reads its entry count)
            wtr.Write(0u);
            // DDS with a 2-level mip chain (shipped EN fonts always carry one)
            byte[] dds = BuildDds(atlas, atlasWidth, atlasHeight, mipChain: true);
            wtr.Write((uint)dds.Length);
            wtr.Write(dds);
        }
        return ms.ToArray();
    }

    private static void WriteVert(BinaryWriter w, float x, float y, float u, float v)
    {
        w.Write(x); w.Write(y); w.Write(u); w.Write(v);
    }

    private static (int, byte[], int, int, float, float, float) NotdefBox(int emPixels)
    {
        int w = emPixels / 2, h = emPixels * 2 / 3;
        var a = new byte[w * h];
        int t = Math.Max(1, h / 16), l = Math.Max(1, w / 16);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (y < t || y >= h - t || x < l || x >= w - l) a[y * w + x] = 255;
        return (0, a, w, h, 0, 0, 1.0f);
    }

    /// <summary>Scanline rasterizer with 4x vertical supersampling; nonzero winding.</summary>
    private static byte[] Rasterize(TrueTypeFont.GlyphOutline outline, int w, int h, int ox, int oy, float pxPerUnit)
    {
        var alpha = new byte[w * h];
        if (outline.Points.Count == 0) return alpha;

        // flatten quadratic contours into polylines (in pixel space)
        var segs = new List<(float X0, float Y0, float X1, float Y1)>();
        var pts = outline.Points;
        int startPt = 0;
        foreach (int endPt in outline.ContourEnds)
        {
            int n = endPt - startPt + 1;
            if (n < 3) { startPt = endPt + 1; continue; }
            var poly = new List<(float X, float Y)>(n * 2);
            for (int i = 0; i < n; i++)
            {
                var p = pts[startPt + i];
                poly.Add((p.X * pxPerUnit - ox, p.Y * pxPerUnit - oy));
            }
            // polyline already has on-curve + midpoints inserted by parser: treat as polygon
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                if (a.X != b.X || a.Y != b.Y) segs.Add((a.X, a.Y, b.X, b.Y));
            }
            startPt = endPt + 1;
        }
        if (segs.Count == 0) return alpha;

        const int SS = 8;
        var coverage = new float[w * h];
        var crossX = new List<float>(256);
        var crossW = new List<int>(256);
        for (int py = 0; py < h; py++)
        {
            for (int ss = 0; ss < SS; ss++)
            {
                float sy = h - py - (ss + 0.5f) / SS; // TTF y-up → bitmap row 0 = glyph top
                crossX.Clear(); crossW.Clear();
                foreach (var (x0, y0, x1, y1) in segs)
                {
                    float ymin = Math.Min(y0, y1), ymax = Math.Max(y0, y1);
                    if (sy < ymin || sy >= ymax) continue;
                    float t = (sy - y0) / (y1 - y0);
                    crossX.Add(x0 + (x1 - x0) * t);
                    crossW.Add(y1 > y0 ? 1 : -1);
                }
                if (crossX.Count == 0) continue;
                // sort by x, walk with winding
                var order = Enumerable.Range(0, crossX.Count).OrderBy(i => crossX[i]).ToArray();
                int winding = 0;
                float spanStart = 0;
                for (int oi = 0; oi < order.Length; oi++)
                {
                    int idx = order[oi];
                    int prevWinding = winding;
                    winding += crossW[idx];
                    if (prevWinding == 0 && winding != 0) spanStart = crossX[idx];
                    else if (prevWinding != 0 && winding == 0)
                    {
                        FillSpan(coverage, w, py, spanStart, crossX[idx]);
                    }
                }
            }
        }
        for (int i = 0; i < alpha.Length; i++) alpha[i] = (byte)Math.Clamp((int)Math.Round(coverage[i] * 255 / SS), 0, 255);
        return alpha;
    }

    private static void FillSpan(float[] coverage, int w, int py, float xa, float xb)
    {
        int x0 = Math.Max(0, (int)Math.Floor(xa));
        int x1 = Math.Min(w - 1, (int)Math.Ceiling(xb) - 1);
        for (int x = x0; x <= x1; x++)
            coverage[py * w + x] += Math.Max(0, Math.Min(xb, x + 1) - Math.Max(xa, x));
    }

    private static byte[] BuildDds(byte[] bgra, int width, int height, bool mipChain = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, true);
        w.Write(0x20534444u); // 'DDS '
        w.Write(124u);        // dwSize
        w.Write(0x0002100Fu); // flags: CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT
        w.Write((uint)height);
        w.Write((uint)width);
        w.Write((uint)(width * 4)); // pitch
        w.Write(1u);          // depth
        w.Write(mipChain ? 2u : 1u); // mipmaps
        for (int i = 0; i < 11; i++) w.Write(0u); // reserved
        w.Write(32u);         // pf size
        w.Write(0x41u);       // pf flags: ALPHAPIXELS | RGB
        w.Write(0u);          // fourcc
        w.Write(32u);         // rgb bit count
        w.Write(0x00FF0000u); // R mask
        w.Write(0x0000FF00u); // G mask
        w.Write(0x000000FFu); // B mask
        w.Write(0xFF000000u); // A mask
        w.Write(mipChain ? 0x00401008u : 0x00001000u); // caps: TEXTURE | (COMPLEX|MIPMAP when mip chain present)
        w.Write(0u); w.Write(0u); w.Write(0u); w.Write(0u); // caps2, caps3, reserved2 (+1 pad to 128 total)
        w.Write(bgra);
        if (mipChain)
        {
            // level 1: half resolution, 2x2 box downsample
            int hw = width / 2, hh = height / 2;
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < hw; x++)
                {
                    int o0 = ((y * 2) * width + (x * 2)) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = bgra[o0 + c] + bgra[o0 + 4 + c] + bgra[o0 + width * 4 + c] + bgra[o0 + width * 4 + 4 + c];
                        w.Write((byte)(sum / 4));
                    }
                }
        }
        return ms.ToArray();
    }
}
