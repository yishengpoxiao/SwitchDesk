using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SwitchDesk.Tests;

/// <summary>回环 FTP 模拟主机：实现被动模式与测试所需的命令子集，可注入登录失败、断传与不同列表风格。</summary>
internal sealed class FtpMockServer : IDisposable
{
    private readonly TcpListener control = new(IPAddress.Loopback, 0);
    private TcpListener? dataListener;
    private readonly CancellationTokenSource stop = new();
    private Task? worker;
    public readonly Dictionary<string, byte[]> Files = new() { ["/readme.txt"] = [1, 2, 3, 4, 5], ["/switch/data.bin"] = new byte[70000] };
    public readonly HashSet<string> Directories = ["/", "/switch"];
    public bool FailLogin, NoMlsd, UseDosList, FailTransfer;
    public int RetrDelayMs;
    public int RetrFinalReplyDelayMs;
    public TaskCompletionSource RetrAwaitingFinalReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ListDelayMs;
    public bool TruncateSuccessfulRetr, IgnoreQuit;
    public bool MultilineGreeting;
    public string? PassiveReplyOverride;
    public bool RejectMkdir;
    public bool NoSize, NoRename;
    public Action<string>? AfterUpload;
    public int SizeReplyDelayMs;
    public TaskCompletionSource SizeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Downloads, Uploads;
    private string? renameFrom;
    public IPEndPoint Endpoint => (IPEndPoint)control.LocalEndpoint;

    public FtpMockServer() { control.Start(); worker = ServeAsync(); }

