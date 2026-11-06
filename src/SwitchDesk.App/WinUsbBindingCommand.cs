using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SwitchDesk.Core;

namespace SwitchDesk.App;

internal static class WinUsbBindingCommand
{
    internal const string Switch = "--bind-switchdesk-winusb";
    private const int InvalidArguments = 87, AccessDenied = 5, TargetUnavailable = 1167, Unsupported = 50, RestartRequired = 3010;

    private static bool ValidInstanceId(string id)
    {
        const string prefix = @"USB\VID_057E&PID_3000\";
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Length > prefix.Length && id.Length < 200 &&
            id.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ&._-".AsSpan()) < 0;
    }

    // This mode runs before the normal settings, USB monitor, main window or single-instance mutex.
    internal static int Execute(string[] args)
    {
        if (args.Length != 2 || args[0] != Switch || !ValidInstanceId(args[1])) return InvalidArguments;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return AccessDenied;
            // This dedicated elevated process only needs the inbox Windows libraries.
            NativeLibrary.SetDllImportResolver(typeof(WinUsbBinding).Assembly, (name, assembly, _) =>
                NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, name), assembly, DllImportSearchPath.System32));
            return WinUsbBinding.Install(args[1]) ? RestartRequired : 0;
        }
        catch (Win32Exception ex) { return ex.NativeErrorCode != 0 ? ex.NativeErrorCode : 31; }
        catch (Exception ex) when (ex is InvalidDataException or DllNotFoundException or EntryPointNotFoundException)
        { return Unsupported; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        { return TargetUnavailable; }
        catch (UnauthorizedAccessException) { return AccessDenied; }
    }

    internal static async Task<string> RunAsync(string instanceId)
    {
        if (!ValidInstanceId(instanceId)) throw new InvalidDataException("设备实例与 SwitchDesk PC 不匹配，请重新检查驱动。");
        var app = Environment.ProcessPath;
        if (app == null || !Path.GetFileName(app).Equals("SwitchDesk.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请从 Windows 运行包中的 SwitchDesk.exe 启动一键绑定。");
        var start = new ProcessStartInfo(app) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
        start.ArgumentList.Add(Switch);
        start.ArgumentList.Add(instanceId);
        try
        {
            using var process = await Task.Run(() => Process.Start(start)) ?? throw new IOException("Windows 未能打开驱动绑定程序。");
            await process.WaitForExitAsync();
            return process.ExitCode switch
            {
                0 => "Windows 已接受绑定请求。",
                RestartRequired => "Windows 已接受绑定请求，并要求重启电脑。",
                TargetUnavailable => "设备已变化或不再符合绑定条件。请保持 SwitchDesk 配套打开，再重新检查。",
                AccessDenied => "Windows 拒绝驱动绑定，请核对管理员权限。",
                Unsupported => "当前 Windows 驱动型号或设备配置不支持一键绑定，请使用 Zadig 指引。",
                var code => $"Windows 未完成绑定（0x{code:X8}）。可核对设备状态或按 Zadig 指南操作。"
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "已取消管理员授权。"; }
    }
}
