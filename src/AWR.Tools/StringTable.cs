using System.Text;

namespace AWR.Tools;

public sealed class StringEntry
{
    public string Id = "";
    public string Value = "";
}

public static class StringTable
{
    public static List<StringEntry> Parse(byte[] data)
    {
        var list = new List<StringEntry>();
        int pos = 0;
        uint count = BitConverter.ToUInt32(data, pos); pos += 4;
        for (int i = 0; i < count; i++)
        {
            int len = (int)BitConverter.ToUInt32(data, pos); pos += 4;
            string id = Encoding.ASCII.GetString(data, pos, len); pos += len;
            int wlen = (int)BitConverter.ToUInt32(data, pos); pos += 4;
            string value = Encoding.Unicode.GetString(data, pos, wlen * 2); pos += wlen * 2;
            list.Add(new StringEntry { Id = id, Value = value });
        }
        return list;
    }

    public static byte[] Build(List<StringEntry> entries)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
        {
            w.Write((uint)entries.Count);
            foreach (var e in entries)
            {
                byte[] id = Encoding.ASCII.GetBytes(e.Id);
                w.Write((uint)id.Length);
                w.Write(id);
                byte[] val = Encoding.Unicode.GetBytes(e.Value);
                w.Write((uint)(val.Length / 2));
                w.Write(val);
            }
        }
        return ms.ToArray();
    }

    public static void ExportCsv(List<StringEntry> entries, string path)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine("id\ttext");
        foreach (var e in entries)
            w.WriteLine($"{Escape(e.Id)}\t{Escape(e.Value)}");
    }

    public static List<StringEntry> ImportCsv(string path)
    {
        var list = new List<StringEntry>();
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0 || lines[0].TrimStart('\uFEFF') != "id\ttext")
            throw new InvalidDataException("文本必须为 UTF-8 TSV，首行为 id<TAB>text。请先导出模板。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            int tab = lines[i].IndexOf('\t');
            if (tab <= 0 || lines[i].IndexOf('\t', tab+1) >= 0) throw new InvalidDataException($"第 {i+1} 行必须为 id 与 text 两列。");
            string id = Unescape(lines[i][..tab]);
            if (!seen.Add(id)) throw new InvalidDataException($"重复 ID：{id}");
            list.Add(new StringEntry { Id = id, Value = Unescape(lines[i][(tab+1)..]) });
        }
        return list;
    }

    private static int IndexOfUnescapedTab(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '\t') return i;
        }
        return -1;
    }

    private static string Escape(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\t", "\\t")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n");

    private static string Unescape(string s)
    {
        var result = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { result.Append(s[i]); continue; }
            if (++i == s.Length) throw new InvalidDataException("文本结尾包含未转义的反斜杠。");
            result.Append(s[i] switch { 't' => '\t', 'r' => '\r', 'n' => '\n', '\\' => '\\', _ => throw new InvalidDataException($"未知转义字符：{s[i]}") });
        }
        return result.ToString();
    }
}
