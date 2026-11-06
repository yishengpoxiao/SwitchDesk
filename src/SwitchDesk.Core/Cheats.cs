using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record CheatEntry(string FileName, string Path, string BuildId, bool Enabled, long Size, int SectionCount)
{ public string StateText => Enabled ? "启用" : "停用"; }

public sealed record CheatSection(string Name, string[] CodeLines)
{
    public bool IsMaster { get; init; }
}

/// <summary>
/// Atmosphère 金手指管理。写入标准 .txt 文件，仍可浏览与管理旧 .cheat 文件。
/// 这一严格形状（见 PathSafety.IsCheatPath）；Atmosphere 的其他内容依旧保持保护。
/// </summary>
public static class CheatManager
{
    public const long MaximumBytes = 1024 * 1024;
    private const string CheatRoot = "/atmosphere/contents/";

    public static string CheatPath(string titleId, string buildId, bool enabled = true)
    {
        RequireTitleId(titleId); RequireBuildId(buildId);
        return $"{CheatRoot}{titleId.ToUpperInvariant()}/cheats/{buildId.ToLowerInvariant()}.txt{(enabled ? "" : ".off")}";
    }
    public static void RequireTitleId(string titleId)
    { if (titleId.Length != 16 || !titleId.All(Uri.IsHexDigit)) throw new InvalidDataException("游戏 TitleID 应为 16 位十六进制"); }
    public static void RequireBuildId(string buildId)
    { if (buildId.Length != 16 || !buildId.All(Uri.IsHexDigit)) throw new InvalidDataException("金手指构建 ID 应为 16 位十六进制"); }

