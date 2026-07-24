using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>
/// Serializes every test class that touches process-wide mutable state.
///
/// <para>Two such statics exist:</para>
/// <list type="bullet">
///   <item><description><see cref="ScrcpyDeck.Services.Log.DirOverride"/> — the log
///   redirection used to keep tests out of the real %AppData%.</description></item>
///   <item><description><see cref="ScrcpyDeck.Services.ScrcpyService"/> and
///   <see cref="ScrcpyDeck.Services.AppLifecycle"/> — a shared session registry and a
///   one-shot shutdown latch.</description></item>
/// </list>
///
/// <para>xUnit runs test CLASSES in parallel by default, so classes touching either static
/// corrupt each other: one class's Dispose restores a value another is still using, and
/// assertions then fail intermittently. A SINGLE collection is used for both concerns
/// rather than one per concern, because a class can only belong to one collection and
/// several classes (notably the shutdown tests) touch both.</para>
///
/// Tests inside one class already run sequentially, so nothing else is needed.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GlobalStateCollection
{
    public const string Name = "global-state";
}
