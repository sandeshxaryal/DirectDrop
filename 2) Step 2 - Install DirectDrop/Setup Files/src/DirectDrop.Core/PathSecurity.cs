namespace DirectDrop.Core;

/// <summary>
/// Every filesystem path that comes from the network (an iPhone's browser) is
/// untrusted input. This class is the single choke point that turns an
/// untrusted relative path into a safe absolute path, or throws.
///
/// The rule is simple and deliberately conservative: after resolving "..",
/// the final absolute path MUST still be physically inside the configured
/// root directory (the DirectDrop "Shared" folder, or the configured upload
/// destination). If it is not, we refuse.
///
/// Both '/' and '\' are always treated as path separators here, regardless
/// of which OS is actually running the code. DirectDrop only ever ships on
/// Windows, where '\' is the native separator - but treating '/' as a
/// separator too (browsers, and this code's own tests, use '/' freely) and
/// normalizing explicitly, rather than leaning on Path.DirectorySeparatorChar
/// or Path.GetInvalidFileNameChars() (both of which quietly change behaviour
/// depending on the OS the code happens to be running on), means the exact
/// same input is judged the exact same way every time. That is also what
/// lets DirectDrop.Tests exercise this class meaningfully from Linux/macOS
/// CI and trust the result applies to the real, Windows-only deployment.
/// </summary>
public static class PathSecurity
{
    private static readonly char[] WindowsInvalidFileNameChars =
    {
        '"', '<', '>', '|', '\0', (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7,
        (char)8, (char)9, (char)10, (char)11, (char)12, (char)13, (char)14, (char)15, (char)16,
        (char)17, (char)18, (char)19, (char)20, (char)21, (char)22, (char)23, (char)24, (char)25,
        (char)26, (char)27, (char)28, (char)29, (char)30, (char)31, ':', '*', '?', '\\', '/',
    };

    private static readonly string[] WindowsReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="rootDirectory"/>
    /// and guarantees the result is contained within that root.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when the resolved path would escape the root (e.g. "../../secret.txt",
    /// an absolute path, or a path containing a drive change).
    /// </exception>
    public static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new UnauthorizedAccessException("An empty path was supplied.");

        // Reject drive letters and UNC prefixes outright, before any
        // normalization - Combine() would otherwise happily ignore the root
        // and use the absolute path as-is.
        if (relativePath.Contains(':') || relativePath.StartsWith("\\\\") || relativePath.StartsWith("//"))
            throw new UnauthorizedAccessException("Absolute paths are not allowed.");

        string root = NormalizeRoot(rootDirectory);

        // Canonicalize separators ourselves rather than trusting
        // Path.DirectorySeparatorChar, so "\" and "/" are both always
        // recognized as separators no matter which OS is running this code.
        string canonicalRelative = relativePath.Replace('\\', '/');

        if (canonicalRelative.StartsWith('/'))
            throw new UnauthorizedAccessException("Absolute paths are not allowed.");

        string[] segments = canonicalRelative.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Resolve "." and ".." ourselves against an explicit stack, rather
        // than handing a string with ".." in it to Path.GetFullPath and
        // hoping it collapses the same way on every OS and every .NET
        // version. This is the actual security boundary, so it does not
        // get to depend on OS-specific path-parsing behaviour.
        var stack = new Stack<string>();
        foreach (string segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count == 0)
                    throw new UnauthorizedAccessException("Path attempts to traverse above the allowed directory.");
                stack.Pop();
                continue;
            }
            stack.Push(segment);
        }

        string safeRelative = string.Join(Path.DirectorySeparatorChar, stack.Reverse());
        string candidate = Path.GetFullPath(Path.Combine(root, safeRelative));

        if (!IsWithinRoot(candidate, root))
            throw new UnauthorizedAccessException($"Resolved path is outside the allowed directory.");

        return candidate;
    }

    /// <summary>
    /// True if <paramref name="relativePath"/> can be safely resolved under <paramref name="rootDirectory"/>.
    /// Never throws - use this when you want a bool instead of a try/catch.
    /// </summary>
    public static bool IsSafe(string rootDirectory, string relativePath)
    {
        try
        {
            ResolveSafePath(rootDirectory, relativePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Strips any directory component and replaces characters that are invalid
    /// in a Windows filename - using a hardcoded Windows character set rather
    /// than Path.GetInvalidFileNameChars(), which returns a much smaller set
    /// on Linux/macOS than on Windows. Since the destination filesystem is
    /// always Windows (this app ships on Windows only), the rule must not
    /// change depending on what OS happens to build or test the code.
    /// Guarantees a non-empty result.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        // Strip any directory component by taking whatever comes after the
        // last '/' or '\' - done manually (not via Path.GetFileName) because
        // Path.GetFileName only recognizes '\' as a separator on Windows.
        string name = fileName;
        int lastSeparator = name.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSeparator >= 0) name = name[(lastSeparator + 1)..];

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(Array.IndexOf(WindowsInvalidFileNameChars, c) >= 0 ? '_' : c);
        name = builder.ToString();

        // Windows also disallows a small set of reserved device names,
        // regardless of extension (e.g. "CON.txt" is still forbidden).
        string stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (Array.IndexOf(WindowsReservedNames, stem) >= 0)
            name = "_" + name;

        // Windows also strips/forbids trailing dots and spaces.
        name = name.TrimEnd('.', ' ').Trim();

        return string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : name;
    }

    /// <summary>
    /// If <paramref name="desiredPath"/> already exists, appends " (1)", " (2)", etc.
    /// before the extension until a free path is found. Never overwrites silently.
    /// </summary>
    public static string MakeUniquePath(string desiredPath)
    {
        if (!File.Exists(desiredPath) && !Directory.Exists(desiredPath))
            return desiredPath;

        string dir = Path.GetDirectoryName(desiredPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(desiredPath);
        string ext = Path.GetExtension(desiredPath);

        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        // Practically unreachable, but keep the method total.
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static string NormalizeRoot(string rootDirectory)
    {
        string full = Path.GetFullPath(rootDirectory);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool IsWithinRoot(string candidateFullPath, string normalizedRoot)
    {
        // normalizedRoot already ends with a separator, so a StartsWith check
        // cannot be fooled by a sibling directory that merely shares a prefix
        // (e.g. root "C:\Shared\" vs candidate "C:\SharedEvil\x").
        string candidate = candidateFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? candidateFullPath
            : candidateFullPath + Path.DirectorySeparatorChar;

        return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
               || string.Equals(candidateFullPath, normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
