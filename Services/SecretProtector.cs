using System.Security.Cryptography;
using System.Text;

namespace GameLocker.Services;

/// <summary>
/// 本机密文存取：DPAPI(CurrentUser) 加密后落盘于 %APPDATA%\GameLocker\{网盘}.auth。
/// 非打包应用无法可靠使用 PasswordVault，故用 DPAPI；密文仅当前 Windows 用户的同一台机器可解
/// （换用户 / 换机器解密失败即视为未设置）。tianyi.auth 存天翼云，baidu.auth 存百度网盘。
/// </summary>
public static class SecretProtector
{
    private static string PathFor(string provider) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameLocker", provider + ".auth");

    /// <summary>读出保存的密文；文件缺失、损坏或解密失败均返回 null（视为未设置）</summary>
    public static string? Load(string provider = "tianyi")
    {
        try
        {
            var path = PathFor(provider);
            if (!File.Exists(path)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            var s = Encoding.UTF8.GetString(raw);
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    /// <summary>加密写入指定网盘的登录会话，写入失败上报调用方</summary>
    public static void Save(string secret, string provider = "tianyi")
    {
        var path = PathFor(provider);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, enc);
        var legacy = Path.Combine(Path.GetDirectoryName(path)!, "webdav.auth");
        if (File.Exists(legacy)) File.Delete(legacy);
    }

    /// <summary>删除指定网盘的密文</summary>
    public static void Clear(string provider = "tianyi")
    {
        var path = PathFor(provider);
        if (File.Exists(path)) File.Delete(path);
    }
}
