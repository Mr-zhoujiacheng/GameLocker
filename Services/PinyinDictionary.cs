namespace GameLocker.Services;

/// <summary>
/// 屏幕键盘的内置拼音字典：音节 → 按常用度排序的候选字，
/// 以及双字词表（拼音 → 按词频排序的词语，见 Resources/words.tsv）。
/// 不依赖系统输入法，选中的字再用 KEYEVENTF_UNICODE 注入前台应用。
/// </summary>
internal static class PinyinDictionary
{
    private static Dictionary<string, string>? _map;
    private static Dictionary<string, string[]>? _words;
    private static Dictionary<string, string[]>? _words3;
    private static Dictionary<string, string[]>? _abbr;
    private static Dictionary<string, List<string>>? _nine;   // 九键数字串 → 可能的音节
    private static string[]? _wordKeys;
    private static string[]? _word3Keys;
    private static readonly object Gate = new();

    /// <summary>
    /// 按拼音串取候选：整串命中优先（单音节）；否则把整串切成音节
    /// （如 nihao → ni|hao、zhongguo → zhong|guo）。
    /// 两三个音节时先用真实词语（按词频排序）作为候选，再补常用字组合；
    /// 更多音节用各音节常用字组合，避免没有候选。
    /// 整串切不出音节时再按简拼查（只打声母，如 nh → 你好、nm → 你们）。
    /// </summary>
    public static IReadOnlyList<string> Query(string buffer, int max = 6)
    {
        if (string.IsNullOrEmpty(buffer)) return Array.Empty<string>();
        var map = Load();

        if (map.TryGetValue(buffer, out var exact))
        {
            var single = new List<string>(max);
            foreach (var ch in exact)
            {
                single.Add(ch.ToString());
                if (single.Count >= max) break;
            }
            return single;
        }

        // 简拼（nh、nm、bj…）要先于音节切分：否则 n/m 这类特殊音节会把简拼拆成杂字
        if (buffer.Length is >= 2 and <= 3 && LoadAbbr().ContainsKey(buffer))
            return LookupAbbr(buffer, max);

        var syllables = Segment(buffer);
        if (syllables is null || syllables.Count < 2)
        {
            var abbr = LookupAbbr(buffer, max);
            return abbr.Count > 0 ? abbr : LookupPrefix(buffer, max);
        }

        var result = new List<string>(max);

        // 双字/三字词：先放词典里的真实词语（已按词频降序）
        var table = syllables.Count switch
        {
            2 => LoadWords(),
            3 => LoadWords3(),
            _ => null
        };
        if (table is not null && table.TryGetValue(buffer, out var words))
        {
            foreach (var word in words)
            {
                if (result.Count >= max) break;
                if (!result.Contains(word)) result.Add(word);
            }
        }

        if (result.Count >= max) return result;

        // 还没候选时按完整拼音前缀找词：nih → 你好、weish → 为什么
        if (result.Count == 0)
        {
            var prefix = LookupPrefix(buffer, max);
            if (prefix.Count > 0) return prefix;
        }

        // 每个音节只取最常用的前 2 个字，组合数可控（2^n）
        var per = new string[syllables.Count];
        for (var i = 0; i < per.Length; i++)
        {
            var chars = map[syllables[i]];
            per[i] = chars.Length > 2 ? chars[..2] : chars;
        }

        var combo = new char[per.Length];
        Walk(0);
        return result;

        void Walk(int index)
        {
            if (result.Count >= max) return;
            if (index == per.Length)
            {
                var text = new string(combo);
                if (!result.Contains(text)) result.Add(text);
                return;
            }
            foreach (var ch in per[index])
            {
                combo[index] = ch;
                Walk(index + 1);
                if (result.Count >= max) return;
            }
        }
    }

