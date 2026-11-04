using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.Dishes;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using APIHandler.Services.Notifications;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly Mock<INotificationService> _mockNotificationService = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object,
            _mockNotificationService.Object);
        _mockNotificationService
            .Setup(service => service.CreateReservationUpdateNotificationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>()))
            .Returns(Task.CompletedTask);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(
                It.IsAny<string>()))
            .ReturnsAsync([]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(
                It.Is<string>(id => id.StartsWith("owner-", StringComparison.Ordinal))))
            .ReturnsAsync((GuestSession?)null);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerAndGuestsExist_ReturnsParticipantsAndTotals()
    {
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        const string ownerUserId = "owner-1";

        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerUserId
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            OwnerUserId = ownerUserId,
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "dish-owner", Quantity = 1, State = OrderItemState.Pending }]
        };
        GuestSession guestSession = new()
        {
            Id = "guest-session",
            GroupReservationId = groupReservationId,
            GuestName = "Andrii",
            IsSubmitted = false,
            Dishes = [new OrderItem { DishId = "dish-guest", Quantity = 2, State = OrderItemState.Pending }]
        };
        Dish ownerDish = new() { Id = "dish-owner", Name = "Soup", Price = 80 };
        Dish guestDish = new() { Id = "dish-guest", Name = "Caesar Salad", Price = 180 };
        UserType owner = new()
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Olena" }]
        };

        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId))
            .ReturnsAsync([ownerSession]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([guestSession, ownerSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([ownerDish, guestDish]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(ownerUserId))
            .ReturnsAsync(owner);

        GroupSessionsResponse response = await _guestSessionService.GetGroupSessionsAsync(ownerUserId, groupReservationId);

        GroupSessionParticipantResponse[] participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal("owner-session", participants[0].Id);
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal("Olena", participants[0].Name);
        Assert.Equal(80m, participants[0].Total);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal("dish-owner", Assert.Single(participants[0].Items).Dish.Id);
        Assert.Equal(1, participants[0].Items.Single().Quantity);

        Assert.Equal("guest-session", participants[1].Id);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal("Andrii", participants[1].Name);
        Assert.Equal(360m, participants[1].Total);
        Assert.False(participants[1].IsSubmitted);
        GroupSessionItemResponse guestItem = Assert.Single(participants[1].Items);
        Assert.Equal("dish-guest", guestItem.Dish.Id);
        Assert.Equal("Caesar Salad", guestItem.Dish.Name);
        Assert.Equal(180m, guestItem.Dish.Price);
        Assert.Equal(2, guestItem.Quantity);
        Assert.Equal(440m, response.Total);

        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(groupReservationId),
            Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId),
            Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Never);
        _mockDishRepository.Verify(
            repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_SubmittedGroup_ShowsOwnerItemsWithoutSubmittedGuestItems()
    {
        const string groupReservationId = "group-submitted";
        const string reservationId = "reservation-submitted";
        const string ownerUserId = "owner-submitted";

        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Submitted
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerUserId
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session-submitted",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            OwnerUserId = ownerUserId,
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = []
        };
        GuestSession guestSession = new()
        {
            Id = "guest-session-submitted",
            GroupReservationId = groupReservationId,
            GuestName = "Nadin",
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "risotto", Quantity = 1, State = OrderItemState.Pending }]
        };
        Order order = new()
        {
            OrderId = "order-1",
            CustomerId = ownerUserId,
            ReservationId = reservationId,
            ReservationAddress = "Address",
            ReservationTime = "Time",
            Status = OrderStatus.SubmittedByCustomer,
            Dishes =
            [
                new OrderItem { DishId = "pizza", Quantity = 1, State = OrderItemState.Pending },
                new OrderItem { DishId = "risotto", Quantity = 2, State = OrderItemState.Pending },
                new OrderItem { DishId = "salmon", Quantity = 1, State = OrderItemState.Pending }
            ]
        };
        UserType owner = new()
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Test" }]
        };
        Mock<IOrderRepository> orderRepository = new();
        orderRepository
            .Setup(repository => repository.GetOrderByReservationIdAsync(reservationId))
            .ReturnsAsync(order);

        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId))
            .ReturnsAsync([ownerSession, guestSession]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([ownerSession, guestSession]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(
                It.Is<string>(id => id.StartsWith("owner-", StringComparison.Ordinal))))
            .ReturnsAsync(ownerSession);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(
            [
                new Dish { Id = "pizza", Name = "Margherita Pizza", Price = 14.99m },
                new Dish { Id = "risotto", Name = "Truffle Mushroom Risotto", Price = 22.50m },
                new Dish { Id = "salmon", Name = "Grilled Salmon 2", Price = 18.99m }
            ]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(ownerUserId))
            .ReturnsAsync(owner);

        GuestSessionService service = new(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object,
            _mockNotificationService.Object,
            orderRepository.Object);

        GroupSessionsResponse response = await service.GetGroupSessionsAsync(ownerUserId, groupReservationId);

        GroupSessionParticipantResponse[] participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal(56.48m, participants[0].Total);
        Assert.Equal(1, Assert.Single(participants[0].Items.Where(item => item.Dish.Id == "risotto")).Quantity);
        Assert.Equal(22.50m, participants[1].Total);
        Assert.Equal(78.98m, response.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerHasNoDishes_ReturnsEmptyItemsAndZeroTotals()
    {
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        const string ownerUserId = "owner-1";

        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerUserId
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            IsOwnerSession = true,
            Dishes = []
        };
        UserType owner = new()
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Olena" }]
        };

        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId))
            .ReturnsAsync([ownerSession]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([ownerSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(ownerUserId))
            .ReturnsAsync(owner);

        GroupSessionsResponse response = await _guestSessionService.GetGroupSessionsAsync(ownerUserId, groupReservationId);

        GroupSessionParticipantResponse participant = Assert.Single(response.Participants);
        Assert.Empty(participant.Items);
        Assert.Equal(0m, participant.Total);
        Assert.Equal(0m, response.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_GroupReservationDoesNotExist_ThrowsGroupReservationNotFoundException()
    {
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync("missing-group"))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync("owner-1", "missing-group"));

        _mockReservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ReservationDoesNotExist_ThrowsReservationNotFoundException()
    {
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "missing-reservation",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync("owner-1", groupReservation.GroupReservationId));
    }

    [Fact]
    public async Task GetGroupSessionsAsync_CallerIsNotReservationOwner_ThrowsActionForbiddenException()
    {
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "actual-owner"
        };
        SetupGroupReservationOwnership(groupReservation, reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetGroupSessionsAsync("different-user", groupReservation.GroupReservationId));

        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_GroupHasNoOwnerSession_BuildsOwnerFromParentReservation()
    {
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        Dish dish = new() { Id = "dish-1", Name = "Caesar Salad", Price = 180m };
        GuestSession guestSession = new()
        {
            Id = "guest-session",
            GroupReservationId = groupReservation.GroupReservationId,
            GuestName = "Andrii",
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = dish.Id, Quantity = 2, State = OrderItemState.Pending }]
        };
        UserType owner = new()
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Olena" }]
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([guestSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(ids => ids.Single() == dish.Id)))
            .ReturnsAsync([dish]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(reservation.CustomerId!))
            .ReturnsAsync(owner);

        GroupSessionsResponse response = await _guestSessionService.GetGroupSessionsAsync(
            reservation.CustomerId!,
            groupReservation.GroupReservationId);

        GroupSessionParticipantResponse[] participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);

        GroupSessionParticipantResponse ownerParticipant = participants[0];
        Assert.Null(ownerParticipant.Id);
        Assert.Equal(ParticipantType.Owner, ownerParticipant.Type);
        Assert.Equal("Olena", ownerParticipant.Name);
        Assert.Empty(ownerParticipant.Items);
        Assert.Equal(0m, ownerParticipant.Total);
        Assert.False(ownerParticipant.IsSubmitted);

        GroupSessionParticipantResponse guestParticipant = participants[1];
        Assert.Equal(guestSession.Id, guestParticipant.Id);
        Assert.Equal(ParticipantType.Guest, guestParticipant.Type);
        Assert.Equal("Andrii", guestParticipant.Name);
        Assert.Equal(360m, guestParticipant.Total);
        Assert.True(guestParticipant.IsSubmitted);
        GroupSessionItemResponse guestItem = Assert.Single(guestParticipant.Items);
        Assert.Equal(dish.Id, guestItem.Dish.Id);
        Assert.Equal(dish.Name, guestItem.Dish.Name);
        Assert.Equal(dish.Price, guestItem.Dish.Price);
        Assert.Equal(2, guestItem.Quantity);
        Assert.Equal(360m, response.Total);

        _mockUserRepository.Verify(
            repository => repository.GetUserByIdAsync(reservation.CustomerId!),
            Times.Once);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_OwnerHasNoGivenName_UsesDefaultOwnerName()
    {
        // Arrange
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session",
            GroupReservationId = groupReservation.GroupReservationId,
            OwnerUserId = reservation.CustomerId,
            GuestName = "Owner",
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = []
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(
                groupReservation.GroupReservationId))
            .ReturnsAsync([ownerSession]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([ownerSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(reservation.CustomerId!))
            .ReturnsAsync(new UserType { Attributes = [] });

        // Act
        GroupSessionsResponse response = await _guestSessionService.GetGroupSessionsAsync(
            reservation.CustomerId!,
            groupReservation.GroupReservationId);

        // Assert
        GroupSessionParticipantResponse owner = Assert.Single(response.Participants);
        Assert.Equal(ownerSession.Id, owner.Id);
        Assert.Equal(ParticipantType.Owner, owner.Type);
        Assert.Equal("Owner", owner.Name);
        Assert.Empty(owner.Items);
        Assert.Equal(0m, owner.Total);
        Assert.True(owner.IsSubmitted);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GuestSessionDeleted_ReturnsUpdatedSnapshotIncludingOwner()
    {
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        const string ownerUserId = "owner-1";
        const string guestSessionId = "guest-to-delete";

        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerUserId
        };
        GuestSession guestSessionToDelete = new()
        {
            Id = guestSessionId,
            GroupReservationId = groupReservationId,
            GuestName = "Guest to delete",
            Dishes = []
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            OwnerUserId = ownerUserId,
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "owner-dish", Quantity = 1, State = OrderItemState.Pending }]
        };
        GuestSession remainingGuestSession = new()
        {
            Id = "remaining-guest",
            GroupReservationId = groupReservationId,
            GuestName = "Andrii",
            IsSubmitted = false,
            Dishes = [new OrderItem { DishId = "guest-dish", Quantity = 2, State = OrderItemState.Pending }]
        };
        Dish ownerDish = new() { Id = "owner-dish", Name = "Soup", Price = 80m };
        Dish guestDish = new() { Id = "guest-dish", Name = "Caesar Salad", Price = 180m };
        UserType owner = new()
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Olena" }]
        };

        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .SetupSequence(repository => repository.GetByGuestSessionIdConsistentAsync(guestSessionId))
            .ReturnsAsync(guestSessionToDelete)
            .ReturnsAsync((GuestSession?)null);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId))
            .ReturnsAsync([ownerSession, remainingGuestSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([ownerDish, guestDish]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(ownerUserId))
            .ReturnsAsync(owner);

        GroupSessionsResponse response = await _guestSessionService.DeleteGuestSessionAsync(
            ownerUserId,
            groupReservationId,
            guestSessionId);

        GroupSessionParticipantResponse[] participants = response.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        GroupSessionParticipantResponse ownerParticipant = participants[0];
        Assert.Equal("owner-session", ownerParticipant.Id);
        Assert.Equal(ParticipantType.Owner, ownerParticipant.Type);
        Assert.Equal("Olena", ownerParticipant.Name);
        Assert.Equal(80m, ownerParticipant.Total);
        Assert.True(ownerParticipant.IsSubmitted);
        GroupSessionItemResponse ownerItem = Assert.Single(ownerParticipant.Items);
        Assert.Equal("owner-dish", ownerItem.Dish.Id);
        Assert.Equal("Soup", ownerItem.Dish.Name);
        Assert.Equal(80m, ownerItem.Dish.Price);
        Assert.Equal(1, ownerItem.Quantity);

        GroupSessionParticipantResponse guestParticipant = participants[1];
        Assert.Equal("remaining-guest", guestParticipant.Id);
        Assert.Equal(ParticipantType.Guest, guestParticipant.Type);
        Assert.Equal("Andrii", guestParticipant.Name);
        Assert.Equal(360m, guestParticipant.Total);
        Assert.False(guestParticipant.IsSubmitted);
        GroupSessionItemResponse guestItem = Assert.Single(guestParticipant.Items);
        Assert.Equal("guest-dish", guestItem.Dish.Id);
        Assert.Equal("Caesar Salad", guestItem.Dish.Name);
        Assert.Equal(180m, guestItem.Dish.Price);
        Assert.Equal(2, guestItem.Quantity);
        Assert.Equal(440m, response.Total);

        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(guestSessionToDelete),
            Times.Once);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_DuplicateOwnerSessionsRemainAfterDeletion_ThrowsDuplicateOwnerSessionException()
    {
        // Arrange
        const string groupReservationId = "group-1";
        const string guestSessionId = "guest-to-delete";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        GuestSession guestSessionToDelete = new()
        {
            Id = guestSessionId,
            GroupReservationId = groupReservationId,
            GuestName = "Guest to delete",
            Dishes = []
        };
        GuestSession firstOwnerSession = new()
        {
            Id = "legacy-owner-session-1",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            OwnerUserId = "owner-1",
            IsOwnerSession = true,
            Dishes = []
        };
        GuestSession secondOwnerSession = new()
        {
            Id = "legacy-owner-session-2",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            OwnerUserId = "owner-1",
            IsOwnerSession = true,
            Dishes = []
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .SetupSequence(repository => repository.GetByGuestSessionIdConsistentAsync(guestSessionId))
            .ReturnsAsync(guestSessionToDelete)
            .ReturnsAsync((GuestSession?)null);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId))
            .ReturnsAsync([firstOwnerSession, secondOwnerSession]);

        // Act
        DuplicateOwnerSessionException exception = await Assert.ThrowsAsync<DuplicateOwnerSessionException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupReservationId, guestSessionId));

        // Assert
        Assert.Equal(
            $"Multiple Owner sessions exist for group reservation '{groupReservationId}'.",
            exception.Message);
        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(guestSessionToDelete),
            Times.Once);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_OwnerSessionRequested_ThrowsValidationExceptionWithoutDeleting()
    {
        const string groupReservationId = "group-1";
        const string ownerUserId = "owner-1";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerUserId
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            IsOwnerSession = true,
            Dishes = []
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(ownerSession.Id))
            .ReturnsAsync(ownerSession);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync(ownerUserId, groupReservationId, ownerSession.Id));

        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GuestSessionDoesNotExist_ThrowsGuestSessionNotFoundException()
    {
        const string groupReservationId = "group-1";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync("missing-session"))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupReservationId, "missing-session"));

        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GuestSessionBelongsToAnotherGroup_ThrowsValidationExceptionWithoutDeleting()
    {
        const string groupReservationId = "group-1";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        GuestSession guestSession = new()
        {
            Id = "guest-session",
            GroupReservationId = "another-group",
            GuestName = "Guest",
            Dishes = []
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(guestSession.Id))
            .ReturnsAsync(guestSession);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupReservationId, guestSession.Id));

        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_GroupReservationDoesNotExist_ThrowsGroupReservationNotFoundExceptionWithoutSideEffects()
    {
        const string groupReservationId = "missing-group";
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupReservationId, "guest-1"));

        AssertNoDeleteSideEffects();
        _mockReservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_ParentReservationDoesNotExist_ThrowsReservationNotFoundExceptionWithoutSideEffects()
    {
        const string groupReservationId = "group-1";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "missing-reservation",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupReservationId, "guest-1"));

        AssertNoDeleteSideEffects();
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_CallerIsNotReservationOwner_ThrowsActionForbiddenExceptionWithoutSideEffects()
    {
        const string groupReservationId = "group-1";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        SetupGroupReservationOwnership(groupReservation, reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.DeleteGuestSessionAsync("another-user", groupReservationId, "guest-1"));

        AssertNoDeleteSideEffects();
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionStillExistsAfterDelete_ThrowsInvalidOperationExceptionWithoutBuildingSnapshot()
    {
        const string groupReservationId = "group-1";
        const string guestSessionId = "guest-to-delete";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        GuestSession guestSession = new()
        {
            Id = guestSessionId,
            GroupReservationId = groupReservationId,
            GuestName = "Guest",
            Dishes = []
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(guestSessionId))
            .ReturnsAsync(guestSession);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _guestSessionService.DeleteGuestSessionAsync("owner-1", groupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(guestSession), Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(It.IsAny<string>()),
            Times.Never);
        _mockDishRepository.Verify(
            repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_EventuallyConsistentReadIsStale_ReturnsSnapshotFromConsistentReadWithoutDeletedGuest()
    {
        const string groupReservationId = "group-1";
        const string guestSessionId = "guest-to-delete";
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = "reservation-1",
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = groupReservation.ReservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = "owner-1"
        };
        GuestSession deletedGuest = new()
        {
            Id = guestSessionId,
            GroupReservationId = groupReservationId,
            GuestName = "Deleted",
            Dishes = []
        };
        GuestSession ownerSession = new()
        {
            Id = "owner-session",
            GroupReservationId = groupReservationId,
            GuestName = "Owner",
            OwnerUserId = "owner-1",
            IsOwnerSession = true,
            Dishes = []
        };
        SetupGroupReservationOwnership(groupReservation, reservation);
        _mockGuestSessionRepository
            .SetupSequence(repository => repository.GetByGuestSessionIdConsistentAsync(guestSessionId))
            .ReturnsAsync(deletedGuest)
            .ReturnsAsync((GuestSession?)null);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([ownerSession, deletedGuest]);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupReservationId))
            .ReturnsAsync([ownerSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync("owner-1"))
            .ReturnsAsync(new UserType
            {
                Attributes = [new AttributeType { Name = "given_name", Value = "Olena" }]
            });

        GroupSessionsResponse response = await _guestSessionService.DeleteGuestSessionAsync(
            "owner-1",
            groupReservationId,
            guestSessionId);

        GroupSessionParticipantResponse participant = Assert.Single(response.Participants);
        Assert.Equal("owner-session", participant.Id);
        Assert.Equal(ParticipantType.Owner, participant.Type);
    }

    private void AssertNoDeleteSideEffects()
    {
        _mockGuestSessionRepository.Verify(
            repository => repository.GetByGuestSessionIdConsistentAsync(It.IsAny<string>()),
            Times.Never);
        _mockGuestSessionRepository.Verify(repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(It.IsAny<string>()),
            Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
        _mockDishRepository.Verify(
            repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()),
            Times.Never);
    }

    private void SetupGroupReservationOwnership(GroupReservation groupReservation, Reservation reservation)
    {
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync(reservation);
    }

    [Fact]
    public async Task UpdateGroupSessionDishAsync_SetsQuantityForSubmittedSessionAndPersistsUpdatedSnapshot()
    {
        const string groupId = "group-update";
        const string reservationId = "reservation-update";
        const string ownerId = "owner-update";
        const string sessionId = "guest-update";
        const string dishId = "dish-update";
        GroupReservation group = new()
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            CustomerId = ownerId,
            TableId = "table",
            WaiterId = "waiter"
        };
        GuestSession session = new()
        {
            Id = sessionId,
            GroupReservationId = groupId,
            GuestName = "Guest",
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = dishId, Quantity = 1, State = OrderItemState.Pending }]
        };
        SetupGroupReservationOwnership(group, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(sessionId))
            .ReturnsAsync(session);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(groupId))
            .ReturnsAsync([session]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([new Dish { Id = dishId, Name = "Soup", Price = 80 }]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(ownerId))
            .ReturnsAsync(new UserType { Attributes = [new AttributeType { Name = "given_name", Value = "Owner" }] });

        GroupSessionsResponse response = await _guestSessionService.UpdateGroupSessionDishAsync(
            ownerId, groupId, sessionId, dishId, 3);

        Assert.Equal(3, Assert.Single(session.Dishes).Quantity);
        Assert.Equal(240m, response.Total);
        _mockGuestSessionRepository.Verify(repository => repository.SaveCartAsync(session), Times.Once);
    }

    [Fact]
    public async Task UpdateGroupSessionDishAsync_ZeroQuantityRemovesDish()
    {
        const string groupId = "group-remove";
        const string reservationId = "reservation-remove";
        const string ownerId = "owner-remove";
        const string sessionId = "guest-remove";
        const string dishId = "dish-remove";
        GroupReservation group = new()
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            CustomerId = ownerId,
            TableId = "table",
            WaiterId = "waiter"
        };
        GuestSession session = new()
        {
            Id = sessionId,
            GroupReservationId = groupId,
            GuestName = "Guest",
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = dishId, Quantity = 1, State = OrderItemState.Pending }]
        };
        SetupGroupReservationOwnership(group, reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(sessionId))
            .ReturnsAsync(session);

        await _guestSessionService.UpdateGroupSessionDishAsync(ownerId, groupId, sessionId, dishId, 0);

        Assert.Empty(session.Dishes);
        _mockGuestSessionRepository.Verify(repository => repository.SaveCartAsync(session), Times.Once);
    }

    [Fact]
    public async Task UpdateGroupSessionDishAsync_NegativeQuantityIsRejectedWithoutSaving()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.UpdateGroupSessionDishAsync("owner", "group", "session", "dish", -1));

        _mockGuestSessionRepository.Verify(
            repository => repository.SaveCartAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;
        parentReservation.CustomerId = "owner-1";
        parentReservation.Status = APIHandler.Constants.ReservationStatus.Reserved;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveCartAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);
        _mockNotificationService
            .Setup(service => service.CreateReservationUpdateNotificationAsync(
                "owner-1",
                parentReservation.Id,
                $"{request.GuestName} joined your group pre-order.",
                null))
            .Returns(Task.CompletedTask);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
        _mockNotificationService.Verify(
            service => service.CreateReservationUpdateNotificationAsync(
                "owner-1",
                parentReservation.Id,
                $"{request.GuestName} joined your group pre-order.",
                null),
            Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveCartAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        session.OwnerUserId = customerId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [InlineData(false, "was not found.")]
    [InlineData(true, "has no 'given_name' attribute.")]
    public async Task GetGuestSessionInfoAsync_OwnerNameIsUnavailable_ReturnsOwnerAndLogsFallback(
        bool userExists,
        string expectedWarning)
    {
        // Arrange
        const string guestSessionId = "owner-session";
        const string groupReservationId = "group-1";
        const string reservationId = "reservation-1";
        const string ownerUserId = "owner-1";
        GuestSession session = new()
        {
            Id = guestSessionId,
            GroupReservationId = groupReservationId,
            GuestName = "Stored owner label",
            OwnerUserId = ownerUserId,
            IsOwnerSession = true,
            Dishes = []
        };
        GroupReservation groupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "reservation-token"
        };
        Reservation reservation = new()
        {
            Id = reservationId,
            TableId = "table-1",
            WaiterId = "waiter-1",
            CustomerId = ownerUserId
        };
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync(session);
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(ownerUserId))
            .ReturnsAsync(userExists ? new UserType { Attributes = [] } : null);
        using StringWriter capturedLog = new();
        TextWriter originalOutput = Console.Out;
        Console.SetOut(capturedLog);

        // Act
        GetGuestSessionResponse result;
        try
        {
            result = await _guestSessionService.GetGuestSessionInfoAsync(guestSessionId);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        // Assert
        Assert.Equal("Owner", result.Name);
        Assert.Contains("[WARN]", capturedLog.ToString());
        Assert.Contains(expectedWarning, capturedLog.ToString());
        Assert.Contains("Falling back to 'Owner'.", capturedLog.ToString());
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 0)]
    public async Task GetGroupReservationStatusAsync_StoredSessions_CountsGuestsPlusExactlyOneOwner(
        int guestSessionCount,
        int storedOwnerRows)
    {
        // Arrange
        int expected = guestSessionCount + 1;
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new() { Id = "reservation-1", TableId = "table-1", WaiterId = "waiter-1", GuestCount = 10 };
        List<GuestSession> sessions = [];
        for (int i = 0; i < storedOwnerRows; i++)
            sessions.Add(new GuestSession { Id = $"owner-{i}", GroupReservationId = "group-1", GuestName = "Owner", IsOwnerSession = true, Dishes = [] });
        for (int i = 0; i < guestSessionCount; i++)
            sessions.Add(new GuestSession { Id = $"guest-{i}", GroupReservationId = "group-1", GuestName = "Guest", IsOwnerSession = false, Dishes = [] });

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync("group-1"))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("reservation-1"))
            .ReturnsAsync(reservation);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync("group-1"))
            .ReturnsAsync(sessions);

        // Act
        var result = await _guestSessionService.GetGroupReservationStatusAsync("group-1");

        // Assert
        Assert.Equal(expected, result.CurrentParticipants);
        Assert.Equal(10, result.MaxCapacity);
    }

    [Fact]
    public async Task GetGroupReservationStatusAsync_PersistedOwnerAndOneGuestWithCapacityTwo_ReportsFullGroup()
    {
        // Arrange
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new() { Id = "reservation-1", TableId = "table-1", WaiterId = "waiter-1", GuestCount = 2 };
        GuestSession ownerSession = new() { Id = "owner-1", GroupReservationId = "group-1", GuestName = "Owner", IsOwnerSession = true, Dishes = [] };
        GuestSession guestSession = new() { Id = "guest-1", GroupReservationId = "group-1", GuestName = "Guest", IsOwnerSession = false, Dishes = [] };

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync("group-1"))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("reservation-1"))
            .ReturnsAsync(reservation);
        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync("group-1"))
            .ReturnsAsync([ownerSession, guestSession]);

        // Act
        var result = await _guestSessionService.GetGroupReservationStatusAsync("group-1");

        // Assert
        Assert.Equal(2, result.CurrentParticipants);
        Assert.Equal(2, result.MaxCapacity);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task CreateGuestSessionAsync_CapacityTwo_AllowsOnlyFirstGuest(int joinedGuests, bool expectedAllowed)
    {
        // Arrange
        CreateGuestSessionRequest request = new("group-1", "Guest");
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new() { Id = "reservation-1", TableId = "table-1", WaiterId = "waiter-1", GuestCount = 2, Status = APIHandler.Constants.ReservationStatus.Reserved };

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync("group-1"))
            .ReturnsAsync(groupReservation);
        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync("reservation-1"))
            .ReturnsAsync(reservation);
        // Repository contract: CountByGroupReservationId returns guests only (Owner rows excluded).
        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId("group-1"))
            .ReturnsAsync(joinedGuests);

        // Act
        Func<Task<CreatGuestSessionResponse>> act = () => _guestSessionService.CreateGuestSessionAsync(request);

        // Assert
        if (expectedAllowed)
        {
            CreatGuestSessionResponse response = await act();
            Assert.NotEmpty(response.SessionId);
            _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(It.IsAny<GuestSession>()), Times.Once);
        }
        else
        {
            await Assert.ThrowsAsync<GuestLimitExceededException>(act);
            _mockGuestSessionRepository.Verify(r => r.SaveCartAsync(It.IsAny<GuestSession>()), Times.Never);
        }
    }
}
