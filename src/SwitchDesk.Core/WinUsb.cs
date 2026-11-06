using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SwitchDesk.Core;

public sealed record UsbCandidate(string Path, string Description);
public sealed record UsbDriverDiagnostic(string Message, string? BindableInstanceId = null, string? WinUsbInstanceId = null)
{
    public override string ToString() => Message;
}
public static class UsbDiscovery
{
    public static readonly Guid InterfaceGuid = new("51A376D0-670B-4C19-A2ED-5210DE5C0001");
    private static readonly Guid UsbGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    public static string? ProblemMessage(uint code) => code switch
    {
        28 => "Windows 报告此设备未安装驱动（代码 28）",
        10 => "Windows 无法启动此设备（代码 10）",
        22 => "此设备已被禁用（Windows 代码 22）",
        0 => null,
        _ => $"Windows 报告设备问题（代码 {code}）"
    };

    private const string SharedId = @"USB\VID_057E&PID_3000";
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid BusProperties = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2");
    internal sealed record DriverNode(string Id, string? Product, bool PlainDevice, uint StatusResult, uint Status, uint Code, string? Service)
    {
        private const uint Started = 0x8, HasProblem = 0x400;
        internal bool Identified => Product == "SwitchDesk PC" && PlainDevice && StatusResult == 0;
        internal bool CanBind => Identified && (Status & (Started | HasProblem)) == HasProblem && Code == 28 && Service == "";
        internal bool HasWinUsb => Identified && (Status & (Started | HasProblem)) == Started && string.Equals(Service, "WinUSB", StringComparison.OrdinalIgnoreCase);
        // CM_Get_DevNode_Status only defines the problem number when DN_HAS_PROBLEM is set.
        internal string? Problem => StatusResult == 0 && (Status & HasProblem) != 0
            ? ProblemMessage(Code) ?? "Windows 报告设备异常，未提供问题代码" : null;
        internal string Description => StatusResult != 0 ? $"未能读取设备状态（CONFIGRET {StatusResult}）" :
            $"{Problem ?? "Windows 未报告设备问题"} · 驱动服务 {Service switch { null => "未取得", "" => "未登记", _ => Service }}";
    }

    internal static bool MatchesSharedUsbId(string instanceId)
    {
        return instanceId.StartsWith(SharedId, StringComparison.OrdinalIgnoreCase) && instanceId.Length > SharedId.Length &&
               instanceId[SharedId.Length] is '\\' or '&';
    }

    internal static bool PlainDeviceIds(string instanceId, string? hardware, string? compatible)
    {
        if (!instanceId.StartsWith(SharedId + "\\", StringComparison.OrdinalIgnoreCase) ||
            instanceId.Length <= SharedId.Length + 1 || instanceId.IndexOf('\\', SharedId.Length + 1) >= 0 ||
            hardware == null || compatible == null) return false;
        var hardwareIds = hardware.Split('\0');
        var compatibleIds = compatible.Split('\0');
        return hardwareIds.Contains(SharedId, StringComparer.OrdinalIgnoreCase) &&
               !hardwareIds.Any(id => id.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) &&
               compatibleIds.All(id => id.Length > 0 && !id.Equals(@"USB\COMPOSITE", StringComparison.OrdinalIgnoreCase));
    }

    // Use unified PnP properties. Only ERROR_NOT_FOUND denotes an absent service.
    // Any malformed, oversized, inaccessible or changing property makes the node ineligible.
    private static string? ReadProperty(nint set, ref Native.DeviceData device, Guid format, uint pid, uint expectedType, out bool absent)
    {
        const uint stringList = 0x2012;
        var key = new Native.PropertyKey { Format = format, Id = pid };
        var bytes = new byte[4096];
        absent = false;
        if (!Native.SetupDiGetDevicePropertyW(set, ref device, ref key, out var type, bytes, (uint)bytes.Length, out var size, 0))
        { absent = Marshal.GetLastWin32Error() == 1168; return null; }
        if (type != expectedType || size < 2 || (size & 1) != 0 || size > bytes.Length) return null;
        var text = Encoding.Unicode.GetString(bytes, 0, (int)size);
        var end = type == stringList ? "\0\0" : "\0";
        if (!text.EndsWith(end, StringComparison.Ordinal)) return null;
        var value = text[..^end.Length];
        if (value.Length == 0 || (type != stringList && value.Contains('\0'))) return null;
        return value;
    }