    /// <summary>
    /// 九键拼音：把 2–9 的数字串切成合法音节（6464 → ming，64426 → ni|hao），
    /// 再复用拼音词表出候选。数字与字母的对应和手机键盘一致。
    /// 同一串数字常能切出多种拼音（64426 既是 nihao 也是 migan），
    /// 先按简拼表的常用度给各条路径排序，再轮流取候选，避免冷门组合把候选位占满。
    /// </summary>
    public static IReadOnlyList<string> QueryDigits(string digits, int max = 6)
    {
        if (string.IsNullOrEmpty(digits)) return Array.Empty<string>();
        var table = LoadNine();

        var paths = new List<(string Pinyin, List<string> Words)>();
        var path = new List<string>(8);
        var visited = 0;
        Walk(0);

        if (paths.Count == 0) return Array.Empty<string>();

        var ranked = paths
            .Select((p, index) => new
            {
                p.Words,
                Index = index,
                Rank = p.Words.Count == 0 ? int.MaxValue : p.Words.Min(w => AbbrRank(p.Pinyin, w))
            })
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Index)
            .ToList();

        var result = new List<string>(max);
        for (var slot = 0; slot < max && result.Count < max; slot++)
        {
            foreach (var item in ranked)
            {
                if (slot >= item.Words.Count) continue;
                if (!result.Contains(item.Words[slot])) result.Add(item.Words[slot]);
                if (result.Count >= max) break;
            }
        }
        return result;

