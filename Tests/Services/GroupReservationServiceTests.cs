using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using AutoFixture.Xunit2;
using Moq;

namespace APIHandler.Tests.Services;

public class GroupReservationServiceTests
{
    private readonly Mock<IGroupReservationRepository> _groupReservationRepositoryMock = new();
    private readonly Mock<ITableRepository> _tableRepositoryMock = new();
    private readonly Mock<IReservationRepository> _reservationRepositoryMock = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepositoryMock = new();
    private readonly Mock<IUserService> _userServiceMock = new();
    private readonly Mock<ILocationService> _locationServiceMock = new();
    private readonly Mock<IOrderRepository> _orderRepositoryMock = new();
    private readonly Mock<IGroupReservationSubmitRepository> _groupReservationSubmitRepositoryMock = new();

    private readonly GroupReservationService _service;

    public GroupReservationServiceTests()
    {
        _service = new GroupReservationService(
            _groupReservationRepositoryMock.Object,
            _tableRepositoryMock.Object,
            _reservationRepositoryMock.Object,
            _guestSessionRepositoryMock.Object,
            _userServiceMock.Object,
            _locationServiceMock.Object,
            _orderRepositoryMock.Object,
            _groupReservationSubmitRepositoryMock.Object);
    }

    [Fact]
    public async Task CreateGroupReservationIfDoesNotExistAsync_WhenGroupAlreadyExists_ReturnsExistingGroupWithoutSaving()
    {
        // Arrange
        GroupReservation existingGroup = CreateGroupReservation(GroupReservationStatus.Open);
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetByReservationIdAsync(existingGroup.ReservationId))
            .ReturnsAsync(existingGroup);

        // Act
        GroupReservation result = await _service.CreateGroupReservationIfDoesNotExistAsync(
            "owner-1",
            existingGroup.ReservationId);

