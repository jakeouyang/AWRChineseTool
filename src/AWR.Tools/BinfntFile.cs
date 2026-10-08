namespace AWR.Tools;

// Independent reader used before installing any generated font.
public sealed class BinfntFile
{
    public byte[] Bytes { get; }
    public int GlyphCount { get; }
    public int MapOffset { get; }
    public int GlyphOffset { get; }
    public int DdsOffset { get; }
    public int Width { get; }
    public int Height { get; }
    public int Glyph(int cp) => BitConverter.ToUInt16(Bytes, MapOffset + cp * 2);
    public BinfntFile(byte[] bytes)
    {
        Bytes = bytes;
        int U(int p) => checked((int)BitConverter.ToUInt32(bytes, p));
        if (U(0) != 4) throw new InvalidDataException("Unsupported binfnt version.");
        int nv = U(4), ip = checked(8 + nv * 16), ni = U(ip);
        int gp = checked(ip + 4 + ni * 2);
        GlyphCount = U(gp); GlyphOffset = gp + 4;
        MapOffset = checked(GlyphOffset + GlyphCount * 44);
        int kp = checked(MapOffset + 131072), nk = U(kp);
        int sizep = checked(kp + 4 + nk * 12);
        DdsOffset = sizep + 4;
        if (U(sizep) != bytes.Length - DdsOffset || U(DdsOffset) != 0x20534444)
            throw new InvalidDataException("Invalid character map / kerning / DDS boundaries.");
        Height = U(DdsOffset + 12); Width = U(DdsOffset + 16);
        if (Width <= 0 || Height <= 0 || U(DdsOffset + 88) != 32 || U(DdsOffset + 84) != 0)
            throw new InvalidDataException("Expected uncompressed 32-bit DDS.");
        long expected = 128; int w = Width, h = Height;
        for (int m = 0; m < Math.Max(1, U(DdsOffset + 28)); m++)
        { expected += (long)w * h * 4; w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); }
        if (expected != bytes.Length - DdsOffset) throw new InvalidDataException("DDS mip byte count mismatch.");
        for (int c = 0; c < 65536; c++)
            if (Glyph(c) >= GlyphCount) throw new InvalidDataException($"Invalid mapping U+{c:X4}.");
        for (int g = 0; g < GlyphCount; g++)
        {
            int p = GlyphOffset + g * 44;
            int vo = BitConverter.ToUInt16(bytes, p), vc = BitConverter.ToUInt16(bytes, p + 2);
            int io = BitConverter.ToUInt16(bytes, p + 4), ic = BitConverter.ToUInt16(bytes, p + 6);
            if (vo + vc > nv || io + ic > ni) throw new InvalidDataException($"Invalid glyph ranges {g}.");
            for (int i = 0; i < vc; i++)
            {
                int v = 8 + (vo + i) * 16;
                for (int k = 0; k < 4; k++)
                    if (!float.IsFinite(BitConverter.ToSingle(bytes, v + k * 4))) throw new InvalidDataException("Non-finite vertex.");
                float u = BitConverter.ToSingle(bytes, v + 8), t = BitConverter.ToSingle(bytes, v + 12);
                if (u < 0 || u > 1 || t < 0 || t > 1) throw new InvalidDataException("UV outside atlas.");
            }
            for (int i = 0; i < ic; i++)
                if (BitConverter.ToUInt16(bytes, ip + 4 + (io + i) * 2) >= vc) throw new InvalidDataException("Local vertex index outside glyph.");
        }
    }

    public void RequireCoverage(IEnumerable<int> chars)
    {
        var missing = chars.Where(c => c >= 65536 || c < 0 || Glyph(c) == 0).ToArray();
        if (missing.Length > 0) throw new InvalidDataException($"字体缺少 {missing.Length} 个字符：" + string.Join(" ", missing.Take(30).Select(c => $"U+{c:X4} {char.ConvertFromUtf32(c)}")));
    }

    // Render by reading the serialized map, metrics, UVs and DDS, not the source TTF.
    public void Preview(string text, string output)
    {
        using var atlas = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = atlas.LockBits(new Rectangle(0, 0, Width, Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, atlas.PixelFormat);
        try { System.Runtime.InteropServices.Marshal.Copy(Bytes, DdsOffset + 128, bits.Scan0, Width * Height * 4); }
        finally { atlas.UnlockBits(bits); }
        const float scale = 18;
        using var result = new Bitmap(1400, 220);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.FromArgb(20, 23, 28));
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        float x = 20, baseline = 85;
        foreach (char c in text)
        {
            if (c == '\n') { x = 20; baseline += 90; continue; }
            int g = Glyph(c), p = GlyphOffset + g * 44;
            int vo = BitConverter.ToUInt16(Bytes, p);
            var verts = Enumerable.Range(0, 4).Select(i => Enumerable.Range(0, 4).Select(k => BitConverter.ToSingle(Bytes, 8 + (vo + i) * 16 + k * 4)).ToArray()).ToArray();
            float x0 = verts.Min(v => v[0]), x1 = verts.Max(v => v[0]), y0 = verts.Min(v => v[1]), y1 = verts.Max(v => v[1]);
            float u0 = verts.Min(v => v[2]), u1 = verts.Max(v => v[2]), v0 = verts.Min(v => v[3]), v1 = verts.Max(v => v[3]);
            if (x1 > x0 && y1 > y0)
                graphics.DrawImage(atlas, new RectangleF(x + x0 * scale, baseline - y1 * scale, (x1-x0)*scale, (y1-y0)*scale), new RectangleF(u0*Width,v0*Height,(u1-u0)*Width,(v1-v0)*Height), GraphicsUnit.Pixel);
            x += BitConverter.ToSingle(Bytes, p + 20) * scale;
        }
        result.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }
}
