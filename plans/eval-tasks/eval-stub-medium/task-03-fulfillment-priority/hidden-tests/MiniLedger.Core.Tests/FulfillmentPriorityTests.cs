using System.Linq;
using MiniLedger.Contracts.Customers;
using MiniLedger.Core.Fulfillment;
using MiniLedger.Storage.InMemory;
using Xunit;

namespace MiniLedger.Core.Tests;

public sealed class FulfillmentPriorityTests
{
    [Fact]
    public void ListPending_OrdersByTierDescending()
    {
        var coordinator = new FulfillmentCoordinator(new InMemoryFulfillmentRepository());
        coordinator.Enqueue("ORD-standard", CustomerTier.Standard);
        coordinator.Enqueue("ORD-vip", CustomerTier.Vip);
        coordinator.Enqueue("ORD-gold", CustomerTier.Gold);

        var pending = coordinator.ListPending().Select(t => t.OrderId).ToArray();

        Assert.Equal(["ORD-vip", "ORD-gold", "ORD-standard"], pending);
    }

    [Fact]
    public void ListPending_SameTier_PreservesCreationOrder()
    {
        var coordinator = new FulfillmentCoordinator(new InMemoryFulfillmentRepository());
        coordinator.Enqueue("ORD-first", CustomerTier.Standard);
        coordinator.Enqueue("ORD-second", CustomerTier.Standard);

        var pending = coordinator.ListPending().Select(t => t.OrderId).ToArray();

        Assert.Equal(["ORD-first", "ORD-second"], pending);
    }
}
