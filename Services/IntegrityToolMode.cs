using System.IO;
using System.Text;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, build-only entry point (mirrors the PLP_SELFTEST pattern): when
/// <c>PLP_INTEGRITY_TOOL</c> is set, the app runs the integrity machinery headlessly and
/// exits instead of showing a window.
///
/// <list type="bullet">
///   <item><description><c>verify</c> — verify this installation's engine files against the
///   embedded manifest. Exit 0 = valid, 2 = invalid, 3 = error. Used by the Release publish
///   gate, so a publish whose engine bundle disagrees with the manifest FAILS.</description></item>
///   <item><description><c>generate</c> — regenerate manifest text from this build's
///   <c>engine\</c> directory and write it to <c>PLP_INTEGRITY_OUT</c>. Used only by the
///   explicit developer step <c>update-engine-manifest.bat</c>; nothing regenerates the
///   tracked manifest implicitly, and application startup never updates hashes.</description></item>
/// </list>
///
/// A one-line report is also written to <c>%TEMP%\plp_integrity_tool.txt</c> for humans.
/// </summary>
internal static class IntegrityToolMode
{
    internal const string ModeVariable = "PLP_INTEGRITY_TOOL";
    internal const string OutVariable = "PLP_INTEGRITY_OUT";

    /// <summary>True when tool mode ran; the caller must exit with <paramref name="exitCode"/>.</summary>
    public static bool TryRun(out int exitCode)
    {
        exitCode = 0;
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (string.IsNullOrWhiteSpace(mode)) return false;

        try
        {
            exitCode = mode.Trim().ToLowerInvariant() switch
            {
                "verify" => Verify(),
                "generate" => Generate(),
                _ => Fail($"unknown mode '{mode.Trim()}'"),
            };
        }
        catch (Exception ex)
        {
            exitCode = Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    private static int Verify()
    {
        var result = EngineIntegrityVerifier.VerifyAsync().GetAwaiter().GetResult();
        Report($"verify: {result.Status}" +
               (result.AffectedPath != null ? $" file={result.AffectedPath}" : "") +
               $" files={result.ExpectedFileCount} in {result.Duration.TotalMilliseconds:F0}ms");
        return result.IsValid ? 0 : 2;
    }

    private static int Generate()
    {
        var outPath = Environment.GetEnvironmentVariable(OutVariable);
        if (string.IsNullOrWhiteSpace(outPath))
            return Fail("generate: " + OutVariable + " not set");

        var engineDir = Path.Combine(AppContext.BaseDirectory, EngineManifest.EngineDirName);
        var result = EngineManifestGenerator.Generate(engineDir);
        if (!result.Success)
            return Fail("generate: " + result.Error);

        // UTF-8 without BOM + LF endings: the manifest must be byte-identical on every
        // machine that generates it from the same engine files.
        File.WriteAllText(outPath, result.ManifestText, new UTF8Encoding(false));
        Report($"generate: OK ({result.ManifestText!.Count(c => c == '\n') - 1} entries)");
        return 0;
    }

    private static int Fail(string message)
    {
        Report("FAIL: " + message);
        return 3;
    }

    private static void Report(string text)
    {
        Log.Info("IntegrityTool: " + text);
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "plp_integrity_tool.txt"), text);
        }
        catch { /* the exit code is the contract; the report file is best-effort */ }
    }
}
