using MiniLedger.Api.Domain;
using MiniLedger.Api.Services;
using MiniLedger.Api.Storage;
using Xunit;

namespace MiniLedger.Tests;

public sealed class SubscriptionLineItemTests
{
    private static OrderService NewService() => new(new InMemoryOrderRepository());

    [Fact]
    public void AddLineItem_SubscriptionWithQuantityOne_Succeeds()
    {
        var service = NewService();
        var order = service.CreateDraft("cust-1");

        var result = service.AddLineItem(order.OrderId, new LineItem("SUB-1", "Pro Plan", LineItemKind.Subscription, 1, 20m));

        Assert.NotNull(result);
        Assert.Equal(20m, service.Get(order.OrderId)!.Subtotal);
    }

    [Fact]
    public void AddLineItem_SubscriptionWithQuantityOtherThanOne_IsRejected()
    {
        var service = NewService();
        var order = service.CreateDraft("cust-1");

        var result = service.AddLineItem(order.OrderId, new LineItem("SUB-1", "Pro Plan", LineItemKind.Subscription, 2, 20m));

        Assert.Null(result);
        Assert.Empty(service.Get(order.OrderId)!.LineItems);
    }
}
