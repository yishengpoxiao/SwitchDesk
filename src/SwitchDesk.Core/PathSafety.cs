namespace SwitchDesk.Core;

public static class PathSafety
{
    public static string Remote(string path)
    {
        if (path.Contains('\\') || path.Contains('\0') || !path.StartsWith('/') || path.Contains(':'))
            throw new InvalidDataException("主机路径格式错误");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(p => p is "." or "..")) throw new InvalidDataException("路径不允许包含相对跳转");
        var result = "/" + string.Join('/', segments);
        if (System.Text.Encoding.UTF8.GetByteCount(result) > 700) throw new InvalidDataException("主机路径过长");
        return result;
    }
    public static void DemandWritable(string path, bool allowCheatFiles = false, bool protectDescendants = false)
    {
        path = Remote(path);
        if (allowCheatFiles && IsCheatPath(path)) return;
        string[] protectedRoots = ["/Nintendo", "/emuMMC", "/atmosphere", "/bootloader", "/switch/SwitchDesk", "/config/SwitchDesk"];
        string[] protectedFiles = ["/exosphere.ini", "/hekate_ipl.ini", "/hbmenu.nro", "/boot.dat", "/boot.ini"];
        if (path == "/" || protectedRoots.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase) ||
            (protectDescendants && p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) ||
            protectedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) throw new DeviceException("ProtectedPath", "该目录由系统或配套程序使用，禁止通过文件管理修改");
    }
    /// <summary>仅放行固定游戏及构建 ID 的 .txt 金手指和旧 .cheat 文件；其他系统路径仍受保护。</summary>
    public static bool IsCheatPath(string path) => IsCheatFile(path) || IsCheatDirectory(path);
    public static bool IsCheatFile(string path)
    {
        var segments = path.Split('/');
        if (segments.Length != 6 || !segments[1].Equals("atmosphere", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("contents", StringComparison.OrdinalIgnoreCase) || !segments[4].Equals("cheats", StringComparison.OrdinalIgnoreCase)) return false;
        var titleId = segments[3]; var file = segments[5];
        var disabled = file.EndsWith(".off", StringComparison.OrdinalIgnoreCase);
        var stem = disabled ? file[..^4] : file;
        return titleId.Length == 16 && titleId.All(Uri.IsHexDigit) &&
            ((stem.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && stem.Length == 20) ||
             (stem.EndsWith(".cheat", StringComparison.OrdinalIgnoreCase) && stem.Length == 22)) &&
            stem[..16].All(Uri.IsHexDigit);
    }
    public static bool IsCheatDirectory(string path)
    {
        var segments = path.Split('/');
        if (segments.Length < 4 || !segments[1].Equals("atmosphere", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("contents", StringComparison.OrdinalIgnoreCase) || segments[3].Length != 16 || !segments[3].All(Uri.IsHexDigit)) return false;
        return segments.Length == 4 || (segments.Length == 5 && segments[4].Equals("cheats", StringComparison.OrdinalIgnoreCase));
    }
    public static string Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) throw new InvalidDataException("无效的备份文件路径");
        if (path.Split('/').Any(p => string.IsNullOrEmpty(p) || p is "." or "..")) throw new InvalidDataException("备份路径越界");
        return path;
    }
    public static string SafeName(string value, int maximumLength = 120, int maximumUtf8Bytes = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUtf8Bytes, 1);
        var name = string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return "_";
        if (ReservedName(name)) name = "_" + name;
        if (name.Length <= maximumLength && System.Text.Encoding.UTF8.GetByteCount(name) <= maximumUtf8Bytes) return name;
        // Count complete Unicode scalars: Windows limits UTF-16 units while Unix
        // filesystems may limit UTF-8 bytes in the same filename component.
        var length = 0; var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Utf16SequenceLength > maximumLength - length || rune.Utf8SequenceLength > maximumUtf8Bytes - bytes) break;
            length += rune.Utf16SequenceLength; bytes += rune.Utf8SequenceLength;
        }
        name = name[..length].TrimEnd('.', ' ');
        return name.Length == 0 ? "_" : name;
    }
    private static bool ReservedName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9';
    }
    public static string Child(string root, string relative)
    {
        Relative(relative); var fullRoot = Path.GetFullPath(root);
        if (relative.Split('/').Any(name => name.EndsWith('.') || name.EndsWith(' ') || ReservedName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("主机文件名不能安全地保存到 Windows：" + relative);
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本地路径越界");
        return target;
    }
}
