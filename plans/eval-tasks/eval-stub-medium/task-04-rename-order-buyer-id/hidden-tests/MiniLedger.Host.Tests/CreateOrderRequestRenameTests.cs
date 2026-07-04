using MiniLedger.Host.Endpoints;
using Xunit;

namespace MiniLedger.Host.Tests;

public sealed class CreateOrderRequestRenameTests
{
    [Fact]
    public void CreateOrderRequest_UsesBuyerIdFieldName()
    {
        var request = new CreateOrderRequest(BuyerId: "buyer-1");
        Assert.Equal("buyer-1", request.BuyerId);
    }
}
