using System.Diagnostics;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed class SettingsStore(string directory)
{
    public string Root => directory;
    public string RecoveryMessage { get; private set; } = "";
    public AppSettings Load()
    {
        RecoveryMessage = "";
        var path = Path.Combine(directory, "settings.json");
        if (!File.Exists(path)) return AppSettings.Default(directory);
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Wire.Json) ?? throw new InvalidDataException("设置文件格式错误");
            var storedPassword = settings.FtpPassword;
            settings = settings with
            {
                AutoBackupKeys = settings.AutoBackupKeys ?? [],
                SysDvrClient = settings.SysDvrClient ?? "",
                DbiBackendPath = settings.DbiBackendPath ?? "",
                Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System",
                SysDvrMode = settings.SysDvrMode == StreamingOptions.BridgeMode ? StreamingOptions.BridgeMode : StreamingOptions.UsbMode,
                SysDvrAddress = settings.SysDvrAddress ?? "",
                FtpHost = settings.FtpHost ?? "",
                FtpPort = settings.FtpPort is >= 1 and <= 65535 ? settings.FtpPort : 21,
                FtpUser = settings.FtpUser ?? "",
                FtpPassword = "",
                HomebrewIndex = settings.HomebrewIndex ?? ""
            };
            Validate(settings);
            // Use the same atomic replacement as normal saves: a failed migration
            // must not truncate the user's otherwise valid settings.
            if (!string.IsNullOrEmpty(storedPassword))
            {
                try { Save(settings); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { RecoveryMessage = "设置已读取，但旧版 FTP 密码未能从设置文件清除；请解除文件占用或检查目录权限后重新保存设置。"; }
            }
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            var backup = path + ".invalid-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
            try { File.Move(path, backup); RecoveryMessage = "设置损坏，已保留原文件并使用默认设置。可在设置中重新选择原有目录。"; }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { RecoveryMessage = "设置无法读取，原文件保持不变；本次使用默认设置。"; }
            return AppSettings.Default(directory);
        }
    }
    public void Save(AppSettings settings)
    {
        settings = settings with { FtpPassword = "" };
        Validate(settings);
        Directory.CreateDirectory(directory); var target = Path.Combine(directory, "settings.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Wire.Json)); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(AppSettings settings)
    {
        if (settings.Retention < 1 || settings.Retention > 1000) throw new InvalidDataException("历史版本保留数量应为 1–1000");
        foreach (var path in new[] { settings.BackupDirectory, settings.LibraryDirectory, settings.ExportDirectory })
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("请选择绝对路径");
        if (settings.AutoBackupKeys == null || settings.AutoBackupKeys.Any(x => x == null)) throw new InvalidDataException("自动备份设置无效");
        if (settings.SysDvrMode is not (StreamingOptions.UsbMode or StreamingOptions.BridgeMode)) throw new InvalidDataException("投屏连接方式无效");
        Streaming.ValidateAddress(settings.SysDvrMode, settings.SysDvrAddress);
        if (settings.FtpPort is < 1 or > 65535) throw new InvalidDataException("FTP 端口应为 1–65535");
        if (settings.HomebrewIndex.Length > 0 && (!Uri.TryCreate(settings.HomebrewIndex, UriKind.Absolute, out var index) || index.Scheme is not ("http" or "https")))
            throw new InvalidDataException("Homebrew 索引地址必须是 http(s) URL");
    }
}
public static class Streaming
{
    /// <summary>依据官方 SysDVR-Client 6.x CLI 构造参数：usb、或 bridge [IP]（网络桥接，IP 可省略自动发现）。</summary>
    public static string[] BuildArguments(StreamingOptions options)
    {
        // 与设备启动路径共用同一套源参数校验，避免两处规则漂移。
        var args = new List<string>(UsbStreamingRuntime.BuildSourceArguments(options.Mode, options.Address));
        if (options.Title.Length > 0) { args.Add("--title"); args.Add(options.Title); }
        return [.. args];
    }
    public static void ValidateAddress(string mode, string? address)
    {
        if (mode != StreamingOptions.BridgeMode) return;
        var host = (address ?? "").Trim();
        if (host.Length > 0 && Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new InvalidDataException("投屏主机地址无效");
    }
}
