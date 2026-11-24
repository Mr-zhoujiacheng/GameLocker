using System.Security.Cryptography;
using System.Text.Json;

namespace GameLocker.Services;

internal static class OptiCaptureMenuLocalization
{
    private const string SupportedRuntime = "v2.10.80";
    private const string ScalerCommit = "956bb7198e819da857710f7f9ace223790a7ba55";
    private const string OfficialHash = "8570433BA0A5293298EBF43EEE494E64DAA283E9A2FD5A20B789CE6CFB27520D";
    private const string BackupName = "dxgi.GameLocker-original.dll";
    private const string MenuDll = "dxgi.dll";
    private const string LocalizedDll = "dxgi.zh-CN.dll";
    private static readonly string BundleDirectory = Path.Combine(AppContext.BaseDirectory, "Resources", "OptiScalerZhCN");

    internal static string Normalize(string? value) => value == "en" ? "en" : "zh-CN";

    internal static bool IsAvailable(string? runtime)
    {
        if (runtime is null) return false;
        try
        {
            var bundleHash = ReadBundleHash(runtime);
            if (bundleHash is null || !MatchesHash(Path.Combine(BundleDirectory, LocalizedDll), bundleHash)) return false;
            var activeHash = ReadHash(Path.Combine(runtime, MenuDll));
            var backup = Path.Combine(runtime, BackupName);
            var originalAvailable = MatchesHash(backup, OfficialHash);
            return (activeHash == OfficialHash && (!File.Exists(backup) || originalAvailable)) ||
                (activeHash == bundleHash && originalAvailable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static void Apply(string runtime, string? language)
    {
        var localized = Normalize(language) == "zh-CN";
        var expectedLocalized = ReadBundleHash(runtime);
        if (expectedLocalized is null) return;
        var current = Path.Combine(runtime, MenuDll);
        var original = Path.Combine(runtime, BackupName);
        var activeHash = ReadHash(current);
        if (localized)
        {
            if (activeHash != OfficialHash) return; // Do not replace third-party or already localized binaries.
            var source = Path.Combine(BundleDirectory, LocalizedDll);
            if (!MatchesHash(source, expectedLocalized)) return;
            if (File.Exists(original) && !MatchesHash(original, OfficialHash)) return;
            Replace(current, source, expectedLocalized, File.Exists(original) ? null : original);
        }
        else if (activeHash == expectedLocalized && MatchesHash(original, OfficialHash))
        {
            Replace(current, original, OfficialHash, null);
        }
    }

    private static string? ReadBundleHash(string runtime)
    {
        var name = Path.GetFileName(runtime);
        if (name != SupportedRuntime && !name.StartsWith(SupportedRuntime + "-", StringComparison.Ordinal)) return null;
        var manifest = Path.Combine(BundleDirectory, "OptiScaler.zh-CN.json");
        if (!File.Exists(manifest)) return null;
        using var file = File.OpenRead(manifest);
        if (file.Length > 4096) return null;
        using var data = JsonDocument.Parse(file);
        var root = data.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
            ReadString(root, "runtimeVersion") != SupportedRuntime || ReadString(root, "sourceCommit") != ScalerCommit ||
            !string.Equals(ReadString(root, "officialDxgiSha256"), OfficialHash, StringComparison.OrdinalIgnoreCase)) return null;
        var hash = ReadString(root, "localizedDxgiSha256");
        return hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash.ToUpperInvariant() : null;
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static string ReadHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool MatchesHash(string file, string hash) => File.Exists(file) && ReadHash(file) == hash;

    private static void Replace(string current, string source, string hash, string? backup)
    {
        var temp = current + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temp);
            if (!MatchesHash(temp, hash)) throw new InvalidDataException("OptiScaler menu binary checksum mismatch.");
            File.Replace(temp, current, backup);
        }
        finally { File.Delete(temp); }
    }
}
