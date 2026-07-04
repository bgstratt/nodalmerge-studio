using System.Linq;
using System.Threading.Tasks;
using MiniLedger.Api.Domain;
using MiniLedger.Api.Services;
using MiniLedger.Api.Storage;
using Xunit;

namespace MiniLedger.Tests;

public sealed class FulfillmentClaimTests
{
    [Fact]
    public void TryClaimForFulfillment_ConcurrentCallers_ExactlyOneSucceeds()
    {
        var service = new OrderService(new InMemoryOrderRepository());
        var order = service.CreateDraft("cust-1");
        service.AddLineItem(order.OrderId, new LineItem("SKU-1", "Widget", LineItemKind.Physical, 1, 5m));
        service.Place(order.OrderId);

        var results = new bool[20];
        Parallel.For(0, results.Length, i => results[i] = service.TryClaimForFulfillment(order.OrderId));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public void TryClaimForFulfillment_OnDraftOrder_ReturnsFalse()
    {
        var service = new OrderService(new InMemoryOrderRepository());
        var order = service.CreateDraft("cust-1");

        Assert.False(service.TryClaimForFulfillment(order.OrderId));
    }
}
