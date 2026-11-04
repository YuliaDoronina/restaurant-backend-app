using APIHandler.Entities;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.Locations;
using APIHandler.Repositories.Notifications;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.Notifications;
using Moq;

namespace APIHandler.Tests.Services;

public class NotificationServiceTests
{
    [Fact]
    public async Task CreateReservationUpdateNotificationAsync_SavesUnreadOwnerNotificationWithoutOrder()
    {
        Mock<INotificationRepository> notificationRepository = new();
        Notification? savedNotification = null;
        notificationRepository
            .Setup(repository => repository.SaveNotificationAsync(It.IsAny<Notification>()))
            .Callback<Notification>(notification => savedNotification = notification)
            .Returns(Task.CompletedTask);

        NotificationService service = new(
            notificationRepository.Object,
            Mock.Of<IReservationRepository>(),
            Mock.Of<IOrderRepository>(),
            Mock.Of<ILocationRepository>(),
            Mock.Of<IDishRepository>());

        await service.CreateReservationUpdateNotificationAsync(
            "customer-1",
            "reservation-1",
            "A guest joined your group pre-order.");

        Assert.NotNull(savedNotification);
        Assert.Equal("customer-1", savedNotification.UserId);
        Assert.Equal("reservation-1", savedNotification.ReservationId);
        Assert.Null(savedNotification.OrderId);
        Assert.Equal("A guest joined your group pre-order.", savedNotification.Message);
        Assert.False(savedNotification.IsRead);
    }
}