    /// <summary>解析普通 [名称] 与主代码 {名称}；空白不结束小节，接受 BOM 与导入说明注释。</summary>
    public static CheatSection[] Parse(string text, long sizeBudget = MaximumBytes)
    {
        text = text.TrimStart('\uFEFF');
        if (text.Length > sizeBudget) throw new InvalidDataException("金手指文件超过解析预算");
        var sections = new List<CheatSection>();
        string? name = null; var codes = new List<string>(); var master = false;
        foreach (var raw in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith(';')) continue;
            if ((line.StartsWith('[') && line.EndsWith(']')) || (line.StartsWith('{') && line.EndsWith('}')))
            {
                AppendSection(sections, name, codes, master); codes.Clear();
                master = line[0] == '{';
                name = line[1..^1].Trim();
                if (name.Length == 0 || name.Length > 120 || name.IndexOfAny(['[', ']', '{', '}']) >= 0) throw new InvalidDataException("金手指小节名称无效");
            }
            else
            {
                var commentAt = line.IndexOfAny(['#', ';']);
                var code = (commentAt >= 0 ? line[..commentAt] : line).Trim();
                if (code.Length == 0) continue;
                if (!IsValidCodeLine(code)) throw new InvalidDataException("金手指代码行无效：" + code[..Math.Min(code.Length, 48)]);
                codes.Add(code);
            }
        }
        AppendSection(sections, name, codes, master);
        if (sections.Count == 0) throw new InvalidDataException("金手指文件没有可识别的小节");
        return [.. sections];
    }
    private static void AppendSection(List<CheatSection> sections, string? name, List<string> codes, bool master)
    {
        if (name != null || codes.Count > 0)
        {
            if (name == null || codes.Count == 0) throw new InvalidDataException("金手指小节缺少名称或代码");
            if (master && sections.Any(s => s.IsMaster)) throw new InvalidDataException("金手指文件只能包含一个主代码小节");
            sections.Add(new(name, [.. codes]) { IsMaster = master });
        }
    }
    /// <summary>检查每行 1 至 4 个 32 位十六进制字；不执行或推断指令效果。</summary>
    private static bool IsValidCodeLine(string line)
    {
        var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length is >= 1 and <= 4 && tokens.All(token => token.Length == 8 && token.All(Uri.IsHexDigit));
    }

    public static async Task<CheatEntry[]> ListAsync(DeviceClient client, string titleId, CancellationToken ct = default)
    {
        RequireTitleId(titleId);
        var directory = $"{CheatRoot}{titleId.ToUpperInvariant()}/cheats";
        var state = await client.QueryAsync<JsonElement>("fs.stat", new { path = directory }, ct);
        if (!state.GetProperty("exists").GetBoolean()) return [];
        if (!state.GetProperty("isDirectory").GetBoolean()) throw new InvalidDataException("金手指路径被文件占用：" + directory);
        var entries = await client.QueryAsync<RemoteEntry[]>("fs.list", new { path = directory }, ct);
        var result = new List<CheatEntry>();
        foreach (var entry in entries.Where(e => !e.IsDirectory && PathSafety.IsCheatFile(directory + "/" + e.Name)))
        {
            var stem = entry.Name.EndsWith(".cheat.off", StringComparison.OrdinalIgnoreCase) ? entry.Name[..^4] : entry.Name;
            result.Add(new(entry.Name, directory + "/" + entry.Name, stem[..16].ToLowerInvariant(), !entry.Name.EndsWith(".off", StringComparison.OrdinalIgnoreCase), entry.Size, 0));
        }
        return [.. result.OrderBy(e => e.BuildId, StringComparer.Ordinal)];
    }

    public static async Task InstallAsync(DeviceClient client, string titleId, byte[] content, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        if (content.Length == 0 || content.Length > MaximumBytes) throw new InvalidDataException("金手指文件大小无效");
        var text = System.Text.Encoding.UTF8.GetString(content);
        var sections = Parse(text);
        var buildId = BuildIdOf(sections) ?? throw new InvalidDataException("无法从内容确定构建 ID，请提供文件对应的构建 ID");
        var path = CheatPath(titleId, buildId);
        await UploadAsync(client, path, CanonicalText(sections), $"安装金手指 · {sections.Length} 个小节", progress, ct);
    }
    public static async Task InstallAsync(DeviceClient client, string titleId, string buildId, byte[] content, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        if (content.Length == 0 || content.Length > MaximumBytes) throw new InvalidDataException("金手指文件大小无效");
        var sections = Parse(System.Text.Encoding.UTF8.GetString(content));
        await UploadAsync(client, CheatPath(titleId, buildId), CanonicalText(sections), "安装金手指", progress, ct);
    }

    public static async Task SetEnabledAsync(DeviceClient client, string path, bool enable, CancellationToken ct = default)
    {
        PathSafety.DemandWritable(path, allowCheatFiles: true);
        var disabled = path.EndsWith(".off", StringComparison.OrdinalIgnoreCase);
        if (enable == !disabled) return; // 已是目标状态
        var target = enable ? path[..^4] : path + ".off";
        PathSafety.DemandWritable(target, allowCheatFiles: true);
        await client.CallAsync("fs.rename", new { path, target }, ct);
    }
    public static async Task DeleteAsync(DeviceClient client, string path, CancellationToken ct = default)
    {
        PathSafety.DemandWritable(path, allowCheatFiles: true);
        await client.CallAsync("fs.delete", new { path }, ct);
    }

    /// <summary>导入以构建 ID 命名的 .txt 或旧 .cheat，目标统一采用 Atmosphère .txt。</summary>
    public static async Task<int> InstallFolderAsync(DeviceClient client, string titleId, string directory, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        var installed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(name);
            if ((!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cheat", StringComparison.OrdinalIgnoreCase)) ||
                Path.GetFileNameWithoutExtension(name).Length != 16 || name[..16].Any(c => !Uri.IsHexDigit(c))) continue;
            if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("金手指文件超过解析预算");
            await InstallAsync(client, titleId, name[..16], await File.ReadAllBytesAsync(path, ct), progress, ct);
            installed++;
        }
        return installed;
    }

    private static byte[] CanonicalText(CheatSection[] sections)
    {
        // dmnt accepts whitespace, [ordinary] and {master} names, and hex words.
        // Input-only BOM/comments must never be copied to its parser unchanged.
        var lines = sections.SelectMany(section => new[] { (section.IsMaster ? "{" : "[") + section.Name + (section.IsMaster ? "}" : "]") }
            .Concat(section.CodeLines.Select(code => string.Join(' ', code.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)))));
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        if (bytes.LongLength > MaximumBytes) throw new InvalidDataException("规范化后的金手指文件超过写入预算");
        return bytes;
    }
    private static string? BuildIdOf(CheatSection[] sections)
    {
        foreach (var section in sections.Take(3))
        {
            var head = section.Name.Split('.')[0].Trim();
            if (head.Length == 16 && head.All(Uri.IsHexDigit)) return head;
        }
        return null;
    }
    private static async Task UploadAsync(DeviceClient client, string path, byte[] content, string stage, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        PathSafety.DemandWritable(path, allowCheatFiles: true);
        // 设备端不会自动创建父目录：按严格形状补建 /atmosphere/contents/<title>[/cheats]。
        var segments = path.Split('/');
        var titleDirectory = $"/atmosphere/contents/{segments[3]}";
        await EnsureDirectoryAsync(client, titleDirectory, ct);
        await EnsureDirectoryAsync(client, titleDirectory + "/cheats", ct);
        var open = await client.QueryAsync<JsonElement>("fs.write.open", new { path, size = content.Length }, ct);
        var handle = open.GetProperty("handle").GetString()!;
        try
        {
            long offset = 0;
            while (offset < content.Length)
            {
                var count = Math.Min(Wire.ChunkSize, content.Length - offset);
                await client.CallAsync("write.chunk", new { handle, path = "", offset }, ct, upload: content.AsSpan((int)offset, (int)count).ToArray());
                offset += count; progress?.Report(new(offset, content.Length, stage));
            }
            await client.CallAsync("fs.write.commit", new { handle }, ct);
        }
        finally { await Transfers.CloseAsync(client, handle); }
    }
    private static async Task EnsureDirectoryAsync(DeviceClient client, string path, CancellationToken ct)
    {
        PathSafety.DemandWritable(path, allowCheatFiles: true);
        var entry = await client.QueryAsync<JsonElement>("fs.stat", new { path }, ct);
        if (entry.GetProperty("exists").GetBoolean())
        {
            if (!entry.GetProperty("isDirectory").GetBoolean()) throw new IOException("目标名称已被文件使用：" + path);
            return;
        }
        await client.CallAsync("fs.mkdir", new { path }, ct);
    }
}
