using MiniLedger.Contracts.Orders;
using MiniLedger.Contracts.Payments;
using MiniLedger.Core.Customers;
using MiniLedger.Core.Discounts;
using MiniLedger.Core.Orders;
using MiniLedger.Core.Payments;
using MiniLedger.Storage.InMemory;
using Xunit;

namespace MiniLedger.Core.Tests;

public sealed class PaymentCaptureTests
{
    [Fact]
    public void Capture_NewPayment_ReturnsCapturedRecord()
    {
        var payments = new PaymentService(new InMemoryPaymentRepository());

        var record = payments.Capture("ORD-1", 50m);

        Assert.Equal(PaymentStatus.Captured, record.Status);
        Assert.Equal(50m, record.Amount);
    }

    [Fact]
    public void Place_CapturesPaymentForOrderTotal()
    {
        var orderRepo = new InMemoryOrderRepository();
        var customers = new CustomerService(new InMemoryCustomerRepository());
        var discounts = new DiscountService();
        var payments = new PaymentService(new InMemoryPaymentRepository());
        var service = new OrderService(orderRepo, customers, discounts, payments);

        var order = service.CreateDraft("buyer-1");
        service.AddLineItem(order.OrderId, new LineItem("SKU-1", "Widget", LineItemKind.Physical, 1, 25m));

        var placed = service.Place(order.OrderId);

        Assert.NotNull(placed);
        Assert.Equal(OrderStatus.Placed, placed!.Status);
        var payment = payments.Get(order.OrderId);
        Assert.NotNull(payment);
        Assert.Equal(PaymentStatus.Captured, payment!.Status);
        Assert.Equal(25m, payment.Amount);
    }
}
