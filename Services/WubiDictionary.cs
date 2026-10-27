namespace GameLocker.Services;

/// <summary>
/// 五笔（86 版）字典：编码 → 按字频排序的候选字词（Resources/wubi.tsv）。
/// 键盘按字母拼五笔码，选中的字词再用 KEYEVENTF_UNICODE 注入前台应用。
/// </summary>
internal static class WubiDictionary
{
    private static Dictionary<string, string[]>? _table;
    private static string[]? _keys;
    private static readonly object Gate = new();

    /// <summary>按五笔码取候选：整码命中优先，不足时再按前缀补（打 1~3 码也能出字）</summary>
    public static IReadOnlyList<string> Query(string code, int max = 6)
    {
        if (string.IsNullOrEmpty(code)) return Array.Empty<string>();
        var table = Load();

        var result = new List<string>(max);
        if (table.TryGetValue(code, out var exact))
        {
            foreach (var word in exact)
            {
                if (result.Count >= max) return result;
                result.Add(word);
            }
        }

        // 前缀：五笔打前几码就要能出字，最多扫 200 个相邻编码，避免卡顿
        var keys = GetKeys(table);
        var index = Array.BinarySearch(keys, code, StringComparer.Ordinal);
        if (index < 0) index = ~index;
        for (var i = index; i < keys.Length && i - index < 200; i++)
        {
            if (!keys[i].StartsWith(code, StringComparison.Ordinal)) break;
            if (keys[i].Length == code.Length) continue;   // 整码已加过
            foreach (var word in table[keys[i]])
            {
                if (result.Count >= max) return result;
                if (!result.Contains(word)) result.Add(word);
            }
        }
        return result;
    }

    private static string[] GetKeys(Dictionary<string, string[]> table)
    {
        if (_keys is not null) return _keys;
        lock (Gate)
        {
            if (_keys is null)
            {
                var keys = table.Keys.ToArray();
                Array.Sort(keys, StringComparer.Ordinal);
                _keys = keys;
            }
            return _keys;
        }
    }

    private static Dictionary<string, string[]> Load()
    {
        if (_table is not null) return _table;
        lock (Gate)
        {
            if (_table is not null) return _table;

            var table = new Dictionary<string, string[]>(120000, StringComparer.Ordinal);
            var asm = typeof(WubiDictionary).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("wubi.tsv", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    table[line[..tab]] = line[(tab + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
            }

            _table = table;
            return table;
        }
    }
}
