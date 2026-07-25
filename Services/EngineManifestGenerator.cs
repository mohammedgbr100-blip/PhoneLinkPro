using System.IO;

namespace ScrcpyDeck.Services;

/// <summary>Outcome of a manifest generation attempt.</summary>
internal sealed record EngineManifestGenerationResult(
    bool Success,
    string? ManifestText,
    IReadOnlyList<string> MissingMandatoryFiles,
    string? Error)
{
    public static EngineManifestGenerationResult Ok(string text)
        => new(true, text, Array.Empty<string>(), null);

    public static EngineManifestGenerationResult Missing(IReadOnlyList<string> missing)
        => new(false, null, missing, $"mandatory engine files missing: {string.Join(", ", missing)}");

    public static EngineManifestGenerationResult Failed(string error)
        => new(false, null, Array.Empty<string>(), error);
}

/// <summary>
/// Builds the engine integrity manifest from an engine directory (SEC-01).
///
/// <para><b>Coverage.</b> Exactly the files the bundled engine needs to run: the renamed
/// engine/bridge executables, the device-side server payload, and every top-level DLL —
/// which mirrors what the MSBuild copy targets place into <c>engine\</c>. Nothing else is
/// included, so logs, settings, recordings, notices or stray files can never block startup.
/// (<c>Assets\app.png</c> is deliberately NOT covered: the mirror-window icon is optional at
/// runtime and a cosmetic file must not disable the whole engine.)</para>
///
/// <para><b>Mandatory set.</b> Generation fails loudly when a file the engine cannot run
/// without is absent, instead of silently producing a manifest for a broken bundle.</para>
///
/// <para><b>Determinism.</b> Same file bytes ⇒ byte-identical manifest, regardless of file
/// timestamps, enumeration order, machine or path of the source directory.</para>
/// </summary>
internal static class EngineManifestGenerator
{
    /// <summary>The renamed engine files a bundle can never run without.</summary>
    internal static readonly string[] CoreFiles =
    {
        "PhoneLink.Engine.exe",   // mirroring engine
        "PhoneLink.Bridge.exe",   // device bridge
        "core.dat",               // device-side server payload
    };

    /// <summary>DLLs required by the bridge and the engine, matched by exact name.</summary>
    internal static readonly string[] MandatoryDlls =
    {
        "AdbWinApi.dll", "AdbWinUsbApi.dll",   // bridge (USB transport)
        "SDL2.dll", "libusb-1.0.dll",          // engine (window/input, USB)
    };

    /// <summary>Version-suffixed codec DLL families; at least one of each must be present.</summary>
    internal static readonly string[] MandatoryDllPrefixes =
    {
        "avcodec-", "avformat-", "avutil-", "swresample-",
    };

    /// <summary>Generates manifest text for <paramref name="engineDirectory"/>.</summary>
    public static EngineManifestGenerationResult Generate(string engineDirectory)
    {
        try
        {
            if (!Directory.Exists(engineDirectory))
                return EngineManifestGenerationResult.Failed(
                    "engine directory not found: " + Path.GetFileName(engineDirectory.TrimEnd('\\', '/')));

            // Top level only, DLLs plus the fixed core names: nested folders, logs, text
            // files and anything user-generated are excluded by construction.
            var names = Directory.EnumerateFiles(engineDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetFileName(f)!)
                .Concat(CoreFiles.Where(c => File.Exists(Path.Combine(engineDirectory, c))))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var missing = FindMissingMandatory(names);
            if (missing.Count > 0) return EngineManifestGenerationResult.Missing(missing);

            var entries = names
                .Select(n => new EngineManifestEntry(
                    EngineManifest.EngineDirName + "/" + n,
                    EngineManifest.HashFileSha256(Path.Combine(engineDirectory, n))))
                .ToList();

            return EngineManifestGenerationResult.Ok(EngineManifest.Render(entries));
        }
        catch (ArgumentException ex)
        {
            // Render refused an entry (a name the manifest format cannot represent).
            return EngineManifestGenerationResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EngineManifestGenerationResult.Failed(ex.GetType().Name);
        }
    }

    /// <summary>Which mandatory files are absent from <paramref name="presentNames"/>.</summary>
    internal static List<string> FindMissingMandatory(IReadOnlyCollection<string> presentNames)
    {
        var present = new HashSet<string>(presentNames, StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var name in CoreFiles.Concat(MandatoryDlls))
            if (!present.Contains(name))
                missing.Add(name);

        foreach (var prefix in MandatoryDllPrefixes)
            if (!presentNames.Any(n =>
                    n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                missing.Add(prefix + "*.dll");

        return missing;
    }
}