    private static DriverNode? ReadDriverNode(nint set, ref Native.DeviceData device, out int error)
    {
        var id = new StringBuilder(512);
        error = 0;
        if (!Native.SetupDiGetDeviceInstanceId(set, ref device, id, (uint)id.Capacity, out _))
        { error = Marshal.GetLastWin32Error(); return null; }
        var instance = id.ToString();
        if (!MatchesSharedUsbId(instance)) return null;
        var product = ReadProperty(set, ref device, BusProperties, 4, 0x12, out _);
        var hardware = ReadProperty(set, ref device, DeviceProperties, 3, 0x2012, out _);
        var compatible = ReadProperty(set, ref device, DeviceProperties, 4, 0x2012, out _);
        var service = ReadProperty(set, ref device, DeviceProperties, 6, 0x12, out var absent);
        var result = Native.CM_Get_DevNode_Status(out var status, out var problem, device.Instance, 0);
        return new(instance, product, PlainDeviceIds(instance, hardware, compatible), result, status, problem, absent ? "" : service);
    }

    internal static string? ReadBindingCandidate(nint set, ref Native.DeviceData device) =>
        ReadDriverNode(set, ref device, out _) is { CanBind: true } node ? node.Id : null;

    private static List<DriverNode> ReadDriverNodes(out int enumerationError)
    {
        var nodes = new List<DriverNode>();
        enumerationError = 0;
        // DIGCF_PRESENT | DIGCF_ALLCLASSES, restricted to the USB enumerator. No device handles are opened.
        var set = Native.SetupDiGetUsbDevices(0, "USB", 0, 6);
        if (set == new nint(-1)) { enumerationError = Marshal.GetLastWin32Error(); return nodes; }
        try
        {
            for (uint i = 0; ; i++)
            {
                var device = new Native.DeviceData { Size = (uint)Marshal.SizeOf<Native.DeviceData>() };
                if (!Native.SetupDiEnumDeviceInfo(set, i, ref device))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 259) enumerationError = error; // ERROR_NO_MORE_ITEMS is normal.
                    break;
                }
                var node = ReadDriverNode(set, ref device, out var readError);
                if (readError != 0) enumerationError = readError;
                if (node != null) nodes.Add(node);
            }
            return nodes;
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    // Preserve the issue-only contract for existing callers.
    public static string? DiagnoseMissingInterface() =>
        ReadDriverNodes(out _).Select(node => node.Problem).FirstOrDefault(message => message != null);

    public static UsbDriverDiagnostic InspectDrivers()
    {
        var nodes = ReadDriverNodes(out var error);
        return DriverReport(nodes, error);
    }

