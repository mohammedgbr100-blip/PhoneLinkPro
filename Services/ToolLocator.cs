using System.IO;

namespace ScrcpyDeck.Services;

/// <summary>Locates the bundled engine binaries (identity-neutral names in the "engine" folder).</summary>
public static class ToolLocator
{
    private const string EngineExe = "PhoneLink.Engine.exe"; // (mirroring engine)
    private const string BridgeExe = "PhoneLink.Bridge.exe"; // (device bridge)
    private const string ServerDat = "core.dat";             // (device-side server payload)

    private static string? _bridge;
    private static string? _engine;
    private static string? _server;

    public static string BaseDir => AppContext.BaseDirectory;

    public static string AdbPath => _bridge ??= Find(BridgeExe);
    public static string ScrcpyPath => _engine ??= Find(EngineExe);
    public static string ServerPath => _server ??= Find(ServerDat);

    public static bool AdbAvailable => File.Exists(AdbPath);
    public static bool ScrcpyAvailable => File.Exists(ScrcpyPath);

    private static string Find(string file)
    {
        var candidates = new[]
        {
            Path.Combine(BaseDir, "engine", file),
            Path.Combine(BaseDir, file),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return Path.Combine(BaseDir, "engine", file);
    }
}