        // Assert
        Assert.Same(existingGroup, result);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateGroupReservationIfDoesNotExistAsync_WhenGroupIsMissing_CreatesAndReturnsIt()
    {
        // Arrange
        const string reservationId = "reservation-new";
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        // Act
        GroupReservation result = await _service.CreateGroupReservationIfDoesNotExistAsync(
            "owner-1",
            reservationId);

        // Assert
        Assert.Equal(reservationId, result.ReservationId);
        Assert.False(string.IsNullOrWhiteSpace(result.GroupReservationId));
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.Is<GroupReservation>(group =>
                group.ReservationId == reservationId &&
                group.GroupReservationId == result.GroupReservationId)),
            Times.Once);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_SubmittedOwnerAndGuestSessions_MergesDishesAndSubmitsOrderAndGroup()
    {
        // Arrange
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        Order order = CreateOrder(group.ReservationId);
        GuestSession ownerSession = CreateSession("owner-session", true, true, ("dish-shared", 2), ("dish-owner", 1));
        GuestSession guestSession = CreateSession("guest-session", false, true, ("dish-shared", 3), ("dish-guest", 4));
        GuestSession unsubmittedSession = CreateSession("unsubmitted-session", false, false, ("dish-ignored", 9));

        SetupSubmitDependencies(group, reservation, order, ownerSession, guestSession, unsubmittedSession);

        // Act
        IReadOnlyList<GroupReservationOrderItemResponse> result = await _service.SubmitGroupReservationAsync(
            group.GroupReservationId,
            group.ReservationId,
            reservation.CustomerId!);

        // Assert
        Assert.Equal(GroupReservationStatus.Submitted, group.Status);
        Assert.Equal(OrderStatus.SubmittedByCustomer, order.Status);
        Assert.Equal(4, order.Dishes.Single(dish => dish.DishId == "dish-shared").Quantity);
        Assert.Equal(OrderItemState.Confirmed, order.Dishes.Single(dish => dish.DishId == "dish-shared").State);
        Assert.Equal(1, order.Dishes.Single(dish => dish.DishId == "dish-existing").Quantity);
        Assert.Equal(4, order.Dishes.Single(dish => dish.DishId == "dish-guest").Quantity);
        Assert.DoesNotContain(order.Dishes, dish => dish.DishId == "dish-owner");
        Assert.DoesNotContain(order.Dishes, dish => dish.DishId == "dish-ignored");
        Assert.Equal(new[] { "dish-shared", "dish-existing", "dish-guest" }, result.Select(item => item.DishId));
        Assert.Equal(4, result.Single(item => item.DishId == "dish-shared").Quantity);
        Assert.Equal(1, result.Single(item => item.DishId == "dish-existing").Quantity);
        Assert.Equal(4, result.Single(item => item.DishId == "dish-guest").Quantity);
        Assert.DoesNotContain(result, item => item.DishId == "dish-ignored");
        _groupReservationSubmitRepositoryMock.Verify(
            repository => repository.SubmitAsync(order, group, It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_GroupReservationNotFound_ThrowsNotFoundWithoutSubmitting()
    {
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync("missing-group"))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() =>
            _service.SubmitGroupReservationAsync("missing-group", "reservation-1", "owner-1"));

        VerifySubmitNotCalled();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ReservationNotFound_ThrowsNotFoundWithoutSubmitting()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        VerifySubmitNotCalled();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ReservationIdMismatch_ThrowsValidationWithoutSubmitting()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, "different-reservation", "owner-1"));

        VerifySubmitNotCalled();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_CallerIsNotReservationOwner_ThrowsForbiddenWithoutSubmitting()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "another-user"));

        VerifySubmitNotCalled();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ReservationHasNoCustomerId_ThrowsForbiddenWithoutCallingSubmitRepository()
    {
        // Arrange
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, customerId: null);
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(reservation);

        // Act
        ActionForbiddenException exception = await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        // Assert
        Assert.Equal("Only the reservation owner can submit the group pre-order.", exception.Message);
        _groupReservationSubmitRepositoryMock.Verify(
            repository => repository.BeginSubmitAsync(
                It.IsAny<GroupReservation>(),
                It.IsAny<string>()),
            Times.Never);
        VerifySubmitNotCalled();
    }

    [Theory]
    [InlineData(GroupReservationStatus.Closed)]
    [InlineData(GroupReservationStatus.Submitted)]
    public async Task SubmitGroupReservationAsync_GroupIsNotOpen_ThrowsConflictWithoutSubmitting(GroupReservationStatus status)
    {
        GroupReservation group = CreateGroupReservation(status);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<GroupReservationSubmitConflictException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        VerifySubmitNotCalled();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_NoSubmittedSessions_ThrowsValidationWithoutSubmitting()
    {
        // Arrange
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(group, reservation, CreateOrder(group.ReservationId),
            CreateSession("unsubmitted", false, false, ("dish-1", 1)));
        string? releasedAttemptId = null;
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.ReleaseSubmitAsync(
                group.GroupReservationId,
                It.IsAny<string>()))
            .Callback<string, string>((_, attemptId) => releasedAttemptId = attemptId)
            .Returns(Task.CompletedTask);

        // Act
        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        // Assert
        Assert.Equal("No submitted guest sessions were found.", exception.Message);
        VerifySubmitNotCalled();
        Assert.False(string.IsNullOrWhiteSpace(releasedAttemptId));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_OnlyOwnerSessionIsSubmitted_ThrowsValidationAndReleasesLease()
    {
        // Arrange
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        Order order = CreateOrder(group.ReservationId);
        GuestSession ownerSession = CreateSession("owner-session", true, true, ("dish-shared", 2));
        SetupSubmitDependencies(group, reservation, order, ownerSession);

        string? releasedAttemptId = null;
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.ReleaseSubmitAsync(
                group.GroupReservationId,
                It.IsAny<string>()))
            .Callback<string, string>((_, attemptId) => releasedAttemptId = attemptId)
            .Returns(Task.CompletedTask);

        // Act
        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        // Assert
        Assert.Equal("No submitted guest sessions were found.", exception.Message);
        Assert.Equal(1, order.Dishes.Single(dish => dish.DishId == "dish-shared").Quantity);
        VerifySubmitNotCalled();
        Assert.False(string.IsNullOrWhiteSpace(releasedAttemptId));
        _groupReservationSubmitRepositoryMock.Verify(
            repository => repository.ReleaseSubmitAsync(group.GroupReservationId, releasedAttemptId!),
            Times.Once);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_SubmittedSessionsContainNoDishes_ThrowsValidationWithoutSubmitting()
    {
        // Arrange
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(group, reservation, CreateOrder(group.ReservationId),
            CreateSession("submitted-empty", false, true));

        // Act
        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        // Assert
        Assert.Equal("Submitted guest sessions contain no dishes.", exception.Message);
        VerifySubmitNotCalled();
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_OrderNotFound_ThrowsValidationWithoutSubmitting()
    {
        // Arrange
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        GuestSession session = CreateSession("submitted", false, true, ("dish-1", 1));
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(group.GroupReservationId))
            .ReturnsAsync([session]);
        _orderRepositoryMock
            .Setup(repository => repository.GetOrderByReservationIdAsync(group.ReservationId))
            .ReturnsAsync((Order?)null);

        // Act
        ValidationException exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        // Assert
        Assert.Equal("Order was not found for the reservation.", exception.Message);
        VerifySubmitNotCalled();
    }

    private void SetupSubmitDependencies(
        GroupReservation group,
        Reservation reservation,
        Order order,
        params GuestSession[] sessions)
    {
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(group.GroupReservationId))
            .ReturnsAsync(group);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(group.ReservationId))
            .ReturnsAsync(reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(group.GroupReservationId))
            .ReturnsAsync(sessions.ToList());
        _orderRepositoryMock
            .Setup(repository => repository.GetOrderByReservationIdAsync(group.ReservationId))
            .ReturnsAsync(order);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_BeginsSubmitBeforeReadingSessionsConsistently()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        List<string> operations = [];
        SetupSubmitDependencies(
            group,
            reservation,
            CreateOrder(group.ReservationId),
            CreateSession("submitted", false, true, ("dish-1", 1)));
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.BeginSubmitAsync(group, It.IsAny<string>()))
            .Callback<GroupReservation, string>((_, _) => operations.Add("begin"))
            .Returns(Task.CompletedTask);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(group.GroupReservationId))
            .Callback<string>(_ => operations.Add("read-consistently"))
            .ReturnsAsync([CreateSession("submitted", false, true, ("dish-1", 1))]);

        await _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1");

        Assert.Equal(new[] { "begin", "read-consistently" }, operations);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_BeginSubmitConflicts_DoesNotReadSessionsOrReleaseSubmit()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Submitting);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(group, reservation, CreateOrder(group.ReservationId));
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.BeginSubmitAsync(group, It.IsAny<string>()))
            .ThrowsAsync(new GroupReservationSubmitConflictException());

        await Assert.ThrowsAsync<GroupReservationSubmitConflictException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(group.GroupReservationId),
            Times.Never);
        _groupReservationSubmitRepositoryMock.Verify(
            repository => repository.ReleaseSubmitAsync(group.GroupReservationId, It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_SubmittingGroup_ReclaimsThroughBeginAndUsesSameAttemptForSubmit()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Submitting);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(
            group,
            reservation,
            CreateOrder(group.ReservationId),
            CreateSession("submitted", false, true, ("dish-1", 1)));
        string? begunAttemptId = null;
        string? submittedAttemptId = null;
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.BeginSubmitAsync(group, It.IsAny<string>()))
            .Callback<GroupReservation, string>((_, attemptId) => begunAttemptId = attemptId)
            .Returns(Task.CompletedTask);
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.SubmitAsync(
                It.IsAny<Order>(),
                group,
                It.IsAny<string>()))
            .Callback<Order, GroupReservation, string>((_, _, attemptId) => submittedAttemptId = attemptId)
            .Returns(Task.CompletedTask);

        await _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1");

        Assert.False(string.IsNullOrWhiteSpace(begunAttemptId));
        Assert.Equal(begunAttemptId, submittedAttemptId);
        Assert.Equal(GroupReservationStatus.Submitted, group.Status);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_SubmissionFailsAndReleaseFails_SurfacesBothFailures()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(
            group,
            reservation,
            CreateOrder(group.ReservationId),
            CreateSession("submitted", false, true, ("dish-1", 1)));
        InvalidOperationException submissionFailure = new("submit failed");
        InvalidOperationException releaseFailure = new("release failed");
        string? releasedAttemptId = null;
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.SubmitAsync(
                It.IsAny<Order>(),
                group,
                It.IsAny<string>()))
            .ThrowsAsync(submissionFailure);
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.ReleaseSubmitAsync(
                group.GroupReservationId,
                It.IsAny<string>()))
            .Callback<string, string>((_, attemptId) => releasedAttemptId = attemptId)
            .ThrowsAsync(releaseFailure);

        AggregateException exception = await Assert.ThrowsAsync<AggregateException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        Assert.Contains(submissionFailure, exception.InnerExceptions);
        Assert.Contains(releaseFailure, exception.InnerExceptions);
        Assert.False(string.IsNullOrWhiteSpace(releasedAttemptId));
        Assert.Equal(GroupReservationStatus.Submitting, group.Status);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_SubmitConflictAndLeaseReleaseConflict_PreservesSubmissionConflict()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(
            group,
            reservation,
            CreateOrder(group.ReservationId),
            CreateSession("submitted", false, true, ("dish-1", 1)));
        GroupReservationSubmitConflictException submissionConflict = new();
        string? begunAttemptId = null;
        string? releasedAttemptId = null;
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.BeginSubmitAsync(group, It.IsAny<string>()))
            .Callback<GroupReservation, string>((_, attemptId) => begunAttemptId = attemptId)
            .Returns(Task.CompletedTask);
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.SubmitAsync(
                It.IsAny<Order>(),
                group,
                It.IsAny<string>()))
            .ThrowsAsync(submissionConflict);
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.ReleaseSubmitAsync(
                group.GroupReservationId,
                It.IsAny<string>()))
            .Callback<string, string>((_, attemptId) => releasedAttemptId = attemptId)
            .ThrowsAsync(new ConditionalCheckFailedException("Submit lease is no longer owned."));

        GroupReservationSubmitConflictException exception = await Assert.ThrowsAsync<GroupReservationSubmitConflictException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        Assert.Same(submissionConflict, exception);
        Assert.False(string.IsNullOrWhiteSpace(begunAttemptId));
        Assert.Equal(begunAttemptId, releasedAttemptId);
        Assert.Equal(GroupReservationStatus.Submitting, group.Status);
        _groupReservationSubmitRepositoryMock.Verify(
            repository => repository.ReleaseSubmitAsync(
                group.GroupReservationId,
                It.Is<string>(attemptId => attemptId == begunAttemptId)),
            Times.Once);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_BeginFailsAmbiguously_AttemptsOwnedAttemptRollback()
    {
        GroupReservation group = CreateGroupReservation(GroupReservationStatus.Open);
        Reservation reservation = CreateReservation(group.ReservationId, "owner-1");
        SetupSubmitDependencies(group, reservation, CreateOrder(group.ReservationId));
        InvalidOperationException beginFailure = new("begin response was lost");
        string? begunAttemptId = null;
        string? releasedAttemptId = null;
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.BeginSubmitAsync(group, It.IsAny<string>()))
            .Callback<GroupReservation, string>((_, attemptId) => begunAttemptId = attemptId)
            .ThrowsAsync(beginFailure);
        _groupReservationSubmitRepositoryMock
            .Setup(repository => repository.ReleaseSubmitAsync(
                group.GroupReservationId,
                It.IsAny<string>()))
            .Callback<string, string>((_, attemptId) => releasedAttemptId = attemptId)
            .Returns(Task.CompletedTask);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SubmitGroupReservationAsync(group.GroupReservationId, group.ReservationId, "owner-1"));

        Assert.Same(beginFailure, exception);
        Assert.False(string.IsNullOrWhiteSpace(begunAttemptId));
        Assert.Equal(begunAttemptId, releasedAttemptId);
        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(group.GroupReservationId),
            Times.Never);
    }

    private void VerifySubmitNotCalled() =>
        _groupReservationSubmitRepositoryMock.Verify(
            repository => repository.SubmitAsync(
                It.IsAny<Order>(),
                It.IsAny<GroupReservation>(),
                It.IsAny<string>()),
            Times.Never);

    private static GroupReservation CreateGroupReservation(GroupReservationStatus status) => new()
    {
        GroupReservationId = "group-1",
        ReservationId = "reservation-1",
        ReservationToken = "token-1",
        Status = status
    };

    private static Reservation CreateReservation(string reservationId, string? customerId) => new()
    {
        Id = reservationId,
        TableId = "table-1",
        CustomerId = customerId,
        WaiterId = "waiter-1"
    };

    private static Order CreateOrder(string reservationId) => new()
    {
        OrderId = "order-1",
        CustomerId = "owner-1",
        ReservationId = reservationId,
        ReservationAddress = "address",
        ReservationTime = "time",
        Dishes =
        [
            new OrderItem { DishId = "dish-shared", Quantity = 1, State = OrderItemState.Confirmed },
                new OrderItem { DishId = "dish-existing", Quantity = 1, State = OrderItemState.Pending }
        ]
    };

    private static GuestSession CreateSession(
        string id,
        bool isOwner,
        bool isSubmitted,
        params (string DishId, int Quantity)[] dishes) => new()
        {
            Id = id,
            GroupReservationId = "group-1",
            GuestName = id,
            IsOwnerSession = isOwner,
            IsSubmitted = isSubmitted,
            Dishes = dishes.Select(dish => new OrderItem
            {
                DishId = dish.DishId,
                Quantity = dish.Quantity,
                State = OrderItemState.Pending
            }).ToList()
        };

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_ValidInput_ReturnsGroupReservationInfo(
        GroupReservation groupReservation,
        Table table,
        Reservation reservation,
        Location location,
        string expectedUserName)
    {
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;
        reservation.TableId = table.Id;
        table.LocationId = location.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(0);

        _tableRepositoryMock
            .Setup(r => r.GetTableAsync(table.Id))
            .ReturnsAsync(table);

        _locationServiceMock
            .Setup(s => s.GetLocationByIdAsync(table.LocationId))
            .ReturnsAsync(location);

        _userServiceMock
            .Setup(s => s.GetUserName(reservation.CustomerId!))
            .ReturnsAsync(expectedUserName);

        var result = await _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken);

        Assert.Equal(groupReservation.GroupReservationId, result.GroupReservationId);
        Assert.Equal(table.LocationId, result.LocationId);
        Assert.Equal(reservation.GuestCount, result.MaxGuestCount);
        Assert.Equal(reservation.StartTime, result.StartTime);
        Assert.Equal(reservation.EndTime, result.EndTime);
        Assert.Equal(table.TableNumber, result.TableNumber);
        Assert.Equal("Green & Tasty", result.RestaurantName);
        Assert.Equal(location.Address, result.LocationAddress);
        Assert.Equal(expectedUserName, result.InviterName);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_InvalidToken_ThrowsNotFound(string token)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(token))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _service.GetGroupReservationByTokenAsync(token));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed)]
    [InlineAutoData(GroupReservationStatus.Submitted)]
    public async Task GetGroupReservationByTokenAsync_GroupReservationIsNotOpen_ThrowsException(
        GroupReservationStatus status,
        GroupReservation groupReservation)
    {
        groupReservation.Status = status;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_WhenGuestCountIsMax_ThrowsGuestLimitExceeded(GroupReservation groupReservation, Reservation reservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = reservation.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(reservation.GuestCount);

        await Assert.ThrowsAsync<GuestLimitExceededException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task GetGroupReservationByTokenAsync_CapacityTwo_AllowsOnlyFirstGuest(int joinedGuests, bool expectedAllowed)
    {
        // Arrange
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };
        Reservation reservation = new() { Id = "reservation-1", GuestCount = 2, TableId = "table-1", WaiterId = "waiter-1", CustomerId = null };
        Table table = new() { Id = "table-1", LocationId = "location-1" };

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync("token-1"))
            .ReturnsAsync(groupReservation);
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync("reservation-1"))
            .ReturnsAsync(reservation);
        // Repository contract: only guest sessions are counted (Owner rows excluded).
        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId("group-1"))
            .ReturnsAsync(joinedGuests);
        _tableRepositoryMock
            .Setup(r => r.GetTableAsync("table-1"))
            .ReturnsAsync(table);

        // Act
        Func<Task<APIHandler.Models.GuestReservation.GetGroupReservationResponse>> act = () => _service.GetGroupReservationByTokenAsync("token-1");

        // Assert
        if (expectedAllowed)
        {
            var response = await act();
            Assert.Equal(2, response.MaxGuestCount);
        }
        else
        {
            await Assert.ThrowsAsync<GuestLimitExceededException>(act);
        }
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_ValidInput_UpdatesGroupReservationStatus(GroupReservation groupReservation)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(groupReservation.ReservationId))
            .ReturnsAsync(groupReservation);

        GroupReservation? savedGroupReservation = null;
        _groupReservationRepositoryMock
            .Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(r => savedGroupReservation = r);

        await _service.CloseGroupReservationAsync(groupReservation.ReservationId);

        Assert.NotNull(savedGroupReservation);
        Assert.Equal(GroupReservationStatus.Closed, savedGroupReservation.Status);
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_GroupReservationNotFound_DoesNotThrowException(string reservationId)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        await _service.CloseGroupReservationAsync(reservationId);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_ValidInput_ReturnsToken(Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        GetGroupInviteTokenResponse result = await _service.GetGroupReservationInviteTokenAsync(reservation.Id);

        Assert.Equal(groupReservation.ReservationToken, result.Token);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_InvalidReservationId_ThrowsNotFound(string reservationId)
    {
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.GetGroupReservationInviteTokenAsync(reservationId));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_OneGuest_ThrowsValidationException(Reservation reservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(ReservationStatus.Completed)]
    [InlineAutoData(ReservationStatus.Cancelled)]
    [InlineAutoData(ReservationStatus.InProgress)]
    public async Task GetGroupReservationInviteTokenAsync_StatusIsNotReserved_ThrowsValidationException(ReservationStatus reservationStatus, Reservation reservation)
    {
        reservation.Status = reservationStatus;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    public async Task GetGroupReservationInviteTokenAsync_GroupStatusIsNotOpen_ThrowsException(GroupReservationStatus status, Type type, Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = status;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(type, () => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }
}
