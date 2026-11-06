using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwitchDesk.Core;

public enum ResourceKind { Game, Homebrew, Cheat }

/// <summary>A downloadable file and the metadata supplied by its source, before device inspection.</summary>
public sealed record ResourceEntry(string Name, ResourceKind Kind, string DownloadUrl, string FileName)
{
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
    public string? TitleId { get; init; }
    public string? BuildId { get; init; }
    public string? SourcePage { get; init; }
    public string SourceName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Id => Fingerprints.Of(Encoding.UTF8.GetBytes(string.Join('\0', Kind.ToString(), DownloadUrl, FileName, Sha256, TitleId, BuildId)));
}

/// <summary>Local download receipt. A calculated digest alone is not a source authenticity check.</summary>
public sealed record ResourceDownload(ResourceEntry Entry, string Path, long Size, string Sha256,
    bool ExpectedVerified, DateTimeOffset DownloadedAt)
{
    // Keep the original inspected-content contract across application restarts.
    // These are absent on generic direct links and on older download receipts.
    public ContentEntry? ExpectedContent { get; init; }
    public string? ContentSourceId { get; init; }
}

public static class ResourceIndex
{
    public const int MaximumIndexBytes = 4 * 1024 * 1024;
    public static long MaximumBytes(ResourceKind kind) => kind switch
    {
        ResourceKind.Game => 128L * 1024 * 1024 * 1024,
        ResourceKind.Homebrew => 512L * 1024 * 1024,
        ResourceKind.Cheat => 1024 * 1024,
        _ => throw new InvalidDataException("资源类型无效")
    };

    public static ResourceEntry Validate(ResourceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var maximum = MaximumBytes(entry.Kind);
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 300 || entry.Name.Any(char.IsControl))
            throw new InvalidDataException("资源名称为空、过长或包含控制字符");
        var url = HttpUrl(entry.DownloadUrl);
        var remoteName = Uri.UnescapeDataString(url.Segments[^1]);
        var name = string.IsNullOrEmpty(entry.FileName) ? remoteName : entry.FileName;
        if (name.Length is 0 or > 180 || name.Any(char.IsControl) || name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
            throw new InvalidDataException("资源文件名无效；请选择单个文件名");
        _ = PathSafety.Child(System.IO.Path.GetTempPath(), name);
        var extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
        var remoteExtension = System.IO.Path.GetExtension(remoteName).ToLowerInvariant();
        var supported = entry.Kind switch
        {
            ResourceKind.Game => extension is ".nsp" or ".xci" or ".nsz" or ".xcz",
            ResourceKind.Homebrew => extension == ".nro",
            ResourceKind.Cheat => extension is ".cheat" or ".txt",
            _ => false
        };
        if (!supported || remoteExtension != extension)
            throw new InvalidDataException("资源地址与文件类型不匹配；请选择游戏包、NRO 或金手指文本直链");
        if (entry.Sha256 != null && !Fingerprints.Valid(entry.Sha256)) throw new InvalidDataException("资源 SHA-256 应为 64 位十六进制");
        if (entry.Size.HasValue && (entry.Size <= 0 || entry.Size > maximum)) throw new InvalidDataException("资源声明长度超过此类型上限或为空");
        if (entry.TitleId != null && !ContentCatalog.ValidId(entry.TitleId)) throw new InvalidDataException("资源 TitleID 应为 16 位十六进制");
        if (entry.BuildId != null && !ContentCatalog.ValidId(entry.BuildId)) throw new InvalidDataException("金手指构建 ID 应为 16 位十六进制");
        if ((entry.SourceName?.Length ?? 0) > 200 || (entry.Description?.Length ?? 0) > 4000) throw new InvalidDataException("资源来源或说明过长");
        if (entry.SourcePage != null) _ = HttpUrl(entry.SourcePage);
        return entry with
        {
            Name = entry.Name.Trim(), DownloadUrl = url.AbsoluteUri, FileName = name,
            Sha256 = entry.Sha256?.ToUpperInvariant(), TitleId = entry.TitleId?.ToUpperInvariant(), BuildId = entry.BuildId?.ToUpperInvariant(),
            SourceName = entry.SourceName ?? "", Description = entry.Description ?? ""
        };
    }

