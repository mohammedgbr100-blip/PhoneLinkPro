using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>Locates repository files from the test output directory (source-drift checks).</summary>
internal static class TestPaths
{
    /// <summary>Repo root, found by walking up from the test assembly to ScrcpyDeck.csproj.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScrcpyDeck.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static string ReadRepoFile(params string[] relative)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(relative).ToArray()));
}
