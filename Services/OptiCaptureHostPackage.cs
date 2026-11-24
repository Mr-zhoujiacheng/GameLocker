using SharpCompress.Archives;
using SharpCompress.Common;

namespace GameLocker.Services;

internal static class OptiCaptureHostPackage
{
    private const long MaxExpandedBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxEntries = 4096;
    private const int MaxIniBytes = 2 * 1024 * 1024;

    internal static bool HasFullRuntime(string directory)
    {
        try { ValidateFullRuntime(directory); return true; }
        catch (FileNotFoundException) { return false; }
    }

    private static void ValidateFullRuntime(string directory)
    {
        OptiCaptureHostService.ValidateRuntime(directory);
        new OptiCaptureProfile("fsrfg", "fsr3").ValidateDependencies(directory);
        new OptiCaptureProfile("dlssg", "dlss").ValidateDependencies(directory);
        new OptiCaptureProfile("xefg", "xess").ValidateDependencies(directory);
        var license = Path.Combine(directory, "Licenses", "XeSS_LICENSE.txt");
        if (!File.Exists(license)) throw new FileNotFoundException("The OptiCaptureHost package is missing the XeSS license.", license);
    }

    internal static string Prepare(string archive, string unpack, string? previous, Version version, CancellationToken token)
    {
        Directory.CreateDirectory(unpack);
        using (var package = ArchiveFactory.Open(archive))
        {
            var expected = Path.GetExtension(archive) == ".rar" ? ArchiveType.Rar : ArchiveType.Zip;
            if (package.Type != expected || !package.IsComplete || package.Volumes.Count() != 1)
                throw new InvalidDataException("The OptiCaptureHost full pack is not a complete single archive.");
            using var entries = package.ExtractAllEntries();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[65536];
            long declaredBytes = 0;
            while (entries.MoveToNextEntry())
            {
                token.ThrowIfCancellationRequested();
                var entry = entries.Entry;
                var name = SafeEntryName(entry.Key, entry.IsDirectory);
                if (seen.Count >= MaxEntries || !seen.Add(name) || entry.IsEncrypted || entry.IsSplitAfter ||
                    entry.LinkTarget is not null || entry.Size < 0 || entry.Size > MaxExpandedBytes - declaredBytes)
                    throw new InvalidDataException("The OptiCaptureHost package contains an invalid archive entry.");
                declaredBytes += entry.Size;
                var path = Path.Combine(unpack, name);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = entries.OpenEntryStream();
                using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var read = source.Read(buffer);
                    if (read == 0) break;
                    written += read;
                    if (written > entry.Size) throw new InvalidDataException("An OptiCaptureHost package entry exceeds its declared size.");
                    destination.Write(buffer, 0, read);
                }
                if (written != entry.Size) throw new InvalidDataException("An OptiCaptureHost package entry is truncated.");
            }
        }

        var expectedWrapper = Path.Combine(unpack, $"OptiCaptureHost-{version.ToString(3)}_Full Pack");
        var runtime = File.Exists(Path.Combine(unpack, "OptiCaptureHost.exe")) ? unpack : expectedWrapper;
        ValidateFullRuntime(runtime);
        foreach (var stale in new[] { OptiCaptureHostStatus.FileName, "OptiCaptureHost.status.tmp", "OptiCaptureHost.log" })
            File.Delete(Path.Combine(runtime, stale));

        // Preserve user INI keys alongside new upstream defaults, then apply GameLocker's direct-launch settings.
        UpdateIni(runtime, previous, "OptiCaptureHost.ini",
            [("Limiter", "Target", "0"), ("Limiter", "OptiFps", "0"), ("Limiter", "Auto", "0")]);
        UpdateIni(runtime, previous, "OptiScaler.ini", [
            ("FrameGen", "External", "false"), ("Plugins", "LoadAsiPlugins", "false"),
            ("MFGUnlock", "XeFG", "false"), ("MFGUnlock", "RTX40", "false"), ("MFGUnlock", "RTX2030", "false")]);
        return runtime;
    }

    private static string SafeEntryName(string? name, bool directory)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512)
            throw new InvalidDataException("The OptiCaptureHost package contains an invalid path.");
        name = name.Replace('\\', '/');
        if (directory && name.EndsWith('/')) name = name[..^1];
        var segments = name.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(c => c < ' ' || c is '<' or '>' or ':' or '"' or '|' or '?' or '*') ||
                IsWindowsDeviceName(segment.Split('.')[0].TrimEnd(' ')))
                throw new InvalidDataException("The OptiCaptureHost package contains an unsafe path.");
        }
        return Path.Combine(segments);
    }

    private static bool IsWindowsDeviceName(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) ||
                upper.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsAsciiDigit(upper[3]) ||
                upper[3] is '\u00B9' or '\u00B2' or '\u00B3'));
    }

    private static void UpdateIni(string runtime, string? previous, string file,
        (string Section, string Key, string Value)[] required)
    {
        var path = Path.Combine(runtime, file);
        var original = ReadIni(path);
        var lines = SplitLines(original).ToList();
        var edits = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        void Set(string section, string key, string value)
        {
            if (!edits.TryGetValue(section, out var keys)) edits[section] = keys = new(StringComparer.OrdinalIgnoreCase);
            keys[key] = value;
        }
        if (previous is not null && File.Exists(Path.Combine(previous, file)))
        {
            var previousSection = "";
            foreach (var line in SplitLines(ReadIni(Path.Combine(previous, file))))
            {
                if (ReadSection(line) is { } heading) previousSection = heading;
                else if (previousSection.Length != 0 && ReadKey(line) is { } key)
                    Set(previousSection, key, line[(line.IndexOf('=') + 1)..].Trim());
            }
        }
        foreach (var (section, key, value) in required) Set(section, key, value);

        var seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>(lines.Count);
        Dictionary<string, string>? current = null;
        void AddMissing()
        {
            if (current is not null)
                foreach (var (key, value) in current)
                    if (seenKeys.Add(key)) output.Add($"{key} = {value}");
            seenKeys.Clear();
        }
        foreach (var line in lines)
        {
            if (ReadSection(line) is { } section)
            {
                AddMissing();
                current = edits.GetValueOrDefault(section);
                seenSections.Add(section);
            }
            if (current is not null && ReadKey(line) is { } key && current.TryGetValue(key, out var value))
            {
                seenKeys.Add(key);
                var equals = line.IndexOf('=');
                output.Add(line[(equals + 1)..].Trim() == value ? line : line[..(equals + 1)] + " " + value);
            }
            else output.Add(line);
        }
        AddMissing();
        foreach (var (section, keys) in edits)
        {
            if (seenSections.Contains(section)) continue;
            output.Add("");
            output.Add($"[{section}]");
            current = keys;
            AddMissing();
        }
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.WriteAllText(path, string.Join(newline, output));
    }

    private static string ReadIni(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > MaxIniBytes) throw new InvalidDataException("An OptiCaptureHost INI is too large to migrate.");
        using var text = new StreamReader(input);
        return text.ReadToEnd();
    }

    private static string[] SplitLines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string? ReadSection(string line)
    {
        var trimmed = line.Trim();
        var end = trimmed.IndexOf(']');
        return trimmed.StartsWith('[') && end > 1 ? trimmed[1..end].Trim() : null;
    }

    private static string? ReadKey(string line)
    {
        var trimmed = line.TrimStart();
        var equals = trimmed.IndexOf('=');
        return equals > 0 && trimmed[0] is not (';' or '#' or '[') ? trimmed[..equals].TrimEnd() : null;
    }
}