    private async Task ServeAsync()
    {
        try
        {
            using var socket = await control.AcceptTcpClientAsync(stop.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
            if (MultilineGreeting)
            {
                await writer.WriteLineAsync("220-mock welcome");
                await writer.WriteLineAsync("200 numeric detail inside the greeting");
            }
            await writer.WriteLineAsync("220 mock ready");
            while (await reader.ReadLineAsync(stop.Token) is { } raw)
            {
                var separator = raw.IndexOf(' ');
                var command = (separator < 0 ? raw : raw[..separator]).ToUpperInvariant();
                var argument = separator < 0 ? "" : raw[(separator + 1)..];
                switch (command)
                {
                    case "USER": await writer.WriteLineAsync(FailLogin ? "530 rejected" : "331 need password"); break;
                    case "PASS": await writer.WriteLineAsync(FailLogin ? "530 login incorrect" : "230 logged in"); break;
                    case "TYPE": await writer.WriteLineAsync("200 binary mode"); break;
                    case "SYST": await writer.WriteLineAsync("215 UNIX Type: L8"); break;
                    case "FEAT": await writer.WriteLineAsync("211-no features"); await writer.WriteLineAsync("211 end"); break;
                    case "PWD": await writer.WriteLineAsync("257 \"/\" is current directory"); break;
                    case "CWD": await writer.WriteLineAsync(Directories.Contains(argument) ? "250 changed" : "550 no such directory"); break;
                    case "SIZE":
                        SizeRequested.TrySetResult();
                        if (SizeReplyDelayMs > 0) await Task.Delay(SizeReplyDelayMs, stop.Token);
                        if (NoSize) { await writer.WriteLineAsync("502 SIZE not implemented"); break; }
                        await writer.WriteLineAsync(Files.TryGetValue(argument, out var size) ? "213 " + size.Length : "550 unknown file"); break;
                    case "PASV": await writer.WriteLineAsync("227 " + PassiveAddress()); break;
                    case "MLSD":
                        if (NoMlsd) { await writer.WriteLineAsync("500 unknown command"); break; }
                        await DataCommandAsync(writer, () => ServeListAsync(writer, argument, MachineLine)); break;
                    case "LIST":
                        if (argument.StartsWith("-la ")) argument = argument[4..];
                        await DataCommandAsync(writer, () => ServeListAsync(writer, argument, UseDosList ? DosLine : UnixLine)); break;
                    case "RETR": await DataCommandAsync(writer, () => ServeRetrAsync(writer, argument)); break;
                    case "STOR": await DataCommandAsync(writer, () => ServeStorAsync(writer, argument)); break;
                    case "MKD":
                        if (RejectMkdir) { await writer.WriteLineAsync("550 cannot create directory"); break; }
                        Directories.Add(argument); await writer.WriteLineAsync("257 created"); break;
                    case "RMD": Directories.Remove(argument); await writer.WriteLineAsync("250 removed"); break;
                    case "DELE": if (!Files.Remove(argument)) { await writer.WriteLineAsync("550 unknown file"); break; } await writer.WriteLineAsync("250 deleted"); break;
                    case "RNFR":
                        if (NoRename) { await writer.WriteLineAsync("502 rename not implemented"); break; }
                        renameFrom = argument; await writer.WriteLineAsync("350 waiting for RNTO"); break;
                    case "RNTO":
                        if (renameFrom != null && Files.Remove(renameFrom, out var payload)) { Files[argument] = payload; renameFrom = null; await writer.WriteLineAsync("250 renamed"); }
                        else if (renameFrom != null && Directories.Contains(renameFrom)) { Directories.Remove(renameFrom); Directories.Add(argument); renameFrom = null; await writer.WriteLineAsync("250 renamed"); }
                        else await writer.WriteLineAsync("550 rename source missing");
                        break;
                    case "ABOR": await writer.WriteLineAsync("426 aborted"); await writer.WriteLineAsync("226 done"); break;
                    case "NOOP": await writer.WriteLineAsync("200 ok"); break;
                    case "QUIT": if (IgnoreQuit) break; await writer.WriteLineAsync("221 bye"); return;
                    default: await writer.WriteLineAsync("502 unknown command"); break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private string PassiveAddress()
    {
        dataListener = new TcpListener(IPAddress.Loopback, 0); dataListener.Start();
        if (PassiveReplyOverride != null) return PassiveReplyOverride;
        var port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
        var ip = ((IPEndPoint)control.LocalEndpoint).Address.GetAddressBytes();
        return $"Entering Passive Mode ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{port / 256},{port % 256})";
    }

    /// <summary>数据连接命令独立容错：客户端提前断开时，回复 426 并继续服务后续命令。</summary>
    private async Task DataCommandAsync(StreamWriter controlWriter, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        { try { await controlWriter.WriteLineAsync("426 data connection failed"); } catch (Exception ignored) when (ignored is IOException or OperationCanceledException) { } }
    }

    private async Task ServeListAsync(StreamWriter controlWriter, string path, Func<string, bool, long, string> lineOf)
    {
        await controlWriter.WriteLineAsync("150 opening data connection");
        using var data = await dataListener!.AcceptTcpClientAsync(stop.Token);
        await using var dataStream = data.GetStream();
        await using var listWriter = new StreamWriter(dataStream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        if (ListDelayMs > 0) await Task.Delay(ListDelayMs, stop.Token);
        var prefix = path.TrimEnd('/'); if (prefix.Length == 0) prefix = "/";
        foreach (var file in Files.Where(x => ParentOf(x.Key) == prefix)) await listWriter.WriteLineAsync(lineOf(file.Key, false, file.Value.Length));
        foreach (var directory in Directories.Where(d => d != "/" && ParentOf(d) == prefix)) await listWriter.WriteLineAsync(lineOf(directory, true, 0));
        await controlWriter.WriteLineAsync("226 transfer complete");
    }

    private async Task ServeRetrAsync(StreamWriter controlWriter, string path)
    {
        if (!Files.TryGetValue(path, out var payload)) { await controlWriter.WriteLineAsync("550 not found"); return; }
        await controlWriter.WriteLineAsync("150 opening data connection");
        using var data = await dataListener!.AcceptTcpClientAsync(stop.Token);
        await using var dataStream = data.GetStream();
        if (RetrDelayMs > 0) await Task.Delay(RetrDelayMs, stop.Token);
        var count = FailTransfer || TruncateSuccessfulRetr ? Math.Min(2, payload.Length) : payload.Length;
        await dataStream.WriteAsync(payload.AsMemory(0, count), stop.Token);
        await dataStream.FlushAsync(stop.Token);
        Downloads++;
        if (RetrFinalReplyDelayMs > 0)
        {
            data.Close();
            RetrAwaitingFinalReply.TrySetResult();
            await Task.Delay(RetrFinalReplyDelayMs, stop.Token);
        }
        await controlWriter.WriteLineAsync(FailTransfer ? "426 connection closed mid-transfer" : "226 transfer complete");
    }

    private async Task ServeStorAsync(StreamWriter controlWriter, string path)
    {
        await controlWriter.WriteLineAsync("150 ready to receive");
        using var data = await dataListener!.AcceptTcpClientAsync(stop.Token);
        await using var dataStream = data.GetStream();
        using var received = new MemoryStream(); var buffer = new byte[65536]; int count;
        while ((count = await dataStream.ReadAsync(buffer, stop.Token)) > 0) received.Write(buffer, 0, count);
        Files[path] = received.ToArray(); Uploads++;
        AfterUpload?.Invoke(path);
        await controlWriter.WriteLineAsync("226 transfer complete");
    }

    private static string MachineLine(string path, bool isDirectory, long size) =>
        (isDirectory ? "type=dir;modify=20260101000000; " : $"type=file;size={size};modify=20260101000000; ") + Path.GetFileName(path);
    private static string UnixLine(string path, bool isDirectory, long size) =>
        (isDirectory ? "drwxr-xr-x 2 root root 4096 Jan 01  2026 " : $"-rw-rw-r-- 1 root root {size} Jan 01  2026 ") + Path.GetFileName(path);
    private static string DosLine(string path, bool isDirectory, long size) =>
        $"01-01-26  01:00PM " + (isDirectory ? $"<DIR> {Path.GetFileName(path)}" : $"{size} {Path.GetFileName(path)}");

    private static string ParentOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? "/" : path[..cut];
    }

    public void Dispose()
    {
        stop.Cancel(); control.Stop(); dataListener?.Stop();
        try { worker?.GetAwaiter().GetResult(); } catch { }
        stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
