using Notification.API.Services;
using Notification.Domain.Entities;

namespace Notification.API.Tests;

public class NotificationRendererTests
{
    [Theory]
    [InlineData(149.99, "$149.99")]
    [InlineData(1.50, "$1.50")]
    [InlineData(0, "$0.00")]
    [InlineData(1234567.891, "$1,234,567.89")]
    public void FormatCurrency_TreatsAmountAsDollars(decimal amount, string expected)
    {
        Assert.Equal(expected, NotificationRenderer.FormatCurrency(amount));
    }

    [Fact]
    public void FormatCurrency_DoesNotDivideByOneHundred()
    {
        var formatted = NotificationRenderer.FormatCurrency(149.99m);

        Assert.Equal("$149.99", formatted);
        Assert.NotEqual("$1.50", formatted);
    }

    [Fact]
    public void RenderNotification_OrderConfirmation_ShowsFullDollarAmountInSubjectAndBody()
    {
        var renderer = new NotificationRenderer();
        var notification = new OrderNotification
        {
            Id = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            OrderTotal = 149.99m,
            CustomerName = "Valued Customer",
            CustomerEmail = "customer@example.com",
            Type = NotificationType.OrderConfirmation,
            CreatedAt = new DateTime(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc)
        };

        var (subject, body) = renderer.RenderNotification(notification);

        Assert.Equal("Order Confirmed — $149.99", subject);
        Assert.Contains("$149.99", body);
        Assert.DoesNotContain("$1.50", body);
    }
}
