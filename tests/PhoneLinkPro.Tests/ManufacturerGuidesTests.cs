using ScrcpyDeck.Services;
using Xunit;

namespace PhoneLinkPro.Tests;

/// <summary>Batch 4B — the manufacturer guide data table backing the setup wizard's selector.</summary>
public sealed class ManufacturerGuidesTests
{
    [Fact]
    public void All_HasEveryRequiredGuideGroup()
    {
        var ids = ManufacturerGuides.All.Select(g => g.Id).ToList();
        Assert.Contains("samsung", ids);
        Assert.Contains("xiaomi", ids);
        Assert.Contains("pixel", ids);
        Assert.Contains("oppo", ids);
        Assert.Contains("huawei", ids);
        Assert.Contains("other", ids);
    }

    [Fact]
    public void All_IdsAreUnique()
        => Assert.Equal(ManufacturerGuides.All.Count, ManufacturerGuides.All.Select(g => g.Id).Distinct().Count());

    [Fact]
    public void Default_IsTheGenericOtherGuide()
        => Assert.Equal("other", ManufacturerGuides.Default.Id);

    [Theory]
    [InlineData("samsung")]
    [InlineData("xiaomi")]
    [InlineData("pixel")]
    [InlineData("oppo")]
    [InlineData("huawei")]
    [InlineData("other")]
    public void EveryGuide_HasNonEmptyInstructionsForBothSteps(string id)
    {
        var guide = ManufacturerGuides.ById(id);
        Assert.False(string.IsNullOrWhiteSpace(guide.DisplayName));
        Assert.NotEmpty(guide.DeveloperOptions.Bullets);
        Assert.NotEmpty(guide.UsbDebugging.Bullets);
        Assert.All(guide.DeveloperOptions.Bullets, b => Assert.False(string.IsNullOrWhiteSpace(b)));
        Assert.All(guide.UsbDebugging.Bullets, b => Assert.False(string.IsNullOrWhiteSpace(b)));
    }

    [Fact]
    public void ById_UnknownId_FallsBackToDefault()
        => Assert.Same(ManufacturerGuides.Default, ManufacturerGuides.ById("not-a-real-manufacturer"));

    [Fact]
    public void ById_NullId_FallsBackToDefault()
        => Assert.Same(ManufacturerGuides.Default, ManufacturerGuides.ById(null));

    [Fact] // Phase 4: never a single universal absolute path claim
    public void Guides_AcknowledgeVariationRatherThanClaimingOneUniversalPath()
    {
        var other = ManufacturerGuides.ById("other");
        var allText = string.Join(" ", other.DeveloperOptions.Bullets.Concat(other.UsbDebugging.Bullets));
        Assert.Contains("تختلف", allText);
    }
}