        void Walk(int pos)
        {
            // 兜底防止极端数字串把组合数撑爆
            if (paths.Count >= 48 || ++visited > 3000) return;
            if (pos == digits.Length)
            {
                var pinyin = string.Concat(path);
                var words = Query(pinyin, max).ToList();
                if (words.Count > 0) paths.Add((pinyin, words));
                return;
            }

            // 先试长音节（ming 这类四码），再退到短音节
            for (var len = Math.Min(6, digits.Length - pos); len >= 1; len--)
            {
                if (!table.TryGetValue(digits.Substring(pos, len), out var syllables)) continue;
                foreach (var syllable in syllables)
                {
                    path.Add(syllable);
                    Walk(pos + len);
                    path.RemoveAt(path.Count - 1);
                    if (paths.Count >= 48) return;
                }
            }
        }
    }

    /// <summary>音节 → 数字串反查表（九键用），按 pinyin.tsv 里音节的出现顺序排</summary>
    private static Dictionary<string, List<string>> LoadNine()
    {
        if (_nine is not null) return _nine;
        lock (Gate)
        {
            if (_nine is not null) return _nine;

            var table = new Dictionary<string, List<string>>(2048, StringComparer.Ordinal);
            foreach (var syllable in Load().Keys)
            {
                var code = string.Concat(syllable.Select(DigitOf));
                if (!table.TryGetValue(code, out var list)) table[code] = list = [];
                list.Add(syllable);
            }
            _nine = table;
            return table;
        }
    }

    /// <summary>字母 → 手机九键的数字（abc=2 … wxyz=9）</summary>
    private static char DigitOf(char c) => c switch
    {
        'a' or 'b' or 'c' => '2',
        'd' or 'e' or 'f' => '3',
        'g' or 'h' or 'i' => '4',
        'j' or 'k' or 'l' => '5',
        'm' or 'n' or 'o' => '6',
        'p' or 'q' or 'r' or 's' => '7',
        't' or 'u' or 'v' => '8',
        _ => '9'
    };

    /// <summary>把拼音串切成字典里都存在的音节；无法完整切分时返回 null</summary>
    private static List<string>? Segment(string buffer)
    {
        var map = Load();
        var memo = new Dictionary<int, List<string>?>();
        return Split(0);

        List<string>? Split(int start)
        {
            if (start == buffer.Length) return [];
            if (memo.TryGetValue(start, out var cached)) return cached;
            memo[start] = null;
            var limit = Math.Min(7, buffer.Length - start);
            for (var len = limit; len >= 1; len--)
            {
                var syllable = buffer.Substring(start, len);
                if (!map.ContainsKey(syllable)) continue;
                var rest = Split(start + len);
                if (rest is null) continue;
                var list = new List<string>(rest.Count + 1) { syllable };
                list.AddRange(rest);
                memo[start] = list;
                return list;
            }
            return null;
        }
    }

    /// <summary>简拼查询：只打每个音节的声母，如 nh → 你好、nm → 你们</summary>
    private static IReadOnlyList<string> LookupAbbr(string buffer, int max)
    {
        if (!LoadAbbr().TryGetValue(buffer, out var words)) return Array.Empty<string>();
        var result = new List<string>(Math.Min(max, words.Length));
        foreach (var word in words)
        {
            result.Add(word);
            if (result.Count >= max) break;
        }
        return result;
    }

    /// <summary>
    /// 按完整拼音前缀找词：输入还没打完时也能联想，
    /// 如 nih / niha → 你好，weish → 为什么。太短的前缀（一两个字母）不查，避免刷出一堆无关词。
    /// </summary>
    private static IReadOnlyList<string> LookupPrefix(string buffer, int max)
    {
        if (buffer.Length < 3) return Array.Empty<string>();
        var result = new List<string>(max);
        Scan(LoadWords(), GetSortedKeys(LoadWords(), ref _wordKeys));
        if (result.Count < max)
            Scan(LoadWords3(), GetSortedKeys(LoadWords3(), ref _word3Keys));
        return result;

        void Scan(Dictionary<string, string[]> table, string[] keys)
        {
            var index = Array.BinarySearch(keys, buffer, StringComparer.Ordinal);
            if (index < 0) index = ~index;

            // 前缀可能命中很多词，最多扫 200 个键，防止卡顿
            var hits = new List<(int Rank, int KeyLen, int Order, string Word)>();
            for (var i = index; i < keys.Length && i - index < 200; i++)
            {
                if (!keys[i].StartsWith(buffer, StringComparison.Ordinal)) break;
                var words = table[keys[i]];
                for (var j = 0; j < words.Length; j++)
                    hits.Add((AbbrRank(keys[i], words[j]), keys[i].Length, j, words[j]));
            }

            // 常用词优先：用简拼表里该词的位置当排名（如 nih 时你好排第一）
            foreach (var hit in hits.OrderBy(h => h.Rank).ThenBy(h => h.KeyLen).ThenBy(h => h.Order))
            {
                if (result.Count >= max) return;
                if (!result.Contains(hit.Word)) result.Add(hit.Word);
            }
        }
    }

    /// <summary>词语在简拼候选里的位次，越靠前越常用；查不到返回最大值</summary>
    private static int AbbrRank(string fullKey, string word)
    {
        var syllables = Segment(fullKey);
        if (syllables is null || syllables.Count < 2) return int.MaxValue;
        var initials = string.Concat(syllables.Select(x => x[0]));
        if (!LoadAbbr().TryGetValue(initials, out var ranked)) return int.MaxValue;
        var index = Array.IndexOf(ranked, word);
        return index >= 0 ? index : int.MaxValue;
    }

    private static string[] GetSortedKeys(Dictionary<string, string[]> table, ref string[]? cache)
    {
        if (cache is not null) return cache;
        lock (Gate)
        {
            if (cache is null)
            {
                var keys = table.Keys.ToArray();
                Array.Sort(keys, StringComparer.Ordinal);
                cache = keys;
            }
            return cache;
        }
    }

    private static Dictionary<string, string[]> LoadWords() => LoadTable("words.tsv", ref _words);

    private static Dictionary<string, string[]> LoadWords3() => LoadTable("words3.tsv", ref _words3);

    private static Dictionary<string, string[]> LoadAbbr() => LoadTable("abbr.tsv", ref _abbr);

    /// <summary>读取「键 TAB 词语1 词语2…」格式的内嵌词表</summary>
    private static Dictionary<string, string[]> LoadTable(string fileName,
        ref Dictionary<string, string[]>? cache)
    {
        if (cache is not null) return cache;
        lock (Gate)
        {
            if (cache is not null) return cache;

            var table = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var asm = typeof(PinyinDictionary).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
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

            cache = table;
            return table;
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (_map is not null) return _map;
        lock (Gate)
        {
            if (_map is not null) return _map;

            var map = new Dictionary<string, string>(512, StringComparer.OrdinalIgnoreCase);
            var asm = typeof(PinyinDictionary).Assembly;
            var name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("pinyin.tsv", StringComparison.OrdinalIgnoreCase));
            if (name is not null)
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0 || tab == line.Length - 1) continue;
                    map[line[..tab]] = line[(tab + 1)..];
                }
            }

            _map = map;
            return map;
        }
    }
}
