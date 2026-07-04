using MiniLedger.Api.Domain;
using Xunit;

namespace MiniLedger.Tests;

public sealed class DiscountRulesFixTests
{
    [Theory]
    [InlineData("WELCOME10", 90)]
    [InlineData("SAVE20", 80)]
    [InlineData("VIP30", 70)]
    public void Code_AppliesExactlyAdvertisedPercentOff(string code, decimal expectedTotal)
    {
        var result = DiscountRules.ApplyDiscount(100m, code);
        Assert.Equal(expectedTotal, result);
    }
}
