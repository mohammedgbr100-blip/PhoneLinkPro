using System.Reflection;
using ScrcpyDeck.Views;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Repair batch 5B (§6) — DashboardView.SignalOperationSettledForTests, the one call site that
/// invokes the test-only OperationSettledForTests hook. These are pure, in-process tests: the
/// wrapper and its exception-capture field are plain static members, so no live WPF Application
/// is needed to prove them (unlike RefreshAsync/ApplyDevicesAsync themselves, which do need one —
/// see DashboardMissedUpdateOrderingSelfTest's own Scenario G for that half of the proof, under
/// PLP_SELFTEST).
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class DashboardOperationSettledHookTests : IDisposable
{
    public DashboardOperationSettledHookTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        DashboardView.OperationSettledForTests = null;
        DashboardView.OperationSettledHookExceptionForTests = null;
    }

    [Fact] // a null hook (the production default) does nothing and records no failure
    public void NullHook_DoesNothing()
    {
        DashboardView.OperationSettledForTests = null;

        var ex = Record.Exception(() => DashboardView.SignalOperationSettledForTests());

        Assert.Null(ex);
        Assert.Null(DashboardView.OperationSettledHookExceptionForTests);
    }

    [Fact] // a well-behaved hook still runs and signals normally
    public void NormalHook_Signals()
    {
        var signalled = false;
        DashboardView.OperationSettledForTests = () => signalled = true;

        DashboardView.SignalOperationSettledForTests();

        Assert.True(signalled);
        Assert.Null(DashboardView.OperationSettledHookExceptionForTests);
    }

    [Fact] // [REGRESSION] a throwing hook must never escape the wrapper — it must not propagate
           // to the caller (which, in production code, is inside a finally block that could
           // otherwise mask the real operation's own exception)
    public void ThrowingHook_NeverPropagates()
    {
        DashboardView.OperationSettledForTests = () => throw new InvalidOperationException("boom");

        var ex = Record.Exception(() => DashboardView.SignalOperationSettledForTests());

        Assert.Null(ex);
    }

    [Fact] // [REGRESSION] a throwing hook's own exception is captured, not silently discarded —
           // the harness (DashboardMissedUpdateOrderingSelfTest.RunAndWaitForSettle) checks this
           // field and fails loudly if it is ever non-null after ITS OWN (non-throwing) hook runs
    public void ThrowingHook_ExceptionIsCaptured()
    {
        DashboardView.OperationSettledForTests = () => throw new InvalidOperationException("boom");

        DashboardView.SignalOperationSettledForTests();

        var captured = DashboardView.OperationSettledHookExceptionForTests;
        Assert.NotNull(captured);
        Assert.IsType<InvalidOperationException>(captured);
        Assert.Equal("boom", captured!.Message);
    }

    [Fact] // installing a new hook after a throwing one fully replaces it — proves the static
           // hook slot itself is simple, mutable state a harness can always reset/restore, which
           // is what DashboardMissedUpdateOrderingSelfTest.RunAndWaitForSettle's own
           // save-then-restore (`previous = ...; ...; OperationSettledForTests = previous;`)
           // relies on even after a hook exception
    public void HookSlot_CanBeRestoredAfterAThrowingHook()
    {
        DashboardView.OperationSettledForTests = () => throw new InvalidOperationException("boom");
        DashboardView.SignalOperationSettledForTests();
        Assert.NotNull(DashboardView.OperationSettledHookExceptionForTests);

        DashboardView.OperationSettledForTests = null;
        DashboardView.OperationSettledHookExceptionForTests = null;

        var ex = Record.Exception(() => DashboardView.SignalOperationSettledForTests());
        Assert.Null(ex);
        Assert.Null(DashboardView.OperationSettledHookExceptionForTests);
    }

    [Fact] // production code has no user-accessible path to the hook: internal, static, and only
           // ever set by test/self-test harness code, never by anything reachable from the UI
    public void Hook_IsInternalStaticNotPublic()
    {
        var field = typeof(DashboardView).GetField(
            "OperationSettledForTests", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsStatic);
        Assert.True(field.IsAssembly, "must be internal, not public — never a caller-facing switch");
        Assert.False(field.IsPublic);
    }

    [Fact] // the exception-capture field is likewise internal-only, never public API
    public void HookExceptionField_IsInternalStaticNotPublic()
    {
        var field = typeof(DashboardView).GetField(
            "OperationSettledHookExceptionForTests", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsStatic);
        Assert.True(field.IsAssembly);
        Assert.False(field.IsPublic);
    }

    [Fact] // [REGRESSION] both production call sites must go through the wrapper, never invoke
           // the hook directly — the exact mutation (H1) this repair batch guards against
    public void Source_ProductionCallSitesUseTheWrapperNotADirectInvoke()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        Assert.DoesNotContain("OperationSettledForTests?.Invoke();", ExcludeWrapperBody(code));
        Assert.Contains("SignalOperationSettledForTests();", code, StringComparison.Ordinal);
    }

    /// <summary>Strips SignalOperationSettledForTests' own body (which legitimately contains the
    /// one guarded `OperationSettledForTests?.Invoke();` call) so the assertion above only checks
    /// the two production call sites in RefreshAsync/ApplyDevicesAsync.</summary>
    private static string ExcludeWrapperBody(string code)
    {
        var start = code.IndexOf("internal static void SignalOperationSettledForTests()", StringComparison.Ordinal);
        Assert.True(start >= 0, "the wrapper method must still exist");
        var braceStart = code.IndexOf('{', start);
        var depth = 0;
        for (var i = braceStart; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0)
                return code[..start] + code[(i + 1)..];
        }
        throw new InvalidOperationException("wrapper method has no matching closing brace");
    }
}
