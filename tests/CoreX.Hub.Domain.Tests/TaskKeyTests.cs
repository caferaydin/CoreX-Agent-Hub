using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Domain.Tests;

/// <summary>Approved Phase 0 format decision: &lt;PROJECT&gt;-&lt;3 digit&gt;.</summary>
public sealed class TaskKeyTests
{
    [Theory]
    [InlineData("HUB-001", "HUB", 1)]
    [InlineData("N360-014", "N360", 14)]
    [InlineData("GATE-999", "GATE", 999)]
    [InlineData("KRN-007", "KRN", 7)]
    public void Valid_keys_parse(string value, string expectedPrefix, int expectedNumber)
    {
        var key = TaskKey.Parse(value);

        Assert.Equal(expectedPrefix, key.Prefix);
        Assert.Equal(expectedNumber, key.Number);
        Assert.Equal(value, key.Value);
    }

    [Theory]
    [InlineData("HUB-1")]
    [InlineData("HUB-0001")]
    [InlineData("hub-001")]
    [InlineData("HUB001")]
    [InlineData("HUB-01A")]
    [InlineData("H-001")]
    [InlineData("-001")]
    [InlineData("HUB-")]
    [InlineData("1HUB-001")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_keys_are_rejected(string? value)
    {
        Assert.False(TaskKey.TryParse(value, out _));
        Assert.Throws<DomainException>(() => TaskKey.Parse(value));
    }

    [Fact]
    public void Prefix_is_not_hard_coded()
    {
        // Any well formed prefix is accepted by the value object; whether it is allowed is
        // decided by project configuration, not by the domain type.
        Assert.True(TaskKey.TryParse("ZZTOP-042", out var key));
        Assert.Equal("ZZTOP", key.Prefix);
        Assert.True(TaskKey.IsValidPrefix("ZZTOP"));
        Assert.False(TaskKey.IsValidPrefix("zztop"));
        Assert.False(TaskKey.IsValidPrefix("Z"));
    }

    [Fact]
    public void Keys_compare_by_value()
    {
        Assert.Equal(TaskKey.Parse("HUB-001"), TaskKey.Parse("HUB-001"));
        Assert.NotEqual(TaskKey.Parse("HUB-001"), TaskKey.Parse("HUB-002"));
    }
}
