using System.Text.Json;

namespace SwitchDesk.Core;

public enum NcaHashPolicy { Strict, Warn }
public enum NcaSignaturePolicy { Strict, Warn }

public sealed record InstallWarning(string Code, string Entry, string ExpectedContentId, string ActualSha256,
    string? HeaderSha256 = null, string? VerificationResult = null)
{
    public string Message => Code == "NcaHeaderSignatureMismatch"
        ? $"NCA 头签名校验未通过：{Entry} · 头 SHA-256 {HeaderSha256} · 验证 Result={VerificationResult}"
        : $"NCA 摘要差异：{Entry} · 原内容 ID {ExpectedContentId} · 实际 SHA-256 {ActualSha256}";
}

public sealed record InstallResult(InstallWarning[] Warnings)
{
    internal static InstallResult Read(JsonElement reply, NcaHashPolicy policy, NcaSignaturePolicy signaturePolicy = NcaSignaturePolicy.Strict)
    {
        if (reply.ValueKind != JsonValueKind.Object) throw new InvalidDataException("安装结果格式无效");
        if (!reply.TryGetProperty("warnings", out var warnings))
        {
            if (policy == NcaHashPolicy.Warn || signaturePolicy == NcaSignaturePolicy.Warn)
                throw new InvalidDataException("配套没有返回兼容安装的校验结果，请核对配套版本");
            return new([]); // Older companions return an empty object for strict installs.
        }
        if (warnings.ValueKind != JsonValueKind.Array || warnings.GetArrayLength() > 4096)
            throw new InvalidDataException("安装警告列表格式无效");
        var result = new List<InstallWarning>();
        foreach (var row in warnings.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !Text(row, "code", out var code)
                || !Text(row, "entry", out var entry) || entry.Length is < 1 or > 1024 || entry.Any(char.IsControl))
                throw new InvalidDataException("配套返回了无效的 NCA 校验警告");
            if (code == "NcaHashMismatch")
            {
                if (!Text(row, "expectedContentId", out var expected) || !Hex(expected, 32)
                    || !Text(row, "actualSha256", out var actual) || !Hex(actual, 64)
                    || actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("配套返回了无效的 NCA 摘要警告");
                result.Add(new(code, entry, expected.ToUpperInvariant(), actual.ToUpperInvariant()));
            }
            else if (code == "NcaHeaderSignatureMismatch")
            {
                if (!Text(row, "headerSha256", out var headerSha256) || !Hex(headerSha256, 64)
                    || !Text(row, "verificationResult", out var verificationResult) || !Hex(verificationResult, 16)
                    || !verificationResult.Equals("00000000000081F9", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("配套返回了无效的 NCA 头签名警告");
                result.Add(new(code, entry, "", "", headerSha256.ToUpperInvariant(), verificationResult.ToUpperInvariant()));
            }
            else throw new InvalidDataException("配套返回了未知的 NCA 校验警告");
        }
        if (policy == NcaHashPolicy.Strict && result.Any(warning => warning.Code == "NcaHashMismatch"))
            throw new DeviceException("PackageHashMismatch", "严格安装收到 NCA 摘要差异，请核对配套版本与实际安装状态");
        if (signaturePolicy == NcaSignaturePolicy.Strict && result.Any(warning => warning.Code == "NcaHeaderSignatureMismatch"))
            throw new DeviceException("PackageSignatureMismatch", "严格安装收到 NCA 头签名校验失败提示，请核对配套版本与实际安装状态");
        return new(result.ToArray());
    }

    private static bool Text(JsonElement row, string name, out string value)
    {
        value = "";
        if (!row.TryGetProperty(name, out var text) || text.ValueKind != JsonValueKind.String) return false;
        value = text.GetString()!;
        return true;
    }
    private static bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
}
