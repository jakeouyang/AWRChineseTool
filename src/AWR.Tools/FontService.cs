using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AWR.Tools;

public static class FontService
{
    public const string ChtBin = "ep999-000-cht.bin", ChtRmdp = "ep999-000-cht.rmdp";
    public const string EnBin = "ep999-000-en.bin", EnRmdp = "ep999-000-en.rmdp";
    public const string StringTablePath = @"locale\cht\string_table.bin", FontPath = @"fonts\locale\cht\customer_facing.binfnt";
    public const string EnStringTablePath = @"locale\en\string_table.bin";
    public const string AdditionalXml = "cht_additional_strings.xml", EnAdditionalXml = "en_additional_strings.xml";
    public static readonly string[] EnFonts = { @"fonts\locale\en\customer_facing.binfnt", @"fonts\locale\en\customer_facing_bold.binfnt", @"fonts\locale\en\customer_facing_typewriter.binfnt" };
    public static string WorkspaceDir => Path.Combine(AppContext.BaseDirectory, "Workspace");
    public static string BackupDir => Path.Combine(WorkspaceDir, "backup-v3");
    public static string TextDir => Path.Combine(WorkspaceDir, "text");
    public static string OverridesCsv => Path.Combine(TextDir, "overrides.csv");
    public static string OverridesFor(bool simplified) => simplified ? OverridesCsv : Path.Combine(TextDir, "overrides-traditional.tsv");
    public static string ExportedCsv => Path.Combine(TextDir, "strings.tsv");
    public static string IntergraDir => Path.Combine(WorkspaceDir, "intergra");
    public static string DataDir(string game) => Path.Combine(game, "data");
    static string Key(string game) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(game).TrimEnd('\\').ToUpperInvariant())))[..16];
    static string Backup(string game) => Path.Combine(BackupDir, Key(game));
    static string BaseFile(string game, string relative) => File.Exists(Path.Combine(Backup(game), relative)) ? Path.Combine(Backup(game), relative) : Path.Combine(game, relative);
    static string RefData => Directory.Exists(Path.Combine(IntergraDir, "data")) ? Path.Combine(IntergraDir, "data") : IntergraDir;
    public static string ValidateIntergraBase() => File.Exists(Path.Combine(RefData, EnBin)) && File.Exists(Path.Combine(RefData, EnRmdp)) ? "" : "请将参考 MOD 完整解压到 Workspace\\intergra（保留 data、licenses）。";
    public static string ValidateGame(string game) => Validate(game, "cht");
    public static string ValidateGameEn(string game) => Validate(game, "en");
    static string Validate(string game, string slot) => new[] { ".bin", ".rmdp" }.All(ext => File.Exists(Path.Combine(DataDir(game), $"ep999-000-{slot}{ext}"))) ? "" : $"游戏目录缺少 {slot} 语言资源包。";
    static void CheckIdle(string game)
    {
        foreach (var p in Process.GetProcessesByName("Game_f_x64_EOS"))
        {
            using (p)
            {
                string? executable;
                try { executable = p.MainModule?.FileName; }
                catch { throw new InvalidOperationException("无法确认正在运行的游戏目录，请退出游戏后操作。"); }
                if (executable != null && Path.GetFullPath(Path.GetDirectoryName(executable)!).Equals(Path.GetFullPath(game).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("请先退出游戏，再安装或还原。");
            }
        }
    }
    static void SaveBackup(string game, IEnumerable<string> files)
    {
        string dir = Backup(game); Directory.CreateDirectory(dir);
        foreach (string rel in files)
        {
            string src = Path.Combine(game, rel), dst = Path.Combine(dir, rel);
            if (!File.Exists(dst) && File.Exists(src)) { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(src, dst); }
        }
        File.WriteAllText(Path.Combine(dir, "game.txt"), Path.GetFullPath(game));
    }
    static List<StringEntry> ReadStrings(RmdpArchive arc, string data, string slot)
        => StringTable.Parse(arc.ReadFileData(data, arc.Files.Single(f => f.Path.Equals($@"locale\{slot}\string_table.bin", StringComparison.OrdinalIgnoreCase))));
    static XDocument? Xml(string file) => File.Exists(file) ? XDocument.Load(file, LoadOptions.PreserveWhitespace) : null;
    static Dictionary<string, XElement> XmlTexts(XDocument? doc) => doc == null ? new() : doc.Descendants().Where(x => x.Name.LocalName == "LocalizedString")
        .Where(x => x.Elements().Any(e => e.Name.LocalName == "Name") && x.Elements().Any(e => e.Name.LocalName == "Text"))
        .ToDictionary(x => x.Elements().First(e => e.Name.LocalName == "Name").Value, x => x.Elements().First(e => e.Name.LocalName == "Text"), StringComparer.Ordinal);

    public static void ImportText(string file, Action<string> report)
    {
        var entries = StringTable.ImportCsv(file);
        Directory.CreateDirectory(TextDir);
        StringTable.ExportCsv(entries, OverridesCsv);
        report($"已导入 {entries.Count} 条翻译，安装时生效：{OverridesCsv}");
    }
    public static List<StringEntry> LoadText(string game, bool simplified, Action<string> report)
    {
        var source = Prepare(game, simplified, true, report);
        return source.Strings.Concat(XmlTexts(source.Xml).Select(x => new StringEntry { Id = "xml:" + x.Key, Value = x.Value.Value })).ToList();
    }
    public static void ValidateText(List<StringEntry> entries, List<StringEntry> baseline)
    {
        var ids = baseline.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!seen.Add(e.Id)) throw new InvalidDataException("重复 ID：" + e.Id);
            if (!ids.ContainsKey(e.Id)) throw new InvalidDataException("未知 ID：" + e.Id);
            if (e.Value.Any(char.IsSurrogate)) throw new InvalidDataException("游戏字体不支持 BMP 以外的字符：" + e.Id);
            if (e.Value.Contains('\0')) throw new InvalidDataException("文本包含 NUL 字符：" + e.Id);
        }
    }
    public static void SaveText(List<StringEntry> entries, List<StringEntry> baseline, bool simplified)
    {
        ValidateText(entries, baseline);
        Directory.CreateDirectory(TextDir);
        string path = OverridesFor(simplified), temporary = path + ".tmp";
        if (File.Exists(path)) File.Copy(path, path + ".previous", true);
        StringTable.ExportCsv(entries, temporary);
        File.Move(temporary, path, true);
    }
    public static void ExportText(string game, Action<string> report) => Export(game, false, report);
    public static void ExportSimplifiedText(string game, Action<string> report) => Export(game, true, report);
    static void Export(string game, bool simplified, Action<string> report)
    {
        var source = Prepare(game, simplified, true, report);
        Directory.CreateDirectory(TextDir);
        var all = source.Strings.Concat(XmlTexts(source.Xml).Select(x => new StringEntry { Id = "xml:" + x.Key, Value = x.Value.Value })).ToList();
        if (File.Exists(OverridesFor(simplified)))
        {
            var changes = StringTable.ImportCsv(OverridesFor(simplified)); ValidateText(changes, all);
            var byId = all.ToDictionary(x => x.Id, StringComparer.Ordinal);
            foreach (var change in changes) byId[change.Id].Value = change.Value;
        }
        StringTable.ExportCsv(all, ExportedCsv);
        report($"已导出 {all.Count} 条文本（含 xml: 附加字符串）：{ExportedCsv}");
    }
    sealed record Source(RmdpArchive Archive, string Rmdp, string Slot, List<StringEntry> Strings, XDocument? Xml);
    static Source Prepare(string game, bool simplified, bool mergeCht, Action<string> report)
    {
        string slot = simplified ? "en" : "cht";
        string error = Validate(game, slot); if (error.Length > 0) throw new InvalidDataException(error);
        bool reference = simplified && ValidateIntergraBase().Length == 0;
        string bin = reference ? Path.Combine(RefData, EnBin) : BaseFile(game, $@"data\ep999-000-{slot}.bin");
        string rmdp = reference ? Path.Combine(RefData, EnRmdp) : BaseFile(game, $@"data\ep999-000-{slot}.rmdp");
        var arc = RmdpArchive.Load(bin);
        var strings = ReadStrings(arc, rmdp, slot);
        string xmlPath = reference ? Path.Combine(RefData, "config", EnAdditionalXml) : BaseFile(game, $@"data\config\{slot}_additional_strings.xml");
        if (reference && !File.Exists(xmlPath)) xmlPath = Path.Combine(RefData, EnAdditionalXml);
        var xml = Xml(xmlPath);
        if (reference)
        {
            report("文本基底：intergra 简体译文（不再自动改写）。来源：https://github.com/intergra/AlanWakeRemastered_Simplified_Chinese");
        }
        else if (simplified && mergeCht)
        {
            var chtArc = RmdpArchive.Load(BaseFile(game, @"data\ep999-000-cht.bin"));
            var cht = ReadStrings(chtArc, BaseFile(game, @"data\ep999-000-cht.rmdp"), "cht").ToDictionary(s => s.Id);
            var conv = ChineseConverter.LoadBuiltin(Path.Combine(AppContext.BaseDirectory, "Data"), true, null);
            foreach (var s in strings) if (cht.TryGetValue(s.Id, out var value)) s.Value = conv.Convert(value.Value);
            var chtXml = XmlTexts(Xml(BaseFile(game, @"data\config\cht_additional_strings.xml")));
            foreach (var pair in XmlTexts(xml)) if (chtXml.TryGetValue(pair.Key, out var value)) pair.Value.Value = conv.Convert(value.Value);
            report("未找到参考 MOD：使用官方繁体文本 + OpenCC 转换，可导入人工译文覆盖。");
        }
        return new(arc, rmdp, slot, strings, xml);
    }
    public static void Install(string game, string? font, bool simplified, Action<string> report) => InstallEn(game, font, simplified, report);
    public static void InstallIntergraBase(string game, Action<string> report)
    { if (ValidateIntergraBase() is { Length: > 0 } error) throw new InvalidDataException(error); InstallEn(game, null, true, report); }
    public static void InstallEn(string game, string? fontPath, bool simplified, Action<string> report, bool mergeCht = true, bool highQuality = true)
    {
        CheckIdle(game);
        if (!string.IsNullOrWhiteSpace(fontPath) && !File.Exists(fontPath)) throw new FileNotFoundException("字体文件不存在。", fontPath);
        var source = Prepare(game, simplified, mergeCht, report);
        var map = source.Strings.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var xmlTexts = XmlTexts(source.Xml);
        string overridesPath = OverridesFor(simplified);
        if (File.Exists(overridesPath))
        {
            var overrides = StringTable.ImportCsv(overridesPath);
            ValidateText(overrides, source.Strings.Concat(xmlTexts.Select(x => new StringEntry { Id = "xml:" + x.Key, Value = x.Value.Value })).ToList());
            var unknown = overrides.Where(o => o.Id.StartsWith("xml:") ? !xmlTexts.ContainsKey(o.Id[4..]) : !map.ContainsKey(o.Id)).ToArray();
            if (unknown.Length > 0) throw new InvalidDataException("翻译 ID 不存在：" + string.Join(", ", unknown.Take(10).Select(o => o.Id)));
            foreach (var entry in overrides)
                if (entry.Id.StartsWith("xml:")) xmlTexts[entry.Id[4..]].Value = entry.Value;
                else map[entry.Id].Value = entry.Value;
            report($"应用导入文本 {overrides.Count} 条。");
        }
        var chars = new HashSet<int>(Enumerable.Range(32,95));
        foreach (var text in source.Strings.Select(s => s.Value).Concat(xmlTexts.Values.Select(x => x.Value)))
            foreach (var rune in text.EnumerateRunes()) if (!Rune.IsControl(rune)) chars.Add(rune.Value);
        // Other resources (including video subtitles) are retained from the base archive.
        var fontEntries = source.Archive.Files.Where(f => f.Path.EndsWith(".binfnt", StringComparison.OrdinalIgnoreCase)).ToList();
        report($"文本 {source.Strings.Count} 条，必需字符 {chars.Count} 个，字体角色 {fontEntries.Count} 个。");
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [$@"locale\{source.Slot}\string_table.bin"] = StringTable.Build(source.Strings) };
        if (!string.IsNullOrWhiteSpace(fontPath))
        {
            using var font = TrueTypeFont.Load(fontPath);
            using var fallbackFont = TrueTypeFont.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc"));
            var missing = chars.Where(c => !font.HasGlyph(c) && !fallbackFont.HasGlyph(c)).ToList();
            int fallbackCount = chars.Count(c => !font.HasGlyph(c) && fallbackFont.HasGlyph(c));
            if (fallbackCount > 0) report($"所选字体缺少 {fallbackCount} 个字符，使用微软雅黑补字（保留正确文字）。");
            // Keep optional characters supported by the selected font as well.
            foreach (var entry in fontEntries)
            {
                byte[] original = source.Archive.ReadFileData(source.Rmdp, entry);
                var fallback = new BinfntFile(original);
                foreach (int c in Enumerable.Range(32, 65504)) if (fallback.Glyph(c) != 0 && font.HasGlyph(c) && !(c >= 0xE000 && c <= 0xF8FF)) chars.Add(c);
            }
            if (missing.Count > 0) throw new InvalidDataException($"所选字体缺少 {missing.Count} 个必需字符，请更换完整字体：" + string.Join(" ", missing.Take(35).Select(c => $"U+{c:X4} {char.ConvertFromUtf32(c)}")));
            byte[] built = BinfntBuilder.Build(font, chars, emPixels: highQuality ? 64 : 48, atlasWidth: highQuality ? 4096 : 2048, fallback: fallbackFont, report: report);
            var parsed = new BinfntFile(built); parsed.RequireCoverage(chars);
            Directory.CreateDirectory(Path.Combine(WorkspaceDir, "preview"));
            parsed.Preview(simplified ? "继续游戏 新游戏 章节 选项 附加内容 退出\n亮瀑镇 简体字体测试 ABC abc 0123456789" : "繼續遊戲 新遊戲 章節 選項 附加內容 退出\n亮瀑鎮 繁體字體測試 ABC abc 0123456789", Path.Combine(WorkspaceDir, "preview", "font.png"));
            foreach (var entry in fontEntries) replacements[entry.Path] = built;
            report($"字体已重建并校验：{parsed.GlyphCount} 字形，{parsed.Width}×{parsed.Height} 图集。");
        }
        else
        {
            foreach (var entry in fontEntries) new BinfntFile(source.Archive.ReadFileData(source.Rmdp, entry)).RequireCoverage(chars);
            report("保留基底字体，字符覆盖校验通过。");
        }
        string stage = Path.Combine(WorkspaceDir, "staging", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        string binName = $"ep999-000-{source.Slot}.bin", rmdpName = $"ep999-000-{source.Slot}.rmdp";
        string bin = Path.Combine(stage, binName), rmdp = Path.Combine(stage, rmdpName);
        source.Archive.Repack(source.Rmdp, replacements, bin, rmdp);
        var check = RmdpArchive.Load(bin);
        foreach (var entry in check.Files)
        {
            var bytes = check.ReadFileData(rmdp, entry);
            if (Crc32.Compute(bytes) != entry.DataCrc) throw new InvalidDataException("资源 CRC 校验失败：" + entry.Path);
            if (replacements.TryGetValue(entry.Path, out var expected) && !bytes.SequenceEqual(expected)) throw new InvalidDataException("重打包内容不一致：" + entry.Path);
        }
        var files = new Dictionary<string, string> { [@"data\" + binName] = bin, [@"data\" + rmdpName] = rmdp };
        if (source.Xml != null)
        {
            string xml = Path.Combine(stage, source.Slot + "_additional_strings.xml"); source.Xml.Save(xml);
            files[@"data\config\" + Path.GetFileName(xml)] = xml;
        }
        SaveBackup(game, files.Keys);
        Commit(game, files, stage);
        if (Path.GetDirectoryName(Path.GetFullPath(stage)) != Path.GetFullPath(Path.Combine(WorkspaceDir, "staging")))
            throw new InvalidOperationException("Unexpected staging path.");
        Directory.Delete(stage, true); // verified private staging directory, after successful commit
        report($"安装完成。请选择 {(simplified ? "English（简体补丁）" : "繁體中文")}。备份：{Backup(game)}");
    }
    static void Commit(string game, Dictionary<string,string> files, string stage)
    {
        var completed = new List<string>(); var absent = new HashSet<string>();
        try
        {
            foreach (var pair in files)
            {
                string dst = Path.Combine(game, pair.Key), rollback = Path.Combine(stage, "rollback", pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                if (File.Exists(dst)) File.Copy(dst, rollback); else absent.Add(pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                completed.Add(pair.Key);
                File.Copy(pair.Value, dst, true);
            }
        }
        catch
        {
            foreach (string rel in completed.AsEnumerable().Reverse())
                if (absent.Contains(rel)) File.Delete(Path.Combine(game, rel));
                else File.Copy(Path.Combine(stage, "rollback", rel), Path.Combine(game, rel), true);
            throw;
        }
    }
    public static void Restore(string game, Action<string> report) => RestoreEn(game, report);
    public static void RestoreEn(string game, Action<string> report)
    {
        CheckIdle(game); string backup = Backup(game);
        if (!Directory.Exists(backup)) throw new InvalidOperationException("此游戏目录没有 v3 备份。旧实验备份不会自动使用。");
        var files = Directory.GetFiles(Path.Combine(backup, "data"), "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(backup,p), p => p);
        string stage = Path.Combine(WorkspaceDir,"staging",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        Commit(game, files, stage); report("已还原此游戏目录的全部已备份语言资源。");
    }
}
