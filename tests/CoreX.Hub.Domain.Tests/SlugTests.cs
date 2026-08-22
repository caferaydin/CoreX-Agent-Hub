using CoreX.Hub.Domain.Common;

namespace CoreX.Hub.Domain.Tests;

public sealed class SlugTests
{
    [Theory]
    [InlineData("hub-dev")]
    [InlineData("hub-sample-repo")]
    [InlineData("n360")]
    [InlineData("isolation-gate-fixture")]
    public void Valid_slugs_parse(string value) => Assert.Equal(value, Slug.Parse(value).Value);

    [Theory]
    [InlineData("Hub-Dev")]
    [InlineData("hub_dev")]
    [InlineData("-hub")]
    [InlineData("hub-")]
    [InlineData("hub--dev")]
    [InlineData("h")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_slugs_are_rejected(string? value)
    {
        Assert.False(Slug.TryParse(value, out _));
        Assert.Throws<DomainException>(() => Slug.Parse(value));
    }
}
