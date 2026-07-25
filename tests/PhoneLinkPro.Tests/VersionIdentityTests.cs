using System.Reflection;
using System.Text.RegularExpressions;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// REL-03 (Batch 1F) — one authoritative product version, one product identity.
/// These tests pin the design: Directory.Build.props is the only place a version literal
/// lives; every displayed value derives from the compiled assembly; the installer reads the
/// version from the built exe; the consent version stays a separate concept; and no
/// user-visible metadata says "ScrcpyDeck".
/// </summary>
public sealed class VersionIdentityTests
{
    private static readonly Assembly App = typeof(AppVersionInfo).Assembly;

    // ---------------------------------------------------------------- assembly agreement

    [Fact] // the version every UI surface displays
    public void ProductVersion_IsThreePartAndComesFromAssemblyMetadata()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppVersionInfo.ProductVersion);
        Assert.Equal(
            App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            AppVersionInfo.ProductVersion);
    }

    [Fact] // assembly + file versions are the product version with a trailing .0
    public void AssemblyAndFileVersions_AgreeWithProductVersion()
    {
        Assert.Equal(AppVersionInfo.ProductVersion + ".0", App.GetName().Version!.ToString());
        Assert.Equal(AppVersionInfo.ProductVersion + ".0",
            App.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version);
    }

    [Fact]
    public void ShortVersion_IsMajorMinorOfProductVersion()
        => Assert.StartsWith(AppVersionInfo.ShortVersion + ".", AppVersionInfo.ProductVersion,
            StringComparison.Ordinal);

    // ---------------------------------------------------------------- single source

    [Fact] // Directory.Build.props holds the one literal; the csproj must not re-declare it
    public void DirectoryBuildProps_IsTheOnlyVersionSource()
    {
        var props = TestPaths.ReadRepoFile("Directory.Build.props");
        var match = Regex.Match(props, @"<PlpProductVersion>(\d+\.\d+\.\d+)</PlpProductVersion>");
        Assert.True(match.Success, "PlpProductVersion missing from Directory.Build.props");
        Assert.Equal(AppVersionInfo.ProductVersion, match.Groups[1].Value);

        var csproj = TestPaths.ReadRepoFile("ScrcpyDeck.csproj");
        Assert.DoesNotMatch(@"<Version>\d", csproj);
        Assert.DoesNotMatch(@"<FileVersion>\d", csproj);
        Assert.DoesNotMatch(@"<AssemblyVersion>\d", csproj);
    }

    [Theory] // no display surface may carry its own version literal again
    [InlineData("MainWindow.xaml")]
    [InlineData("Windows/AboutWindow.xaml")]
    public void Xaml_ContainsNoHardcodedVersionLiteral(string relativePath)
    {
        var xaml = TestPaths.ReadRepoFile(relativePath.Split('/'));
        Assert.DoesNotMatch(@"v\d+\.\d+", xaml);
        Assert.DoesNotMatch(@"الإصدار\s+\d", xaml);
    }

    // ---------------------------------------------------------------- installer

    [Fact] // the installer derives its version from the published exe, never a literal
    public void Installer_ConsumesTheCentralVersion()
    {
        var iss = TestPaths.ReadRepoFile("installer", "PhoneLinkPro.iss");
        Assert.DoesNotMatch("#define\\s+MyAppVersion\\s+\"", iss);
        Assert.Contains("GetVersionNumbersString", iss);
        Assert.Contains("VersionInfoVersion={#MyAppVersion4}", iss);
    }

    [Fact] // Inno's AppMutex must equal the application's single-instance mutex name
    public void Installer_AppMutexMatchesTheApplicationMutex()
    {
        var iss = TestPaths.ReadRepoFile("installer", "PhoneLinkPro.iss");
        Assert.Contains($"\"{SingleInstance.MutexName}\"", iss);
        Assert.Contains("AppMutex={#MyAppMutex}", iss);
    }

    // ---------------------------------------------------------------- consent separation

    [Fact] // legal consent is versioned by the policy text, NOT by the product release
    public void ConsentVersion_IsSeparateFromProductVersion()
    {
        Assert.Equal(PolicyText.Version, SettingsStore.CurrentConsentVersion);
        // If these ever coincide, someone wired the consent version to the release version;
        // a product release must never silently re-prompt (or silently NOT re-prompt) consent.
        Assert.NotEqual(AppVersionInfo.ProductVersion, PolicyText.Version);
    }

    // ---------------------------------------------------------------- identity

    [Fact] // user-visible metadata says PhoneLink Pro, never the internal ScrcpyDeck name
    public void ProductIdentity_ContainsNoInternalName()
    {
        Assert.Equal("PhoneLink Pro", App.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        Assert.Equal("PhoneLink Pro", App.GetCustomAttribute<AssemblyTitleAttribute>()!.Title);
        Assert.Equal("PhoneLink Pro", AppVersionInfo.ProductName);
        Assert.Equal("PhoneLinkPro", App.GetName().Name);

        var manifest = TestPaths.ReadRepoFile("app.manifest");
        Assert.DoesNotContain("ScrcpyDeck", manifest);
        Assert.Contains("PhoneLinkPro.app", manifest);

        var iss = TestPaths.ReadRepoFile("installer", "PhoneLinkPro.iss");
        Assert.DoesNotContain("ScrcpyDeck", iss);
    }
}
