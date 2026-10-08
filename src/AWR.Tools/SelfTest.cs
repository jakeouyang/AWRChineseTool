namespace AWR.Tools;

internal static class SelfTest
{
    public static void Run(string game, string fontDir, string output)
    {
        Directory.CreateDirectory(output);
        void Assert(bool value, string why) { if (!value) throw new InvalidDataException("TEST FAILED: " + why); }
        var escaped = new List<StringEntry> { new() { Id = @"WRITER\NEW", Value = "第一行\n第二行\t" + @"literal\n\test" }, new() { Id = "empty", Value = "" } };
        string tsv = Path.Combine(output, "roundtrip.tsv");
        StringTable.ExportCsv(escaped, tsv);
        var read = StringTable.ImportCsv(tsv);
        Assert(read.Select(e => (e.Id, e.Value)).SequenceEqual(escaped.Select(e => (e.Id, e.Value))), "TSV escaping/newlines/empty values");
        var binary = StringTable.Build(escaped);
        Assert(StringTable.Build(StringTable.Parse(binary)).SequenceEqual(binary), "string table roundtrip");
        File.WriteAllText(Path.Combine(output, "duplicate.tsv"), "id\ttext\nx\tone\nx\ttwo\n");
        bool rejected = false;
        try { StringTable.ImportCsv(Path.Combine(output, "duplicate.tsv")); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "duplicate IDs rejected");
        rejected = false;
        try { FontService.ValidateText(new() { new() { Id = "unknown", Value = "a" } }, escaped); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "unknown IDs rejected");
        Console.WriteLine("PASS text escaping, binary roundtrip, duplicate and unknown ID rejection");
        var texts = FontService.LoadText(game, true, Console.WriteLine);
        var charset = new HashSet<int>(Enumerable.Range(32, 95));
        foreach (var e in texts) foreach (char c in e.Value) if (!char.IsControl(c)) charset.Add(c);
        using var fallback = TrueTypeFont.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"));
        var fonts = new[] { "jiangxizhuokai.ttf", "LXGWWenKai-Light.ttf", "GenRyuMin.ttf", "GenWanMin2TC-M.otf" };
        foreach (string name in fonts)
        {
            using var font = TrueTypeFont.Load(Path.Combine(fontDir, name));
            Assert(charset.All(c => font.HasGlyph(c) || fallback.HasGlyph(c)), "coverage " + name);
            byte[] data = BinfntBuilder.Build(font, charset, 64, 4096, fallback: fallback, report: Console.WriteLine);
            var parsed = new BinfntFile(data); parsed.RequireCoverage(charset);
            int nv = BitConverter.ToInt32(data, 4), ip = 8 + nv * 16;
            Assert(BitConverter.ToUInt16(data, ip + 4 + 12) == 0, "local indices, second glyph starts at zero");
            Assert(BitConverter.ToUInt32(data, parsed.GlyphOffset + 8) == 0, "red channel flag");
            var corrupt = (byte[])data.Clone();
            BitConverter.GetBytes((ushort)4).CopyTo(corrupt, ip + 4 + 12);
            rejected = false;
            try { _ = new BinfntFile(corrupt); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "old global-index regression detected");
            parsed.Preview("继续游戏 新游戏 章节 选项 附加内容 退出\n亮瀑镇 简体字体测试 ABC abc 0123456789", Path.Combine(output, name + ".png"));
            Console.WriteLine($"PASS {name}: {charset.Count} characters; TTF/OTF native generation, fallback TTC, map, indices, DDS, preview");
        }
        Console.WriteLine("ALL TESTS PASSED");
    }
}