    public static ResourceEntry[] Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 or > MaximumIndexBytes) throw new InvalidDataException("资源索引为空或超过 4 MiB");
        LocalSafety.DemandJsonObject(json);
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (!root.TryGetProperty("formatVersion", out var format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out var version) || version != 1 ||
            !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 10000)
            throw new InvalidDataException("资源索引需要 formatVersion: 1 与 entries 数组（最多 10000 项）");
        var result = new List<ResourceEntry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in entries.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("资源索引条目必须是对象");
            string? Optional(string key)
            {
                if (!row.TryGetProperty(key, out var field) || field.ValueKind == JsonValueKind.Null) return null;
                if (field.ValueKind != JsonValueKind.String) throw new InvalidDataException("资源索引字段类型错误：" + key);
                return field.GetString();
            }
            var kindText = Optional("kind");
            var kind = kindText?.ToLowerInvariant() switch
            {
                "game" => ResourceKind.Game, "homebrew" => ResourceKind.Homebrew, "cheat" => ResourceKind.Cheat,
                _ => throw new InvalidDataException("资源 kind 必须明确为 Game、Homebrew 或 Cheat")
            };
            long? size = null;
            if (row.TryGetProperty("size", out var field) && field.ValueKind != JsonValueKind.Null)
                size = field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var parsed) ? parsed : throw new InvalidDataException("资源 size 必须是整数");
            var entry = Validate(new(Optional("name") ?? "", kind, Optional("url") ?? "", Optional("fileName") ?? "")
            {
                Size = size, Sha256 = Optional("sha256"), TitleId = Optional("titleId"), BuildId = Optional("buildId"),
                SourcePage = Optional("sourcePage"), SourceName = Optional("sourceName") ?? "", Description = Optional("description") ?? ""
            });
            if (!seen.Add(entry.Id)) throw new InvalidDataException("资源索引包含重复条目");
            result.Add(entry);
        }
        return result.ToArray();
    }

    public static async Task<ResourceEntry[]> FetchAsync(HttpClient http, string url, CancellationToken ct)
    {
        var uri = HttpUrl(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3");
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct); headers.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IOException("资源索引连接超时", ex); }
        using (response)
        {
            DemandResponse(response, uri);
            if (response.Content.Headers.ContentLength > MaximumIndexBytes) throw new InvalidDataException("资源索引超过 4 MiB");
            await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream();
            var buffer = new byte[81920]; int read;
            while ((read = await StreamReads.WithInactivityTimeoutAsync(input, buffer, ct)) > 0)
            {
                if (output.Length + read > MaximumIndexBytes) throw new InvalidDataException("资源索引超过 4 MiB");
                output.Write(buffer, 0, read);
            }
            if (response.Content.Headers.ContentLength is {} declared && output.Length != declared) throw new InvalidDataException("资源索引下载长度与响应声明不一致");
            return Parse(output.ToArray());
        }
    }

    internal static Uri HttpUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            throw new InvalidDataException("资源地址必须是无嵌入凭据、无片段的 HTTP(S) 地址");
        return url;
    }

    internal static void DemandResponse(HttpResponseMessage response, Uri requested)
    {
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is {} actual && actual.AbsoluteUri != requested.AbsoluteUri)
            throw new IOException("来源返回跳转，请使用最终文件直链或在原站浏览器中完成下载");
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange != null)
            throw new InvalidDataException("资源下载需要完整文件响应，不能使用部分范围");
        if (response.Content.Headers.ContentEncoding.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("资源文件响应不能使用内容编码");
        var media = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (media is "text/html" or "application/xhtml+xml") throw new InvalidDataException("来源返回网页；请在原站获取可用文件直链");
    }
}

