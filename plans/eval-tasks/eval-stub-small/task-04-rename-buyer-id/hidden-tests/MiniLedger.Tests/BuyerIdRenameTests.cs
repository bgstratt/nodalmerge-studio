using MiniLedger.Api.Endpoints;
using MiniLedger.Api.Services;
using MiniLedger.Api.Storage;
using Xunit;

namespace MiniLedger.Tests;

public sealed class BuyerIdRenameTests
{
    [Fact]
    public void CreateOrderRequest_UsesBuyerIdFieldName()
    {
        var request = new CreateOrderRequest(BuyerId: "cust-1");
        Assert.Equal("cust-1", request.BuyerId);
    }

    [Fact]
    public void Order_ExposesBuyerIdProperty()
    {
        var service = new OrderService(new InMemoryOrderRepository());
        var order = service.CreateDraft("cust-1");

        Assert.Equal("cust-1", order.BuyerId);
    }
}
