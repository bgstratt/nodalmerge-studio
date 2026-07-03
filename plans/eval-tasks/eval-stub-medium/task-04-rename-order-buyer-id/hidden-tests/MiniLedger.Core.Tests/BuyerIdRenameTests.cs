using MiniLedger.Contracts.Customers;
using MiniLedger.Core.Customers;
using MiniLedger.Core.Discounts;
using MiniLedger.Core.Orders;
using MiniLedger.Storage.InMemory;
using Xunit;

namespace MiniLedger.Core.Tests;

public sealed class BuyerIdRenameTests
{
    [Fact]
    public void Order_ExposesBuyerIdProperty()
    {
        var service = new OrderService(
            new InMemoryOrderRepository(),
            new CustomerService(new InMemoryCustomerRepository()),
            new DiscountService());

        var order = service.CreateDraft("buyer-1");

        Assert.Equal("buyer-1", order.BuyerId);
    }

    [Fact]
    public void Customer_StillExposesCustomerIdPrimaryKey()
    {
        // Regression guard: Customer's own primary key is a distinct field from Order's
        // buyer reference and must NOT be renamed by this task.
        var customer = new Customer { CustomerId = "cust-1", Name = "Ada" };
        Assert.Equal("cust-1", customer.CustomerId);
    }
}
