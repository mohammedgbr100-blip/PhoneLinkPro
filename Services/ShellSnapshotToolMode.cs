using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScrcpyDeck.Services;

/// <summary>
/// Hidden, bounded design-QA tool mode (mirrors <see cref="IntegrityToolMode"/> and the
/// PLP_SELFTEST pattern): when <c>PLP_TOOL_MODE=shell-snapshot</c> is set EXACTLY, the app
/// renders the shell offscreen at the approved reference sizes and exits with a meaningful
/// code instead of running normally.
///
/// <list type="bullet">
///   <item><description>Dispatched from <c>App.OnStartup</c> BEFORE single-instance
///   acquisition, consent, integrity verification and any engine service — so this mode
///   never starts ADB or the device watcher, never claims engine-service ownership, and
///   never disturbs a running normal instance.</description></item>
///   <item><description><c>PLP_SNAPSHOT_OUT</c> must be a fully-qualified directory. Empty,
///   relative and malformed values are REJECTED; there is no silent fall back to the working
///   directory.</description></item>
///   <item><description>Exit 0 = snapshots written, 3 = validation / render / tool failure.
///   A one-line report is written to <c>%TEMP%\plp_shell_snapshot.txt</c>.</description></item>
/// </list>
///
/// Nothing here activates on a partial, empty, <c>0</c> or <c>false</c> value: a normal user
/// launch is never affected by an unrelated environment variable.
/// </summary>
internal static class ShellSnapshotToolMode
{
    internal const string ModeVariable = "PLP_TOOL_MODE";
    internal const string ModeValue = "shell-snapshot";
    internal const string OutVariable = "PLP_SNAPSHOT_OUT";

    internal const int ExitSuccess = 0;
    internal const int ExitFailure = 3;

    /// <summary>Hard ceiling on the whole tool run; the process cannot outlive it.</summary>
    internal static TimeSpan WatchdogBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-resize ceiling on waiting for a real render pass.</summary>
    internal static TimeSpan RenderWaitBudget { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Far offscreen: the capture never disturbs whatever the user is doing.</summary>
    private const double OffscreenLeft = -6000;
    private const double OffscreenTop = 100;

    /// <summary>The approved Direction D reference sizes, in capture order.</summary>
    internal static readonly (double Width, double Height)[] ReferenceSizes =
        { (1440d, 900d), (1366d, 768d) };

    private static string? _outputDirectory;

    /// <summary>
    /// True once this mode has taken over startup. MainWindow's Loaded handler checks this
    /// to skip consent, integrity verification, engine services and onboarding.
    /// </summary>
    internal static bool IsActive { get; private set; }

    // ---------------- activation (pure, directly testable) ----------------

    /// <summary>
    /// EXACT match only. Trimmed and case-insensitive, but nothing else activates: empty,
    /// "0", "false", "shell", "shell-snapshots" and any other value all return false.
    /// </summary>
    internal static bool IsModeRequested(string? raw)
        => string.Equals(raw?.Trim(), ModeValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the environment and applies <see cref="IsModeRequested(string?)"/>.</summary>
    internal static bool IsRequested()
        => IsModeRequested(Environment.GetEnvironmentVariable(ModeVariable));

    /// <summary>
    /// Validates the requested output directory. Returns the normalised full path, or null
    /// with a caller-reportable reason. A relative or drive-relative path is an error, never
    /// a silent fall back to the process working directory.
    /// </summary>
    internal static string? ValidateOutputDirectory(string? raw, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{OutVariable} is not set";
            return null;
        }

        var value = raw.Trim();

        // IsPathFullyQualified also rejects drive-relative forms ("C:out") and root-relative
        // forms ("\out"), both of which would resolve against ambient process state.
        if (!Path.IsPathFullyQualified(value))
        {
            error = $"{OutVariable} must be an absolute path, got '{value}'";
            return null;
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            error = $"{OutVariable} is not a usable path ('{value}'): {ex.GetType().Name}";
            return null;
        }
    }

    /// <summary>A window smaller than one pixel cannot back a RenderTargetBitmap.</summary>
    internal static bool AreDimensionsRenderable(double width, double height)
        => width >= 1 && height >= 1;

    internal static string SnapshotFileName(double width, double height)
        => $"shell-{width:F0}x{height:F0}.png";

    /// <summary>
    /// Bounded watchdog seam. Returns a handle that CANCELS the timeout when disposed, so
    /// the timeout decision is testable deterministically without hanging anything.
    /// </summary>
    internal static IDisposable StartWatchdog(TimeSpan budget, Action onTimeout)
    {
        var timer = new System.Threading.Timer(_ => onTimeout(), null,
            (int)budget.TotalMilliseconds, System.Threading.Timeout.Infinite);
        return timer;
    }

    // ---------------- startup dispatch ----------------

    /// <summary>
    /// Claims startup for this mode and validates its inputs. Call only when
    /// <see cref="IsRequested"/> is true. Returns <see cref="ExitSuccess"/> when the run may
    /// proceed, or <see cref="ExitFailure"/> when the caller must exit immediately.
    /// </summary>
    internal static int Prepare()
    {
        IsActive = true;

        var directory = ValidateOutputDirectory(Environment.GetEnvironmentVariable(OutVariable), out var error);
        if (directory == null) return Fail(error);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            return Fail($"cannot create output directory '{directory}': {ex.GetType().Name}: {ex.Message}");
        }

        _outputDirectory = directory;
        return ExitSuccess;
    }

