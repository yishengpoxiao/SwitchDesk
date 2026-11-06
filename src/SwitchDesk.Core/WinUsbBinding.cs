using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using DeviceData = SwitchDesk.Core.UsbDiscovery.Native.DeviceData;

namespace SwitchDesk.Core;

[SupportedOSPlatform("windows")]
public static class WinUsbBinding
{
    private const uint ClassDriver = 1;
    private const string InterfaceNames = "DeviceInterfaceGUIDs";
    private static readonly string SystemInf = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "..", "INF", "winusb.inf"));

    public static bool Install(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (!Environment.Is64BitProcess || !UsbDiscovery.MatchesSharedUsbId(instanceId) || instanceId.Contains('\0'))
            throw new InvalidDataException("设备实例或进程架构无效");
        using var exclusive = new Mutex(false, @"Global\SwitchDesk.WinUsbBinding.51A376D0");
        try { if (!exclusive.WaitOne(0)) throw new IOException("另一个驱动绑定任务正在进行"); }
        catch (AbandonedMutexException) { /* Ownership transferred. Recheck actual PnP state below. */ }
        try { return InstallExclusive(instanceId); }
        finally { exclusive.ReleaseMutex(); }
    }

    private static bool InstallExclusive(string instanceId)
    {
        if (!Same(UsbDiscovery.InspectDrivers().BindableInstanceId, instanceId)) throw Changed();
        // Build without a setup class. A Code 28 node may not have one yet. Only the fixed,
        // inbox INF is enumerated, and the device inherits this list (DIOD_INHERIT_CLASSDRVS).
        var set = Native.SetupDiCreateDeviceInfoList(0, 0);
        if (set == -1) throw Error("无法建立本机设备信息集");
        try
        {
            BuildInboxList(set);
            var device = new DeviceData { Size = (uint)Marshal.SizeOf<DeviceData>() };
            if (!Native.SetupDiOpenDeviceInfoW(set, instanceId, 0, 2, ref device)) throw Error("无法打开指定的设备实例");
            if (!Same(UsbDiscovery.ReadBindingCandidate(set, ref device), instanceId)) throw Changed();
            var driver = SelectInboxDriver(set, ref device);
            using var key = OpenHardwareKey(set, ref device);
            if (!Same(UsbDiscovery.ReadBindingCandidate(set, ref device), instanceId)) throw Changed();

            // WinUSB reads DeviceInterfaceGUIDs when it starts. The generic system INF has
            // no application GUID. A short registry transaction preserves other writers.
            string[]? before = null, written = null;
            ChangeInterfaceList(key, registry =>
            {
                before = ReadInterfaceList(registry);
                var updated = WithInterfaceGuid(before, ReadValue<string>(registry, "DeviceInterfaceGUID", RegistryValueKind.String));
                if (updated == null) return false;
                registry.SetValue(InterfaceNames, updated, RegistryValueKind.MultiString);
                written = updated;
                return true;
            });
            try
            {
                if (!Same(UsbDiscovery.ReadBindingCandidate(set, ref device), instanceId)) throw Changed();
                if (!Native.DiInstallDevice(0, set, ref device, ref driver, 0, out var needReboot))
                    throw Error("Windows 未能为选定设备安装系统 WinUSB 驱动");
                return needReboot;
            }
            catch
            {
                if (written != null)
                    ChangeInterfaceList(key, registry =>
                    {
                        var current = ReadInterfaceList(registry);
                        // A different installer may have changed the list in the meantime.
                        // Do not roll back values we did not write.
                        if (current == null || !current.SequenceEqual(written, StringComparer.Ordinal)) return false;
                        if (before == null) registry.DeleteValue(InterfaceNames);
                        else registry.SetValue(InterfaceNames, before, RegistryValueKind.MultiString);
                        return true;
                    });
                throw;
            }
        }
        finally { UsbDiscovery.Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static IOException Changed() => new("设备已断开、模式或驱动状态已变化，或无法唯一确认目标。请返回连接诊断重新检测。");
    private static SafeRegistryHandle OpenHardwareKey(nint set, ref DeviceData device)
    {
        var key = Native.SetupDiOpenDevRegKey(set, ref device, 1, 0, 1, 3); // global DIREG_DEV, query + set
        if (!key.IsInvalid) return key;
        var error = Marshal.GetLastPInvokeError();
        key.Dispose();
        if (error is not (2 or 3 or unchecked((int)0xe0000204))) // ERROR_KEY_DOES_NOT_EXIST
            throw new Win32Exception(error, $"无法打开该设备的硬件配置键（Win32 0x{error:X8}）");
        // A never-installed device need not have a Device Parameters key yet.
        key = Native.SetupDiCreateDevRegKeyW(set, ref device, 1, 0, 1, 0, null);
        if (!key.IsInvalid) return key;
        var failure = Error("无法建立该设备的硬件配置键");
        key.Dispose();
        throw failure;
    }
    private static bool Same(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static Win32Exception Error(string stage)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{stage}（Win32 0x{code:X8}）");
    }

    private static void BuildInboxList(nint set)
    {
        var parameters = new InstallParams { Size = (uint)Marshal.SizeOf<InstallParams>() };
        if (!Native.SetupDiGetDeviceInstallParamsW(set, 0, ref parameters)) throw Error("无法读取驱动枚举参数");
        parameters.Flags |= 0x10000;    // DI_ENUMSINGLEINF
        parameters.FlagsEx |= 0x800;    // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
        parameters.DriverPath = SystemInf;
        if (!Native.SetupDiSetDeviceInstallParamsW(set, 0, ref parameters) ||
            !Native.SetupDiBuildDriverInfoList(set, 0, ClassDriver)) throw Error("无法读取系统 winusb.inf 驱动列表");
    }

    private static DriverInfo SelectInboxDriver(nint set, ref DeviceData device)
    {
        DriverInfo? selected = null;
        for (uint i = 0; ; ++i)
        {
            var info = new DriverInfo { Size = (uint)Marshal.SizeOf<DriverInfo>() };
            if (!Native.SetupDiEnumDriverInfoW(set, ref device, ClassDriver, i, ref info))
            {
                if (Marshal.GetLastPInvokeError() != 259) throw Error("无法枚举系统驱动型号");
                break;
            }
            if (MatchesModel(set, ref device, ref info))
            {
                if (selected.HasValue) throw new InvalidDataException("系统 winusb.inf 包含多个匹配型号");
                selected = info;
            }
        }
        return selected ?? throw new InvalidDataException("当前系统 winusb.inf 未提供可选的 WINUSB 通用功能驱动型号");
    }

    private static bool MatchesModel(nint set, ref DeviceData device, ref DriverInfo info)
    {
        Native.SetupDiGetDriverInfoDetailW(set, ref device, ref info, 0, 0, out var length);
        if (Marshal.GetLastPInvokeError() != 122) throw Error("无法读取系统驱动型号大小");
        var idOffset = Marshal.OffsetOf<DriverDetail>(nameof(DriverDetail.HardwareId)).ToInt32();
        var fixedSize = Marshal.SizeOf<DriverDetail>();
        if (length <= idOffset || length > 65536) throw new InvalidDataException("系统驱动型号结构大小无效");
        var capacity = Math.Max((int)length, fixedSize);
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            Marshal.WriteInt32(buffer, fixedSize);
            if (!Native.SetupDiGetDriverInfoDetailW(set, ref device, ref info, buffer, (uint)capacity, out length)) throw Error("无法读取系统驱动型号");
            var detail = Marshal.PtrToStructure<DriverDetail>(buffer);
            if (length > capacity || detail.CompatibleOffset <= 1 || idOffset + 2L * detail.CompatibleOffset > length) return false;
            var id = Marshal.PtrToStringUni(buffer + idOffset, (int)detail.CompatibleOffset - 1);
            return IsInboxModel(detail.Section, detail.Inf, id, detail.CompatibleLength);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool IsInboxModel(string? section, string? inf, string? id, uint compatibleLength) =>
        Same(section, "WINUSB") && Same(inf, SystemInf) && Same(id, @"USB\MS_COMP_WINUSB") && compatibleLength == 0;

    private static string[]? ReadInterfaceList(RegistryKey registry) =>
        ReadValue<string[]>(registry, InterfaceNames, RegistryValueKind.MultiString);

    private static T? ReadValue<T>(RegistryKey registry, string name, RegistryValueKind kind) where T : class
    {
        if (!registry.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        if (registry.GetValueKind(name) != kind || registry.GetValue(name) is not T value)
            throw new InvalidDataException("已有设备接口配置无法安全合并：" + name);
        return value;
    }

    internal static string[]? WithInterfaceGuid(string[]? existing, string? singular)
    {
        var values = existing?.ToList() ?? [];
        var guid = UsbDiscovery.InterfaceGuid.ToString("B");
        if (values.Contains(guid, StringComparer.OrdinalIgnoreCase)) return null;
        // Some older device configurations use the singular REG_SZ spelling.
        if (!string.IsNullOrEmpty(singular) && !values.Contains(singular, StringComparer.OrdinalIgnoreCase))
            values.Add(singular);
        if (!values.Contains(guid, StringComparer.OrdinalIgnoreCase)) values.Add(guid);
        return values.ToArray();
    }

    private static void ChangeInterfaceList(SafeRegistryHandle deviceKey, Func<RegistryKey, bool> change)
    {
        using var transaction = Native.CreateTransaction(0, 0, 0, 0, 0, 0, null);
        if (transaction.IsInvalid) throw Error("无法建立设备接口配置事务");
        var rc = Native.RegOpenKeyTransactedW(deviceKey, "", 0, 3, out var transacted, transaction, 0);
        using (transacted)
        {
            if (rc != 0) throw new Win32Exception(rc, $"无法打开设备接口配置事务（Win32 {rc}）");
            using var registry = RegistryKey.FromHandle(transacted);
            if (!change(registry)) return;
        }
        if (!Native.CommitTransaction(transaction)) throw Error("设备接口配置发生变化，无法提交");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InstallParams
    {
        internal uint Size, Flags, FlagsEx;
        internal nint Parent, Callback, Context, Queue, ClassReserved;
        internal uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DriverPath;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DriverInfo
    {
        internal uint Size, DriverType; internal nint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Provider;
        internal FILETIME Date; internal ulong Version;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DriverDetail
    {
        internal uint Size; internal FILETIME Date; internal uint CompatibleOffset, CompatibleLength; internal nint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Section;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Inf;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Description;
        internal ushort HardwareId;
    }
    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern nint SetupDiCreateDeviceInfoList(nint guid, nint parent);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)] internal static extern bool SetupDiGetDeviceInstallParamsW(nint set, nint device, ref InstallParams parameters);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)] internal static extern bool SetupDiSetDeviceInstallParamsW(nint set, nint device, ref InstallParams parameters);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiBuildDriverInfoList(nint set, nint device, uint type);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern bool SetupDiOpenDeviceInfoW(nint set, string instance, nint parent, uint flags, ref DeviceData device);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)] internal static extern bool SetupDiEnumDriverInfoW(nint set, ref DeviceData device, uint type, uint index, ref DriverInfo info);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)] internal static extern bool SetupDiGetDriverInfoDetailW(nint set, ref DeviceData device, ref DriverInfo info, nint detail, uint size, out uint required);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern SafeRegistryHandle SetupDiOpenDevRegKey(nint set, ref DeviceData device, uint scope, uint profile, uint type, uint access);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] internal static extern SafeRegistryHandle SetupDiCreateDevRegKeyW(nint set, ref DeviceData device, uint scope, uint profile, uint type, nint inf, string? section);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("newdev.dll", SetLastError = true)] internal static extern bool DiInstallDevice(nint parent, nint set, ref DeviceData device, ref DriverInfo info, uint flags, out bool needReboot);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ktmw32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern SafeFileHandle CreateTransaction(nint attributes, nint id, uint options, uint isolation, uint flags, uint timeout, string? description);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ktmw32.dll", SetLastError = true)] internal static extern bool CommitTransaction(SafeFileHandle transaction);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] internal static extern int RegOpenKeyTransactedW(SafeRegistryHandle key, string subkey, uint options, uint access, out SafeRegistryHandle result, SafeFileHandle transaction, nint extra);
    }
}
