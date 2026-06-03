using Notification.API.Services;
using Notification.Domain.Entities;

namespace Notification.Tests;

public class NotificationRendererTests
{
    private readonly NotificationRenderer _renderer = new();

    private static OrderNotification CreateTestNotification(decimal orderTotal = 9999) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        OrderTotal = orderTotal,
        CustomerEmail = "test@example.com",
        CustomerName = "Jane Doe",
        Type = NotificationType.OrderConfirmation,
        Status = NotificationStatus.Pending,
        CreatedAt = new DateTime(2025, 6, 1, 14, 30, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void RenderNotification_ReturnsSubjectAndBody()
    {
        var notification = CreateTestNotification();
        var (subject, body) = _renderer.RenderNotification(notification);

        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public void RenderNotification_SubjectContainsOrderConfirmation()
    {
        var notification = CreateTestNotification();
        var (subject, _) = _renderer.RenderNotification(notification);

        Assert.Contains("Order Confirmed", subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RenderNotification_BodyContainsCustomerName()
    {
        var notification = CreateTestNotification();
        var (_, body) = _renderer.RenderNotification(notification);

        Assert.Contains("Jane Doe", body);
    }

    [Fact]
    public void RenderNotification_BodyContainsFormattedCurrency()
    {
        var notification = CreateTestNotification(orderTotal: 15000);
        var (_, body) = _renderer.RenderNotification(notification);

        // 15000 cents = $150.00
        Assert.Contains("150.00", body);
    }

    [Fact]
    public void RenderNotification_BodyIsValidHtml()
    {
        var notification = CreateTestNotification();
        var (_, body) = _renderer.RenderNotification(notification);

        Assert.StartsWith("<!DOCTYPE html>", body.TrimStart());
        Assert.Contains("</html>", body);
    }

    [Fact]
    public void RenderOrderConfirmation_ContainsOrderIdPrefix()
    {
        var notification = CreateTestNotification();
        var html = _renderer.RenderOrderConfirmation(notification);

        var expectedPrefix = notification.OrderId.ToString()[..8].ToUpper();
        Assert.Contains(expectedPrefix, html);
    }
}