    internal static UsbDriverDiagnostic DriverReport(IReadOnlyList<DriverNode> nodes, int enumerationError)
    {
        var lines = new List<string>();
        if (enumerationError != 0) lines.Add($"未能完整枚举 USB 设备（Win32 {enumerationError}）");
        if (nodes.Count == 0 && enumerationError == 0)
            return new("Windows 当前未枚举到 VID_057E / PID_3000 设备。请核对主机所开的程序、USB 数据线和端口。");
        if (nodes.Count > 0)
        {
            lines.Add("检测到 VID_057E / PID_3000。SwitchDesk 配套、DBI 等应用可共用此标识，请核对主机当前画面。");
            lines.AddRange(nodes.Select((node, i) => $"USB 节点 {i + 1} · 总线产品 {node.Product ?? "未取得"} · {node.Description}"));
            if (nodes.Count > 1) lines.Add("检测到多个匹配节点，已关闭自动绑定。");
        }
        var unique = enumerationError == 0 && nodes.Count == 1 ? nodes[0] : null;
        return new(string.Join(Environment.NewLine, lines), unique?.CanBind == true ? unique.Id : null, unique?.HasWinUsb == true ? unique.Id : null);
    }
    public static IReadOnlyList<UsbCandidate> Enumerate()
    {
        var results = new List<UsbCandidate>();
        foreach (var guidValue in new[] { InterfaceGuid, UsbGuid })
        {
            var guid = guidValue; var set = Native.SetupDiGetClassDevs(ref guid, null, 0, 0x12);
            if (set == new nint(-1)) continue;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new Native.InterfaceData { Size = (uint)Marshal.SizeOf<Native.InterfaceData>() };
                    if (!Native.SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref data)) break;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var required, 0);
                    if (required < 8 || required > 65536) continue;
                    var detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, 0)) continue;
                        var path = Marshal.PtrToStringUni(detail + 4)!;
                        if (guidValue == InterfaceGuid || path.Contains("vid_057e&pid_3000", StringComparison.OrdinalIgnoreCase))
                            results.Add(new(path, "Switch USB 管理接口"));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }
        return results.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { internal uint Size; internal Guid Guid; internal uint Flags; internal nint Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceData { internal uint Size; internal Guid Class; internal uint Instance; internal nint Reserved; }
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetUsbDevices(nint guid, string enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceData device);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInstanceId(nint set, ref DeviceData device, [Out] StringBuilder id, uint capacity, out uint required);
        [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDevicePropertyW(nint set, ref DeviceData device, ref PropertyKey key, out uint type, [Out] byte[] buffer, uint size, out uint required, uint flags);
        [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref InterfaceData data, nint detail, uint size, out uint required, nint deviceInfo);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
    }
}
public sealed class WinUsbStream : Stream
{
    private readonly SafeFileHandle file;
    private readonly SafeUsbHandle usb;
    private readonly byte readPipe, writePipe;
    private readonly SemaphoreSlim readGate = new(1), writeGate = new(1);
    private int disposed;
    public WinUsbStream(string devicePath)
    {
        file = Native.CreateFile(devicePath, 0xC0000000, 0, 0, 3, 0x40000000, 0);
        if (file.IsInvalid) { var error = Marshal.GetLastWin32Error(); file.Dispose(); throw new Win32Exception(error, $"无法打开 USB 接口（Win32 {error}）"); }
        if (!Native.WinUsb_Initialize(file, out usb)) { var error = Marshal.GetLastWin32Error(); file.Dispose(); throw new Win32Exception(error, $"无法初始化 WinUSB 接口（Win32 {error}），可在连接诊断中检查驱动"); }
        try
        {
            var descriptorBytes = new byte[18];
            if (!Native.WinUsb_GetDescriptor(usb, 1, 0, 0, descriptorBytes, 18, out var descriptorSize) || descriptorSize != 18)
                throw new IOException("无法核对 USB 产品描述符");
            var languages = new byte[255]; ushort language = 0x0409;
            if (Native.WinUsb_GetDescriptor(usb, 3, 0, 0, languages, 255, out var languageSize) && languageSize >= 4)
                language = (ushort)(languages[2] | languages[3] << 8);
            var product = new byte[255];
            if (!Native.WinUsb_GetDescriptor(usb, 3, descriptorBytes[15], language, product, 255, out var productSize) || productSize < 2 ||
                Encoding.Unicode.GetString(product, 2, (int)productSize - 2) != "SwitchDesk PC")
                throw new IOException("等待 SwitchDesk 配套程序；当前 USB 接口属于其他主机程序");
            if (!Native.WinUsb_QueryInterfaceSettings(usb, 0, out var descriptor)) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (byte i = 0; i < descriptor.Endpoints; i++)
            {
                if (!Native.WinUsb_QueryPipe(usb, 0, i, out var pipe) || pipe.Type != 2) continue;
                if ((pipe.Id & 0x80) != 0) readPipe = pipe.Id; else writePipe = pipe.Id;
            }
            if (readPipe == 0 || writePipe == 0) throw new IOException("USB 接口没有双向 Bulk 端点");
            uint timeout = 600000; // long save commits continue without a UI cancellation
            if (!Native.WinUsb_SetPipePolicy(usb, readPipe, 3, 4, ref timeout) || !Native.WinUsb_SetPipePolicy(usb, writePipe, 3, 4, ref timeout))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置 USB 传输超时");
        }
        catch { usb.Dispose(); file.Dispose(); throw; }
    }
    public override bool CanRead => Volatile.Read(ref disposed) == 0;
    public override bool CanWrite => Volatile.Read(ref disposed) == 0;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        readGate.Wait();
        try { return ReadCore(buffer, offset, count); }
        finally { readGate.Release(); }
    }
    private int ReadCore(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this); if (count == 0) return 0;
        var temporary = new byte[count];
        if (!Native.WinUsb_ReadPipe(usb, readPipe, temporary, (uint)count, out var read, 0)) throw new IOException("USB 读取中断", new Win32Exception(Marshal.GetLastWin32Error()));
        Buffer.BlockCopy(temporary, 0, buffer, offset, (int)read); return (int)read;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        writeGate.Wait();
        try { WriteCore(buffer, offset, count); }
        finally { writeGate.Release(); }
    }
    private void WriteCore(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var data = buffer.AsSpan(offset, count).ToArray();
        if (!Native.WinUsb_WritePipe(usb, writePipe, data, (uint)count, out var written, 0) || written != count)
            throw new IOException("USB 写入中断", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        await UsbPipeOperation.RunAsync(readGate, () =>
        {
            var data = new byte[buffer.Length]; var count = ReadCore(data, 0, data.Length);
            data.AsMemory(0, count).CopyTo(buffer); return count;
        }, () => Abort(readPipe), ct).ConfigureAwait(false);
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        await UsbPipeOperation.RunAsync(writeGate, () =>
        {
            var data = buffer.ToArray(); WriteCore(data, 0, data.Length); return true;
        }, () => Abort(writePipe), ct).ConfigureAwait(false);
    private void Abort(byte pipe) { try { if (!usb.IsClosed) Native.WinUsb_AbortPipe(usb, pipe); } catch (ObjectDisposedException) { } }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Abort(readPipe); Abort(writePipe);
            usb.Dispose(); file.Dispose();
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    private sealed class SafeUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeUsbHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.WinUsb_Free(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct InterfaceDescriptor { internal byte Length, Type, Number, Alternate, Endpoints, Class, SubClass, Protocol, String; }
        [StructLayout(LayoutKind.Sequential)] internal struct PipeInformation { internal int Type; internal byte Id; internal ushort MaxPacket; internal byte Interval; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_Initialize(SafeFileHandle device, out SafeUsbHandle usb);
        [DllImport("winusb.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_Free(nint usb);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_QueryInterfaceSettings(SafeUsbHandle usb, byte alternate, out InterfaceDescriptor descriptor);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_QueryPipe(SafeUsbHandle usb, byte alternate, byte index, out PipeInformation pipe);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_GetDescriptor(SafeUsbHandle usb, byte type, byte index, ushort language, [Out] byte[] buffer, uint length, out uint transferred);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_ReadPipe(SafeUsbHandle usb, byte pipe, [Out] byte[] buffer, uint length, out uint transferred, nint overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_WritePipe(SafeUsbHandle usb, byte pipe, byte[] buffer, uint length, out uint transferred, nint overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_AbortPipe(SafeUsbHandle usb, byte pipe);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WinUsb_SetPipePolicy(SafeUsbHandle usb, byte pipe, uint policy, uint size, ref uint value);
    }
}
