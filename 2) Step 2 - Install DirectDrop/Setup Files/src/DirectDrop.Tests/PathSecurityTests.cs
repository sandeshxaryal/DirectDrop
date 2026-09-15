using DirectDrop.Core;
using Xunit;

namespace DirectDrop.Tests;

public class PathSecurityTests
{
    private readonly string _root;

    public PathSecurityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dd-test-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "Videos"));
        File.WriteAllText(Path.Combine(_root, "Videos", "clip.mp4"), "fake video bytes");
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM")]
    [InlineData("../secret.txt")]
    [InlineData("Videos/../../outside.txt")]
    public void ResolveSafePath_rejects_traversal_attempts(string maliciousPath)
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathSecurity.ResolveSafePath(_root, maliciousPath));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("\\\\attacker-share\\payload.exe")]
    public void ResolveSafePath_rejects_absolute_paths(string absolutePath)
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathSecurity.ResolveSafePath(_root, absolutePath));
    }

    [Fact]
    public void ResolveSafePath_rejects_empty_input()
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathSecurity.ResolveSafePath(_root, ""));
    }

    [Theory]
    [InlineData("Videos/clip.mp4")]
    [InlineData("Videos\\clip.mp4")]
    [InlineData("newfile.txt")]
    public void ResolveSafePath_accepts_paths_within_root(string relativePath)
    {
        string resolved = PathSecurity.ResolveSafePath(_root, relativePath);
        Assert.StartsWith(Path.GetFullPath(_root), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveSafePath_does_not_match_a_sibling_directory_with_a_shared_prefix()
    {
        // Root is ".../Shared/" - make sure ".../SharedEvil/x" is never
        // treated as "inside" just because the string happens to start
        // with the same characters.
        string parent = Path.GetDirectoryName(_root)!;
        string evilSibling = _root.TrimEnd(Path.DirectorySeparatorChar) + "Evil";
        Directory.CreateDirectory(evilSibling);
        try
        {
            Assert.False(PathSecurity.IsSafe(_root, Path.GetRelativePath(_root, Path.Combine(evilSibling, "x.txt"))));
        }
        finally
        {
            Directory.Delete(evilSibling, recursive: true);
        }
    }

    [Fact]
    public void IsSafe_returns_false_instead_of_throwing()
    {
        Assert.False(PathSecurity.IsSafe(_root, "../outside.txt"));
        Assert.True(PathSecurity.IsSafe(_root, "Videos/clip.mp4"));
    }

    [Theory]
    [InlineData("normal.txt", "normal.txt")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("a<b>c:d.txt", "a_b_c_d.txt")]
    public void SanitizeFileName_strips_directories_and_invalid_characters(string input, string expected)
    {
        Assert.Equal(expected, PathSecurity.SanitizeFileName(input), ignoreCase: true);
    }

    [Fact]
    public void MakeUniquePath_appends_a_counter_when_the_file_already_exists()
    {
        string path = Path.Combine(_root, "dup.txt");
        File.WriteAllText(path, "one");
        string second = PathSecurity.MakeUniquePath(path);
        Assert.NotEqual(path, second);
        Assert.Equal("dup (1).txt", Path.GetFileName(second));
    }

    [Fact]
    public void MakeUniquePath_returns_input_unchanged_when_free()
    {
        string path = Path.Combine(_root, "free.txt");
        Assert.Equal(path, PathSecurity.MakeUniquePath(path));
    }
}
