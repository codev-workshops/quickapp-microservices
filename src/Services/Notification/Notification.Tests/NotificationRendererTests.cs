using System.Globalization;
using Notification.API.Services;
using Notification.Domain.Entities;
using Xunit;

namespace Notification.Tests;

public class NotificationRendererTests
{
    [Fact]
    public void RenderOrderConfirmationPreservesDollarAmountInSubjectAndBody()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var notification = new OrderNotification
            {
                OrderId = Guid.NewGuid(),
                OrderTotal = 149.99m,
                Type = NotificationType.OrderConfirmation
            };

            var (subject, body) = new NotificationRenderer().RenderNotification(notification);

            Assert.Equal("Order Confirmed — $149.99", subject);
            Assert.Contains(">$149.99</td>", body);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
