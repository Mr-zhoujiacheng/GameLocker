namespace GameLocker.Services;

/// <summary>Settings for the independent capture host, not the in-game OptiScaler widget.</summary>
internal sealed class OptiCaptureProfile(string? frameGeneration, string? upscaler, string? vSync = null,
    string? fpsOverlay = null, string? menuScale = null, string? sharpness = null)
{
    // The host registers Insert globally, then forwards F14 to OptiScaler after focusing its overlay.
    private const string HostMenuHotkey = "0,45";
    private const int ForwardedMenuKey = 0x7D;

    internal string FrameGeneration { get; } = NormalizeFrameGeneration(frameGeneration);
    internal string Upscaler { get; } = NormalizeUpscaler(upscaler);
    internal string VSync { get; } = NormalizeToggle(vSync);
    internal string FpsOverlay { get; } = NormalizeToggle(fpsOverlay);
    internal string MenuScale { get; } = NormalizeMenuScale(menuScale);
    internal string Sharpness { get; } = NormalizeSharpness(sharpness);
    internal bool UsesFrameGeneration => FrameGeneration != "nofg";

    internal static string NormalizeToggle(string? value) => value is "on" or "off" ? value : "inherit";
    internal static string NormalizeMenuScale(string? value) => value is "0.75" or "1.0" or "1.25" or "1.5" or "2.0" ? value : "inherit";
    internal static string NormalizeSharpness(string? value) => value is "off" or "0.3" or "0.6" or "0.9" ? value : "inherit";

    internal static string NormalizeFrameGeneration(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fsrfg" => "fsrfg",
        "dlssg" => "dlssg",
        "nofg" => "nofg",
        _ => "xefg"
    };

    internal static string NormalizeUpscaler(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fsr3" => "fsr3",
        "xess" => "xess",
        "dlss" => "dlss",
        _ => "ffx"
    };

    internal void ValidateDependencies(string directory)
    {
        bool Has(params string[] paths) => paths.Any(path => File.Exists(Path.Combine(directory, path)));
        void Require(string file, bool available)
        {
            if (!available) throw new FileNotFoundException($"The selected OptiScaler mode requires {file}.", file);
        }

        var combinedFfx = Has("amd_fidelityfx_dx12.dll", "Optiscaler/amd_fidelityfx_dx12.dll");
        var ffxLoader = Has("amd_fidelityfx_loader_dx12.dll", "Optiscaler/amd_fidelityfx_loader_dx12.dll");
        if (FrameGeneration == "fsrfg")
            Require(combinedFfx || !ffxLoader ? "amd_fidelityfx_dx12.dll" : "amd_fidelityfx_framegeneration_dx12.dll",
                combinedFfx || (ffxLoader && Has("amd_fidelityfx_framegeneration_dx12.dll", "Optiscaler/amd_fidelityfx_framegeneration_dx12.dll")));
        if (FrameGeneration == "dlssg")
            foreach (var file in new[] { "sl.interposer.dll", "sl.common.dll", "sl.dlss_g.dll", "nvngx_dlssg.dll" })
                Require(file, Has($"Optiscaler/streamline/{file}"));
        if (Upscaler == "xess") Require("libxess.dll", Has("libxess.dll", "Optiscaler/libxess.dll"));
        if (Upscaler == "fsr3")
            Require(combinedFfx || !ffxLoader ? "amd_fidelityfx_dx12.dll" : "amd_fidelityfx_upscaler_dx12.dll",
                combinedFfx || (ffxLoader && Has("amd_fidelityfx_upscaler_dx12.dll", "Optiscaler/amd_fidelityfx_upscaler_dx12.dll")));
        if (Upscaler == "dlss") Require("nvngx_dlss.dll", Has("nvngx_dlss.dll", "Optiscaler/streamline/nvngx_dlss.dll"));
    }

    internal void Apply(string directory)
    {
        var scalerPath = Path.Combine(directory, "OptiScaler.ini");
        var hostPath = Path.Combine(directory, "OptiCaptureHost.ini");
        var scaler = File.ReadAllText(scalerPath);
        var host = File.ReadAllText(hostPath);
        WriteIfChanged(scalerPath, scaler, UpdateScalerIni(scaler));
        WriteIfChanged(hostPath, host, UpdateHostIni(host));
    }

    internal string UpdateScalerIni(string text)
    {
        text = SetIniValue(text, "FrameGen", "External", "false");
        text = SetIniValue(text, "FrameGen", "Enabled", UsesFrameGeneration ? "true" : "false");
        text = SetIniValue(text, "FrameGen", "FGInput", "Upscaler");
        text = SetIniValue(text, "FrameGen", "FGOutput", FrameGeneration == "xefg" ? "XeFG" : FrameGeneration);
        text = SetIniValue(text, "FrameGen", "FGNvngxReplacement", FrameGeneration == "dlssg" ? "None" : "auto");
        text = SetIniValue(text, "XeFG", "InterpolationCount", "1");
        text = SetIniValue(text, "DLSSG", "InterpolationCount", "1");
        text = SetIniValue(text, "Upscalers", "Dx12Upscaler", Upscaler == "fsr3" ? "ffx" : Upscaler);
        text = SetIniValue(text, "FSR", "UpscalerIndex", Upscaler == "fsr3" ? "2" : "auto");
        text = SetIniValue(text, "Menu", "ShortcutKey", $"0x{ForwardedMenuKey:X2}");
        if (FpsOverlay != "inherit") text = SetIniValue(text, "Menu", "ShowFps", FpsOverlay == "on" ? "true" : "false");
        if (MenuScale != "inherit") text = SetIniValue(text, "Menu", "Scale", MenuScale);
        if (Sharpness != "inherit")
        {
            text = SetIniValue(text, "Sharpness", "OverrideSharpness", Sharpness == "off" ? "false" : "true");
            if (Sharpness != "off") text = SetIniValue(text, "Sharpness", "Sharpness", Sharpness);
        }
        return text;
    }

    internal string UpdateHostIni(string text)
    {
        text = SetIniValue(text, "Settings", "FrameGenMode", FrameGeneration switch
        {
            "nofg" => "2",
            "dlssg" => "8",
            "fsrfg" => "9",
            _ => "0"
        });
        text = SetIniValue(text, "Shortcuts", "Menu", HostMenuHotkey);
        text = SetIniValue(text, "General", "MenuKey", ForwardedMenuKey.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return VSync == "inherit" ? text : SetIniValue(text, "General", "VSync", VSync == "on" ? "1" : "0");
    }

    private static string SetIniValue(string text, string section, string key, string value)
    {
        var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var header = $"[{section}]";
        var start = lines.FindIndex(line => line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines[^1].Length != 0) lines.Add("");
            lines.Add(header);
            lines.Add($"{key} = {value}");
            lines.Add("");
            return string.Join(eol, lines);
        }

        var end = start + 1;
        var found = false;
        for (; end < lines.Count && !lines[end].TrimStart().StartsWith('['); end++)
        {
            var equals = lines[end].IndexOf('=');
            if (equals < 0 || !lines[end][..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (!lines[end][(equals + 1)..].Trim().Equals(value, StringComparison.Ordinal))
                lines[end] = $"{key} = {value}";
            found = true;
        }
        if (!found) lines.Insert(end == lines.Count && lines[^1].Length == 0 ? end - 1 : end, $"{key} = {value}");
        return string.Join(eol, lines);
    }

    private static void WriteIfChanged(string path, string previous, string updated)
    {
        if (updated == previous) return;
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, updated);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
