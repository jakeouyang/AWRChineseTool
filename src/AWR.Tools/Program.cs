using System.Runtime.InteropServices;

namespace AWR.Tools;

internal static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 0) { Application.Run(new MainForm()); return 0; }
        AttachConsole(-1);
        try
        {
            switch (args)
            {
                case ["self-test", var game, var fonts, var output]:
                    SelfTest.Run(game, fonts, output); break;
                case ["--render-editor", var game, var output]:
                    using (var form = new TextEditorForm(FontService.LoadText(game, true, Console.WriteLine), true))
                    {
                        form.Show(); Application.DoEvents();
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    break;
                case ["import-text", var file]:
                    FontService.ImportText(file, Console.WriteLine); break;
                case ["font-preview", var fontFile, var output]:
                    new BinfntFile(File.ReadAllBytes(fontFile)).Preview("继续游戏 新游戏 章节 选项 附加内容 退出\n亮瀑镇 简体字体测试 ABC abc 0123456789", output); break;
                case ["install-intergra", var game]:
                    FontService.InstallIntergraBase(game, Console.WriteLine);
                    break;
                case ["install", var game, .. var options]:
                    if (options.Contains("--intergra"))
                    {
                        FontService.InstallIntergraBase(game, Console.WriteLine);
                    }
                    else
                    {
                        FontService.InstallEn(game, GetFont(options), options.Contains("--simplified") || options.Contains("--simp"),
                            Console.WriteLine, mergeCht: !options.Contains("--keep-en"), highQuality: !options.Contains("--standard"));
                    }
                    break;
                case ["restore", var game]:
                    FontService.RestoreEn(game, Console.WriteLine);
                    break;
                case ["export-text", var game]:
                    FontService.ExportText(game, Console.WriteLine);
                    break;
                case ["export-simplified", var game]:
                    FontService.ExportSimplifiedText(game, Console.WriteLine);
                    break;
                case ["en-font-install-file", var fontFile, var binPath, var rmdpPath]:
                    {
                        var bytes = File.ReadAllBytes(fontFile);
                        var arc = RmdpArchive.Load(binPath);
                        string outBin = Path.Combine(Path.GetTempPath(), "ef.bin");
                        string outRmdp = Path.Combine(Path.GetTempPath(), "ef.rmdp");
                        var rep = new Dictionary<string, byte[]>();
                        foreach (var fontRel in FontService.EnFonts) rep[fontRel] = bytes;
                        arc.Repack(rmdpPath, rep, outBin, outRmdp);
                        File.Copy(outBin, binPath, true);
                        File.Copy(outRmdp, rmdpPath, true);
                        Console.WriteLine($"EN font installed: {bytes.Length:N0} bytes x{rep.Count}");
                    }
                    break;
                case ["validate-game", var game]:
                    if (FontService.ValidateGame(game) is { Length: > 0 } error) throw new InvalidDataException(error);
                    Console.WriteLine("VALID");
                    break;
                case ["list", var binPath]:
                    ListPack(binPath);
                    break;
                case ["font-debug", var ttfPath, var outPath]:
                    FontDebug(ttfPath, outPath);
                    break;
                case ["font-mini", var ttfPath, var binPath, var rmdpPath]:
                    // test: build ASCII-only font and install into a pack copy (for crash bisecting)
                    var cps = new List<int>();
                    for (int c = 0x20; c <= 0x7E; c++) cps.Add(c);
                    using (var ttf = TrueTypeFont.Load(ttfPath))
                    {
                        var bytes = BinfntBuilder.Build(ttf, cps.Where(ttf.HasGlyph), emPixels: 48);
                        var arc = RmdpArchive.Load(binPath);
                        string outBin = Path.Combine(Path.GetTempPath(), "mini.bin");
                        string outRmdp = Path.Combine(Path.GetTempPath(), "mini.rmdp");
                        arc.Repack(rmdpPath, new Dictionary<string, byte[]> { [FontService.FontPath] = bytes }, outBin, outRmdp);
                        File.Copy(outBin, Path.Combine(Path.GetDirectoryName(binPath)!, Path.GetFileName(binPath)), true);
                        File.Copy(outRmdp, Path.Combine(Path.GetDirectoryName(rmdpPath)!, Path.GetFileName(rmdpPath)), true);
                        Console.WriteLine($"mini font installed: {bytes.Length:N0} bytes, {cps.Count + 1} glyphs");
                    }
                    break;
                case ["font-install-file", var fontFile, var binPath, var rmdpPath]:
                    {
                        var bytes = File.ReadAllBytes(fontFile);
                        var arc = RmdpArchive.Load(binPath);
                        string outBin = Path.Combine(Path.GetTempPath(), "fi.bin");
                        string outRmdp = Path.Combine(Path.GetTempPath(), "fi.rmdp");
                        arc.Repack(rmdpPath, new Dictionary<string, byte[]> { [FontService.FontPath] = bytes }, outBin, outRmdp);
                        File.Copy(outBin, binPath, true);
                        File.Copy(outRmdp, rmdpPath, true);
                        Console.WriteLine($"font file installed: {bytes.Length:N0} bytes");
                    }
                    break;
                case ["--render-ui", var output, .. var options]:
                    try
                    {
                        using (var form = new MainForm())
                        {
                            form.SelectInterfaceLanguage(options.Contains("--zh"));
                            form.Show(); Application.DoEvents();
                            using var bitmap = new Bitmap(form.Width, form.Height);
                            form.DrawToBitmap(bitmap, form.ClientRectangle);
                            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.WriteLine("UI rendered: " + output);
                    }
                    catch (Exception renderEx)
                    {
                        File.WriteAllText(Path.ChangeExtension(output, ".err.txt"), renderEx.ToString());
                        throw;
                    }
                    break;
                case ["roundtrip", var binPath, var rmdpPath, var outDir]:
                    Roundtrip(binPath, rmdpPath, outDir);
                    break;
                default:
                    throw new ArgumentException(
                        "Commands: install <game> [font.ttf] [--simplified] | restore <game> | export-text <game> | validate-game <game> | list <bin> | roundtrip <bin> <rmdp> <extractDir> | font-debug <ttf> <out.binfnt>");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    static string? GetFont(string[] options)
    {
        for (int i = 0; i < options.Length; i++)
            if (options[i] == "--font" && i + 1 < options.Length) return options[i + 1];
        return options.FirstOrDefault(x => !x.StartsWith("--") && File.Exists(x));
    }

    static void ListPack(string binPath)
    {
        var archive = RmdpArchive.Load(binPath);
        foreach (var f in archive.Files)
            Console.WriteLine($"{f.Path,-60} offset={f.Offset,12} size={f.Size}");
    }

    static void FontDebug(string ttfPath, string outPath)
    {
        // build with the ASCII + CJK sample charset, print layout accounting
        var cps = new List<int>();
        for (int c = 0x20; c <= 0x7E; c++) cps.Add(c);
        foreach (var s in new[] { "亮瀑镇中文字体测试的了一是我不人在他有这", "，。！？：；、…—「」『』（）" })
            foreach (char ch in s) cps.Add(ch);
        using var ttf = TrueTypeFont.Load(ttfPath);
        cps = cps.Where(ttf.HasGlyph).Distinct().ToList();
        var bytes = BinfntBuilder.Build(ttf, cps, emPixels: 48);
        File.WriteAllBytes(outPath, bytes);
        var parsed = new BinfntFile(bytes);
        parsed.RequireCoverage(cps);
        Console.WriteLine($"Validated: {bytes.Length:N0} bytes, {parsed.GlyphCount} glyphs, {parsed.Width}x{parsed.Height} atlas");

    }

    static void Roundtrip(string binPath, string rmdpPath, string outDir)
    {
        var archive = RmdpArchive.Load(binPath);
        Directory.CreateDirectory(outDir);
        archive.ExtractAll(rmdpPath, outDir);
        Console.WriteLine($"Extracted {archive.Files.Count} files to {outDir}");
        var replacements = new Dictionary<string, byte[]>();
        foreach (var f in archive.Files)
        {
            string p = Path.Combine(outDir, f.Path.Replace('\\', Path.DirectorySeparatorChar));
            replacements[f.Path] = File.ReadAllBytes(p);
        }
        string outBin = Path.Combine(outDir, "roundtrip.bin");
        string outRmdp = Path.Combine(outDir, "roundtrip.rmdp");
        archive.Repack(rmdpPath, replacements, outBin, outRmdp);
        Console.WriteLine("Repacked. Comparing…");
        Console.WriteLine("bin identical: " + FilesEqual(binPath, outBin));
        Console.WriteLine("rmdp identical: " + FilesEqual(rmdpPath, outRmdp));
    }

    static bool FilesEqual(string a, string b)
    {
        var fa = File.ReadAllBytes(a);
        var fb = File.ReadAllBytes(b);
        if (fa.Length != fb.Length) { Console.WriteLine($"size differ: {fa.Length:N0} vs {fb.Length:N0}"); return false; }
        for (int i = 0; i < fa.Length; i++)
            if (fa[i] != fb[i]) { Console.WriteLine($"first diff at {i}: {fa[i]:X2} vs {fb[i]:X2}"); return false; }
        return true;
    }
}
