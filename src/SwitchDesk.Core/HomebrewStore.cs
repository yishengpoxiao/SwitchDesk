using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace SwitchDesk.Core;

public sealed record HomebrewApp(string Name, string Author, string Version, string Description, string DownloadUrl, string FileName)
{
    public string? Sha256 { get; init; }
    public string Summary => string.IsNullOrEmpty(Author) ? Name : $"{Name} · {Author}";
}

/// <summary>
/// Homebrew 应用目录：只收录社区免费开源软件（用户配置索引 URL 的 JSON，
/// 兼容 switchbru/hbappstore 风格字段 name/author/version/details/dlurl|url）。
/// 不涉及任何商业 ROM 分发；安装目标固定为 SD 卡 /switch/&lt;名称&gt;/。
/// </summary>
public static class HomebrewStore
{
    public const long MaximumIndexBytes = 4 * 1024 * 1024;
    public const long MaximumAppBytes = 512 * 1024 * 1024;

    public static async Task<HomebrewApp[]> FetchIndexAsync(HttpClient http, string indexUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(indexUrl)) throw new InvalidDataException("请先在设置中填写 Homebrew 索引地址");
        if (!Uri.TryCreate(indexUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidDataException("Homebrew 索引地址必须是 http(s) URL");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange != null)
            throw new InvalidDataException("Homebrew 索引需要完整响应，不能使用部分范围");
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaximumIndexBytes) throw new InvalidDataException("Homebrew 索引超过 4 MiB 解析预算");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await StreamReads.WithInactivityTimeoutAsync(stream, buffer, ct)) > 0)
        { if (output.Length + read > MaximumIndexBytes) throw new InvalidDataException("Homebrew 索引超过 4 MiB 解析预算"); output.Write(buffer, 0, read); }
        if (declaredLength.HasValue && output.Length != declaredLength.Value)
            throw new InvalidDataException("Homebrew 索引下载长度与响应声明不符");
        return ParseIndex(output.ToArray());
    }

    public static HomebrewApp[] ParseIndex(byte[] json)
    {
        if (json.Length == 0 || json.Length > MaximumIndexBytes) throw new InvalidDataException("Homebrew 索引长度无效");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root :
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("list", out var nested) ? nested :
            throw new InvalidDataException("Homebrew 索引需要 list 数组或顶层数组");
        if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Homebrew 索引需要 list 数组或顶层数组");
        var result = new List<HomebrewApp>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string Text(params string[] keys) => keys.Select(k => item.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
            var name = Text("name", "title");
            var url = Text("dlurl", "url", "download");
            if (name.Length == 0 || url.Length == 0) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https")) continue;
            var fileName = Uri.UnescapeDataString(target.Segments[^1]);
            // ZIP distributions need extraction and may contain assets. Renaming their bytes to .nro
            // creates an unusable application, so expose only the direct NRO format supported here.
            if (!Path.GetExtension(fileName).Equals(".nro", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha256 = null;
            if (item.TryGetProperty("sha256", out var digest))
            {
                if (digest.ValueKind != JsonValueKind.String || !Fingerprints.Valid(sha256 = digest.GetString())) throw new InvalidDataException("Homebrew 索引中的 SHA-256 格式无效");
            }
            result.Add(new(PathSafety.SafeName(name), Text("author", "by"), Text("version", "ver"), Text("details", "description", "desc"), url, fileName) { Sha256 = sha256 });
        }
        if (result.Count == 0) throw new InvalidDataException("Homebrew 索引没有可直接下载的 NRO 条目；ZIP 等分发包暂不支持");
        return [.. result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static async Task<byte[]> DownloadAsync(HttpClient http, HomebrewApp app, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        if (!Uri.TryCreate(app.DownloadUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            !Path.GetExtension(Uri.UnescapeDataString(url.AbsolutePath)).Equals(".nro", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Homebrew 当前只支持直接下载 NRO，不支持 ZIP 等分发包");
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var declaredLength = response.Content.Headers.ContentLength; var total = declaredLength ?? 0;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange != null)
            throw new InvalidDataException("Homebrew 下载需要完整文件，来源返回了部分内容或无效的成功响应");
        if (total > MaximumAppBytes) throw new InvalidDataException("Homebrew 包超过 512 MiB 上限");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[81920]; long completed = 0; int read;
        while ((read = await StreamReads.WithInactivityTimeoutAsync(stream, buffer, ct)) > 0)
        {
            if (output.Length + read > MaximumAppBytes) throw new InvalidDataException("Homebrew 包超过 512 MiB 上限");
            output.Write(buffer, 0, read); completed += read;
            progress?.Report(new(completed, total, "下载 " + app.Name));
        }
        if (declaredLength.HasValue && completed != declaredLength.Value) throw new InvalidDataException("Homebrew 下载长度与响应声明不符");
        return output.ToArray();
    }

    public static string InstallPath(string appName, string fileName)
    {
        var leaf = Path.GetFileName(fileName);
        var extension = Path.GetExtension(leaf);
        if (!extension.Equals(".nro", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Homebrew 安装文件必须使用 .nro 扩展名");
        var safeName = PathSafety.SafeName(leaf);
        // SafeName may truncate a long filename before its extension.
        if (!safeName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            var length = Math.Min(safeName.Length, 120 - extension.Length);
            if (length > 0 && char.IsHighSurrogate(safeName[length - 1])) length--;
            safeName = (length == 0 ? "_" : safeName[..length]) + extension;
        }
        var path = "/switch/" + PathSafety.SafeName(appName) + "/" + safeName;
        PathSafety.DemandWritable(path);
        return path;
    }

    /// <summary>安装到 SD 卡 /switch/&lt;名称&gt;/&lt;文件名&gt;；如索引提供 sha256 字段则先核对。</summary>
    public static async Task<string> InstallAsync(DeviceClient client, HomebrewApp app, byte[] payload, string? expectedSha256, IProgress<TransferProgress>? progress, CancellationToken ct = default)
    {
        if (payload.LongLength > MaximumAppBytes || payload.Length < 0x80 || !payload.AsSpan(0x10, 4).SequenceEqual("NRO0"u8))
            throw new InvalidDataException("Homebrew 文件超过大小预算或缺少有效的 NRO 文件头，未写入主机");
        // The normal UI call has no explicit digest; preserve the validator supplied by the index.
        expectedSha256 ??= app.Sha256;
        if (expectedSha256 != null)
        {
            if (!Fingerprints.Valid(expectedSha256)) throw new InvalidDataException("Homebrew 包的 SHA-256 格式无效");
            if (!Fingerprints.Equal(Convert.ToHexString(SHA256.HashData(payload)), expectedSha256))
                throw new InvalidDataException("Homebrew 包 SHA-256 与索引不符");
        }
        var path = InstallPath(app.Name, app.FileName);
        var directory = path[..path.LastIndexOf('/')];
        var exists = await client.QueryAsync<JsonElement>("fs.stat", new { path = directory }, ct);
        if (!exists.GetProperty("exists").GetBoolean()) await client.CallAsync("fs.mkdir", new { path = directory }, ct);
        var open = await client.QueryAsync<JsonElement>("fs.write.open", new { path, size = payload.Length }, ct);
        var handle = open.GetProperty("handle").GetString()!;
        try
        {
            long offset = 0;
            while (offset < payload.Length)
            {
                var count = Math.Min(Wire.ChunkSize, payload.Length - offset);
                await client.CallAsync("write.chunk", new { handle, path = "", offset }, ct, upload: payload.AsSpan((int)offset, (int)count).ToArray());
                offset += count; progress?.Report(new(offset, payload.Length, "写入 " + path));
            }
            await client.CallAsync("fs.write.commit", new { handle }, ct);
        }
        finally { await Transfers.CloseAsync(client, handle); }
        return path;
    }
}
