using ComputeWarden.Core.Config;

namespace ComputeWarden.Tests;

public class LeaseOptionsTests
{
    [Theory]
    [InlineData(null, 300)]   // unspecified -> default
    [InlineData(10, 30)]      // below min -> min
    [InlineData(300, 300)]    // in range -> unchanged
    [InlineData(99999, 3600)] // above max -> max
    public void Resolve_clamps_to_bounds(int? requested, int expected)
    {
        var options = new LeaseOptions { DefaultSeconds = 300, MinimumSeconds = 30, MaximumSeconds = 3600 };
        Assert.Equal(expected, options.Resolve(requested));
    }

    [Fact]
    public void Resolve_tolerates_inverted_min_max_never_below_minimum()
    {
        // Misconfigured: minimum > maximum. Result must never drop below the guaranteed minimum.
        var options = new LeaseOptions { DefaultSeconds = 100, MinimumSeconds = 100, MaximumSeconds = 50 };

        Assert.Equal(100, options.Resolve(75));   // would previously have returned 100 anyway
        Assert.Equal(100, options.Resolve(200));  // previously wrongly returned 50 (< minimum)
        Assert.Equal(100, options.Resolve(null));
    }
}