    /// <summary>
    /// Positions a window far offscreen for capture. Called from the MainWindow constructor
    /// (before the window is ever shown, so nothing flashes on the user's desktop) and again
    /// defensively before capture; it is idempotent.
    /// </summary>
    internal static void ConfigureForCapture(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Left = OffscreenLeft;
        window.Top = OffscreenTop;
    }

    /// <summary>
    /// Captures every reference size from the offscreen shell and returns the process exit
    /// code. Never throws: every failure is reported and mapped to <see cref="ExitFailure"/>,
    /// so a failed run can never be mistaken for a successful one.
    /// </summary>
    internal static async Task<int> RunAsync(Func<Window?> windowResolver)
    {
        var watchdog = StartWatchdog(WatchdogBudget, () => Environment.Exit(ExitFailure));
        Window? window = null;
        try
        {
            window = windowResolver();
            if (window == null) return Fail("the shell window was not available to capture");

            ConfigureForCapture(window);
            if (!window.IsVisible) window.Show();

            var exitCode = await CaptureAsync(window, _outputDirectory!);
            if (exitCode == ExitSuccess)
                Report($"OK: {ReferenceSizes.Length} snapshot(s) written to {_outputDirectory}");
            return exitCode;
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            watchdog.Dispose();
            try { window?.Close(); } catch { /* teardown is best effort; the exit code is the contract */ }
        }
    }

    // ---------------- capture ----------------

    private static async Task<int> CaptureAsync(Window window, string outputDirectory)
    {
        foreach (var (width, height) in ReferenceSizes)
        {
            window.Width = width;
            window.Height = height;

            if (!await WaitForRenderAsync(window))
                return Fail($"render did not settle within {RenderWaitBudget.TotalSeconds:F0}s " +
                            $"at {width:F0}x{height:F0}");

            // RenderTargetBitmap throws on a zero-sized source, and ActualWidth is driven by
            // the window handle rather than by layout — so it is validated, never assumed.
            var actualWidth = window.ActualWidth;
            var actualHeight = window.ActualHeight;
            if (!AreDimensionsRenderable(actualWidth, actualHeight))
                return Fail($"unrenderable window size at {width:F0}x{height:F0}: " +
                            $"ActualWidth={actualWidth:F1} ActualHeight={actualHeight:F1}");

            var bitmap = new RenderTargetBitmap(
                (int)actualWidth, (int)actualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var path = Path.Combine(outputDirectory, SnapshotFileName(width, height));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        return ExitSuccess;
    }

    /// <summary>
    /// Waits for a CAUSAL render signal rather than a fixed sleep: layout is flushed, the
    /// dispatcher is drained to Loaded priority, and then one real compositor frame is
    /// awaited. Bounded by <see cref="RenderWaitBudget"/> so it can never hang the tool.
    /// </summary>
    private static async Task<bool> WaitForRenderAsync(Window window)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        var rendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? onRendering = null;
        onRendering = (_, _) =>
        {
            CompositionTarget.Rendering -= onRendering;
            rendered.TrySetResult(true);
        };
        CompositionTarget.Rendering += onRendering;

        var finished = await Task.WhenAny(rendered.Task, Task.Delay(RenderWaitBudget));
        if (finished != rendered.Task)
        {
            CompositionTarget.Rendering -= onRendering;
            return false;
        }

        // Let anything scheduled by that frame drain before the pixels are read back.
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        return true;
    }

    // ---------------- reporting ----------------

    private static int Fail(string message)
    {
        Report("FAIL: " + message);
        return ExitFailure;
    }

    private static void Report(string text)
    {
        Log.Info("ShellSnapshot: " + text);
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "plp_shell_snapshot.txt"), text);
        }
        catch { /* the exit code is the contract; the report file is best-effort */ }
    }
}
