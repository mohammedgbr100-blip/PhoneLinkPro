using System.Reflection;
using ScrcpyDeck.Views;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Batch 3B — DashboardView's DeviceWatcher.DevicesChanged subscription lifecycle.
///
/// <para>
/// The genuine behavioural proof — constructing a real DashboardView, raising Loaded/Unloaded,
/// and reading DeviceWatcher's subscriber count off its backing field — runs in
/// DashboardLifecycleSelfTest under PLP_SELFTEST. xUnit runs MTA with no Application.Resources
/// merged (no ScrcpyDeck.App instance is created), so DashboardView.xaml's StaticResource
/// lookups ("Bg", "GhostButton", "Cardish", ...) cannot resolve here — the same constraint
/// DesignFoundationTests documents for MainWindow. Constructing one just for this file would
/// mean creating the process-wide Application singleton inside the shared xUnit host, which
/// would change DeviceWatcher.Start()'s dispatcher-timer branch for every other test class
/// that touches it (DeviceWatcherLogicTests and friends) — exactly the kind of indirect
/// service-behaviour change this batch must not cause.
/// </para>
///
/// <para>
/// What IS safely verifiable here, without any of that: the reflection-visible shape of the
/// fix (a real, internal, false-by-default field — not a source-text guess), and that the old
/// anonymous-subscription pattern is gone from source. The second class is supplementary
/// coverage only, per instruction; it is not the proof that the lifecycle is correct.
/// </para>
/// </summary>
public sealed class DashboardViewSubscriptionTests
{
    // ---------------------------------------------------------------- reflection-verified shape

    [Fact] // the harness switch is a real field, not merely mentioned in a comment
    public void SuppressRefreshForHarness_IsAnInternalStaticBoolDefaultingFalse()
    {
        var field = typeof(DashboardView).GetField(
            "SuppressRefreshForHarness", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field!.FieldType);
        Assert.True(field.IsStatic);
        Assert.True(field.IsAssembly, "must be internal, not public — never a caller-facing switch");
        Assert.False(field.IsPublic);
        Assert.False((bool)field.GetValue(null)!, "must default to false: normal launches always refresh");
    }

    [Fact] // exactly one boolean guards the subscription — no scope for it to desync
    public void WatcherSubscribedGuard_IsAPrivateInstanceBool()
    {
        var field = typeof(DashboardView).GetField(
            "_watcherSubscribed", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);
        Assert.Equal(typeof(bool), field!.FieldType);
        Assert.False(field.IsStatic, "must be per-instance: each cached view tracks its own subscription");
    }

    [Fact] // the named handler DeviceWatcher.DevicesChanged is actually wired to exists
    public void OnDevicesChanged_HandlerMethodExists()
    {
        var method = typeof(DashboardView).GetMethod(
            "OnDevicesChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(void), method!.ReturnType);
    }

    // ---------------------------------------------------------------- supplementary source guard
    // Not the proof — DashboardLifecycleSelfTest (PLP_SELFTEST) is. This only confirms the
    // specific defect pattern reported for Batch 3B cannot silently return.

    [Fact] // [REGRESSION] the original defect: an anonymous lambda subscribed in the
           // constructor, with no matching unsubscribe anywhere in the class
    public void Source_NoLongerSubscribesWithAnAnonymousLambdaInTheConstructor()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        Assert.DoesNotContain("DeviceWatcher.DevicesChanged += devices =>", code);
        Assert.Contains("DeviceWatcher.DevicesChanged += OnDevicesChanged;", code);
        Assert.Contains("DeviceWatcher.DevicesChanged -= OnDevicesChanged;", code);
    }

    [Fact] // Loaded/Unloaded are named handlers, not inline lambdas, and both are wired
    public void Source_UsesNamedLoadedAndUnloadedHandlers()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        Assert.Contains("Loaded += DashboardView_Loaded;", code);
        Assert.Contains("Unloaded += DashboardView_Unloaded;", code);
        Assert.Contains("private void DashboardView_Loaded(", code);
        Assert.Contains("private void DashboardView_Unloaded(", code);
    }

    [Fact] // both Subscribe and Unsubscribe are idempotent (early-return on the guard)
    public void Source_SubscribeAndUnsubscribeAreGuardedByTheSameBoolean()
    {
        var code = TestPaths.ReadRepoFile("Views", "DashboardView.xaml.cs");

        Assert.Contains("if (_watcherSubscribed) return;", code);
        Assert.Contains("if (!_watcherSubscribed) return;", code);
    }
}
