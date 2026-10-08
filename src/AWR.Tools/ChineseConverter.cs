using System.Text;

namespace AWR.Tools;

/// <summary>
/// OpenCC-style chained simplified/traditional converter.
/// Stage 1 (optional, tw2sp): TWPhrasesRev → TWVariantsRevPhrases → TWVariantsRev  (Taiwan wording → Mainland wording)
/// Stage 2: TSPhrases → TSCharacters  (Traditional → Simplified)
/// Within a stage, dictionaries are merged with earlier entries taking priority;
/// segmentation uses maximal forward matching.
/// </summary>
public sealed class ChineseConverter
{
    private sealed class DictStage
    {
        public Dictionary<string, string> Map = new();
        public int MaxKeyLen;
    }

    private readonly List<DictStage> _stages = new();
    private Dictionary<string, string>? _overrides;

    public static ChineseConverter LoadBuiltin(string dataDir, bool taiwanPhrases, string? overridesCsv = null)
    {
        var conv = new ChineseConverter();
        if (taiwanPhrases)
        {
            var s1 = new DictStage();
            AddDict(s1, Path.Combine(dataDir, "TWPhrasesRev.txt"));
            AddDict(s1, Path.Combine(dataDir, "TWVariantsRevPhrases.txt"));
            AddDict(s1, Path.Combine(dataDir, "TWVariantsRev.txt"));
            conv._stages.Add(s1);
        }
        var s2 = new DictStage();
        AddDict(s2, Path.Combine(dataDir, "TSPhrases.txt"));
        AddDict(s2, Path.Combine(dataDir, "TSCharacters.txt"));
        conv._stages.Add(s2);

        if (overridesCsv != null && File.Exists(overridesCsv))
        {
            conv._overrides = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(overridesCsv))
            {
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                conv._overrides[line[..tab]] = line[(tab + 1)..];
            }
        }
        return conv;
    }

    private static void AddDict(DictStage stage, string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith("#")) continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string key = line[..tab];
            string val = line[(tab + 1)..];
            int sp = val.IndexOf(' ');
            if (sp > 0) val = val[..sp];
            if (key.Length == 0 || val.Length == 0) continue;
            if (!stage.Map.ContainsKey(key))
                stage.Map[key] = val;
            if (key.Length > stage.MaxKeyLen) stage.MaxKeyLen = key.Length;
        }
    }

    public string Convert(string input)
    {
        if (_overrides != null && _overrides.Count > 0)
            input = ReplaceAll(input, _overrides);
        string s = input;
        foreach (var stage in _stages)
            s = ApplyStage(s, stage);
        return s;
    }

    private static string ReplaceAll(string text, Dictionary<string, string> map)
    {
        int maxLen = 0;
        foreach (var k in map.Keys) maxLen = Math.Max(maxLen, k.Length);
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            for (int len = Math.Min(maxLen, text.Length - i); len >= 1; len--)
            {
                if (map.TryGetValue(text.Substring(i, len), out var rep))
                {
                    sb.Append(rep);
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string ApplyStage(string text, DictStage stage)
    {
        if (stage.Map.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            int maxLen = Math.Min(stage.MaxKeyLen, text.Length - i);
            for (int len = maxLen; len >= 2; len--)
            {
                if (stage.Map.TryGetValue(text.Substring(i, len), out var rep))
                {
                    sb.Append(rep);
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                // single char: only convert if present as a single-char key (skip phrase-internal chars)
                if (stage.Map.TryGetValue(text.Substring(i, 1), out var rep1))
                    sb.Append(rep1);
                else
                    sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
