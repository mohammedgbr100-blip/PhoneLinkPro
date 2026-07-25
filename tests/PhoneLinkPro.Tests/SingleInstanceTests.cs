using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// P3-02 (Batch 1F) — single-instance ownership decisions, exercised against REAL named
/// kernel objects with unique per-test names (never the production name, so a developer's
/// running app cannot interfere with the suite, nor the suite with the app).
///
/// In the global collection: SingleInstance holds process-wide handle state, and
/// AppLifecycle.Shutdown (exercised by other collection members) calls
/// SingleInstance.Release — running in parallel would dispose a test's mutex mid-flight.
/// </summary>
[Collection(GlobalStateCollection.Name)]
public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _name = "PLP-TEST-" + Guid.NewGuid().ToString("N");

    public void Dispose() => SingleInstance.Release();

    [Fact] // first launch owns the instance
    public void FirstAcquire_BecomesPrimary()
    {
        Assert.Equal(InstanceRole.Primary, SingleInstance.Acquire(_name));
        Assert.True(SingleInstance.ShouldProceed(InstanceRole.Primary));
    }

    [Fact] // a live owner forces the second launch into the hand-off path
    public void SecondAcquire_WhileOwned_IsSecondary()
    {
        // A dedicated thread holds the mutex like a live first instance would.
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var m = new Mutex(initiallyOwned: true, _name);
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            try { m.ReleaseMutex(); } catch { }
        });
        owner.Start();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));

        try
        {
            var role = SingleInstance.Acquire(_name);

            Assert.Equal(InstanceRole.Secondary, role);
            Assert.False(SingleInstance.ShouldProceed(role));
        }
        finally
        {
            release.Set();
            owner.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact] // crash recovery: an owner that died without releasing must not lock users out
    public void Acquire_AfterOwnerThreadDiedHolding_RecoversOwnership()
    {
        Mutex? leaked = null;
        var owner = new Thread(() =>
        {
            // Deliberately NO release and NO dispose: the owning thread just ends,
            // which abandons the mutex exactly like a crashed process.
            leaked = new Mutex(initiallyOwned: true, _name);
        });
        owner.Start();
        owner.Join(TimeSpan.FromSeconds(5));

        try
        {
            var role = SingleInstance.Acquire(_name);

            Assert.Equal(InstanceRole.PrimaryRecovered, role);
            Assert.True(SingleInstance.ShouldProceed(role));
        }
        finally
        {
            leaked?.Dispose();
        }
    }

    [Fact] // orderly release hands the name back completely
    public void Release_MakesTheNextAcquireFullyPrimary()
    {
        Assert.Equal(InstanceRole.Primary, SingleInstance.Acquire(_name));
        SingleInstance.Release();

        Assert.Equal(InstanceRole.Primary, SingleInstance.Acquire(_name));
    }

    [Fact] // the wake-up handshake between a secondary and the primary
    public async Task NotifyExistingInstance_InvokesThePrimaryListener()
    {
        var eventName = _name + ".show";
        using var received = new SemaphoreSlim(0);

        SingleInstance.StartShowRequestListener(() => received.Release(), eventName);
        try
        {
            SingleInstance.NotifyExistingInstance(eventName);
            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)),
                "primary never received the show request");

            // AutoReset: a second launch later wakes the primary again.
            SingleInstance.NotifyExistingInstance(eventName);
            Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(5)),
                "second show request was lost");
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    [Fact] // notifying with no primary alive must be a silent no-op
    public void NotifyExistingInstance_WithNoPrimary_DoesNotThrow()
    {
        var ex = Record.Exception(() => SingleInstance.NotifyExistingInstance(_name + ".none"));
        Assert.Null(ex);
    }

    [Fact] // the startup gate: only a Secondary is turned away
    public void ShouldProceed_RejectsOnlySecondary()
    {
        Assert.True(SingleInstance.ShouldProceed(InstanceRole.Primary));
        Assert.True(SingleInstance.ShouldProceed(InstanceRole.PrimaryRecovered));
        Assert.True(SingleInstance.ShouldProceed(InstanceRole.PrimaryFailOpen));
        Assert.False(SingleInstance.ShouldProceed(InstanceRole.Secondary));
    }

    [Fact] // Release is part of orderly shutdown: it must be idempotent and never throw
    public void Release_IsIdempotent()
    {
        SingleInstance.Acquire(_name);
        var ex = Record.Exception(() =>
        {
            SingleInstance.Release();
            SingleInstance.Release();
        });
        Assert.Null(ex);
    }
}