/// <summary>Downloads to a PC staging directory only. Device installation is a separate confirmed operation.</summary>
public sealed class ResourceDownloads : IDisposable
{
    private readonly HttpClient http;
    private readonly TimeSpan inactivity;
    public ResourceDownloads() : this(new HttpClient(new SocketsHttpHandler
        { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan }) { }
    public ResourceDownloads(HttpClient client, TimeSpan? inactivityTimeout = null)
    {
        http = client; inactivity = inactivityTimeout ?? TimeSpan.FromSeconds(30);
        if (inactivity <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
    }

    public async Task<ResourceDownload> DownloadAsync(ResourceEntry entry, string directory, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); entry = ResourceIndex.Validate(entry);
        var fullDirectory = System.IO.Path.GetFullPath(directory);
        for (var ancestor = fullDirectory; ancestor != null; ancestor = System.IO.Path.GetDirectoryName(ancestor))
            if (Directory.Exists(ancestor)) { LocalSafety.DemandDirectory(ancestor); break; }
        Directory.CreateDirectory(fullDirectory); LocalSafety.DemandDirectory(fullDirectory);
        var destination = PathSafety.Child(fullDirectory, entry.FileName);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标文件已存在，请更换资源文件名或下载目录");
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        using var request = new HttpRequestMessage(HttpMethod.Get, entry.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("SwitchDesk/0.3"); request.Headers.AcceptEncoding.ParseAdd("identity");
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(ct); headers.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested) { throw new IOException("资源下载连接超时", ex); }
        using (response)
        {
            ResourceIndex.DemandResponse(response, new(entry.DownloadUrl));
            if (response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() is
                "application/json" or "text/json" or "application/javascript" or "text/javascript" or "application/xml" or "text/xml")
                throw new InvalidDataException("资源地址返回数据接口或脚本页面，请提供实际文件直链");
            var maximum = ResourceIndex.MaximumBytes(entry.Kind); var declared = response.Content.Headers.ContentLength;
            if (declared > maximum || declared == 0 || entry.Size.HasValue && declared.HasValue && entry.Size != declared)
                throw new InvalidDataException("资源响应长度为空、超限或与索引不一致");
            try
            {
                long completed = 0; string digest;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, Wire.ChunkSize, true))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[Wire.ChunkSize]; var leading = new byte[512]; var leadingCount = 0; int count;
                    while ((count = await StreamReads.WithInactivityTimeoutAsync(input, buffer, ct, inactivity)) > 0)
                    {
                        if (completed + count > maximum || entry.Size.HasValue && completed + count > entry.Size.Value || declared.HasValue && completed + count > declared.Value)
                            throw new InvalidDataException("资源内容超出允许或声明的长度");
                        var capture = Math.Min(count, leading.Length - leadingCount);
                        buffer.AsSpan(0, capture).CopyTo(leading.AsSpan(leadingCount)); leadingCount += capture;
                        if (capture > 0 && LooksLikePage(leading.AsSpan(0, leadingCount))) throw new InvalidDataException("资源直链返回网页而非文件");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct); hash.AppendData(buffer, 0, count); completed += count;
                        progress?.Report(new(completed, entry.Size ?? declared ?? 0, "下载到电脑 · " + entry.Name));
                    }
                    if (completed == 0 || entry.Size.HasValue && completed != entry.Size.Value || declared.HasValue && completed != declared.Value)
                        throw new InvalidDataException("资源内容为空或未完整下载");
                    digest = Convert.ToHexString(hash.GetHashAndReset());
                    if (entry.Sha256 != null && !Fingerprints.Equal(digest, entry.Sha256)) throw new InvalidDataException("资源 SHA-256 与来源不匹配，未保存为完成文件");
                    ct.ThrowIfCancellationRequested(); output.Flush(true);
                }
                ct.ThrowIfCancellationRequested(); File.Move(partial, destination);
                return new(entry, destination, completed, digest, entry.Sha256 != null, DateTimeOffset.UtcNow);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
    }

    public static async Task VerifyAsync(ResourceDownload download, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(download);
        ct.ThrowIfCancellationRequested();
        if (download.Size <= 0 || !Fingerprints.Valid(download.Sha256)) throw new InvalidDataException("资源下载记录的长度或摘要无效");
        // Imported local content sources do not have HTTP URLs. The local receipt,
        // rather than a network request or index re-parse, establishes this check.
        LocalSafety.DemandRegularFile(download.Path);
        await using var input = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, true);
        if (input.Length != download.Size || !Fingerprints.Equal(Convert.ToHexString(await SHA256.HashDataAsync(input, ct)), download.Sha256))
            throw new InvalidDataException("电脑资源文件已变化，请重新下载或核对后再安装");
    }

    private static bool LooksLikePage(ReadOnlySpan<byte> first)
    {
        var text = Encoding.UTF8.GetString(first[..Math.Min(first.Length, 512)]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return new[] { "<!doctype", "<html", "<head", "<body", "<script", "<?xml" }.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => http.Dispose();
}
