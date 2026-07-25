using System.Diagnostics;
using ScrcpyDeck.Models;
using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// SEC-01 (Batch 1E) — the process-scanning / anti-debug SecurityGuard is GONE, and nothing
/// like it may creep back: no Environment.FailFast, no system-wide process-name scanning, no
/// periodic security timer, and the app must work normally alongside harmless processes that
/// happen to carry a formerly-blocked tool name.
///
/// In the global collection: the behavioural test drives ScrcpyService/AppLifecycle.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class SecurityGuardRemovalTests : IDisposable
{
    public SecurityGuardRemovalTests()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
        ScrcpyService.EngineFileCheck = () => Array.Empty<string>();
        ScrcpyService.IntegrityCheck = () => true;
    }

    public void Dispose()
    {
        ScrcpyService.ResetForTests();
        AppLifecycle.ResetForTests();
    }

    // ---------------------------------------------------------------- the type is gone

    [Fact]
    public void SecurityGuardType_NoLongerExists()
    {
        var appAssembly = typeof(AppLifecycle).Assembly;

        Assert.Null(appAssembly.GetType("ScrcpyDeck.Services.SecurityGuard"));
    }

    // ---------------------------------------------------------------- source guarantees

    /// <summary>Repo root, located from the test assembly (…\tests\PhoneLinkPro.Tests\bin\…).</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScrcpyDeck.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Every production source file (the application, not the tests).</summary>
    private static IEnumerable<string> ProductionSources()
    {
        var root = FindRepoRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));
    }

    [Fact] // requirement A-3: no Environment.FailFast anywhere in production code
    public void NoProductionSource_CallsEnvironmentFailFast()
    {
        foreach (var file in ProductionSources())
            Assert.DoesNotContain("Environment.FailFast", File.ReadAllText(file));
    }

    [Fact] // no anti-debug probes remain
    public void NoProductionSource_UsesAntiDebugProbes()
    {
        foreach (var file in ProductionSources())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("IsDebuggerPresent", text);
            Assert.DoesNotContain("CheckRemoteDebuggerPresent", text);
        }
    }

    [Fact] // full process enumeration exists ONLY in the ADB-server ownership policy, which
           // narrows to adb-family names and proves ownership by image path before ever
           // acting — it is not a security scanner and it never touches foreign processes.
    public void ProcessEnumeration_ExistsOnlyInTheAdbOwnershipPolicy()
    {
        foreach (var file in ProductionSources())
        {
            if (Path.GetFileName(file) == "AdbServerOwnership.cs") continue;
            Assert.DoesNotContain("GetProcesses", File.ReadAllText(file));
        }
    }

    [Fact] // the old guard file itself is gone from the tree
    public void SecurityGuardSourceFile_IsDeleted()
        => Assert.False(File.Exists(Path.Combine(FindRepoRoot(), "Services", "SecurityGuard.cs")));

    // ---------------------------------------------------------------- behaviour

    [Fact] // the app must function while a harmless process carries a formerly blocked name
    public async Task AppFunctionsNormally_WhileAProcessWithAFormerlyBlockedNameRuns()
    {
        // "cheatengine" was on the old blocklist (substring match). Run a harmless copy of
        // cmd.exe under that name and prove a mirror session still launches and shutdown
        // still completes.
        var dir = Path.Combine(Path.GetTempPath(), "plp-tests-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var fakeTool = Path.Combine(dir, "cheatengine-harmless.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), fakeTool);

        Process? tool = null;
        try
        {
            tool = Process.Start(new ProcessStartInfo
            {
                FileName = fakeTool,
                Arguments = "/c ping -n 4 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(tool);
            Assert.False(tool!.HasExited);

            var proc = new FakeProcessHandle();
            ScrcpyService.Launcher = new FakeLauncher(proc);

            var result = ScrcpyService.Launch(new ScrcpyOptions { Serial = "R58M12ABCDE" });
            Assert.Equal(MirrorLaunchStatus.Started, result.Status);

            AppLifecycle.Shutdown();
            Assert.Equal(0, ScrcpyService.ActiveCount);
        }
        finally
        {
            try { if (tool is { HasExited: false }) tool.Kill(entireProcessTree: true); } catch { }
            tool?.Dispose();
            // Bounded cleanup: the copy may stay locked for a moment after the kill.
            for (var i = 0; i < 20 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { await Task.Delay(100); }
            }
        }
    }
}
