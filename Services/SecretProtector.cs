using System.Security.Cryptography;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 本机密文存取：DPAPI(CurrentUser) 加密后落盘于 %APPDATA%\GameLocker\webdav.auth。
/// 非打包应用无法可靠使用 PasswordVault，故用 DPAPI；密文仅当前 Windows 用户的同一台机器可解
/// （换用户 / 换机器解密失败即视为未设置）。目前用于云端备份的 WebDAV 应用密码。
/// </summary>
public static class SecretProtector
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLocker", "webdav.auth");

    /// <summary>读出保存的密文；文件缺失、损坏或解密失败均返回 null（视为未设置）</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(raw);
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    /// <summary>加密写入密文（失败静默：不影响本次会话已保存的设置）</summary>
    public static void Save(string secret)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, enc);
        }
        catch { /* 落盘失败不影响设置本身，仅下次启动需重新填密码 */ }
    }

    /// <summary>删除本机密文</summary>
    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* 删除失败无害 */ }
    }
}
