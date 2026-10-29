using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.Reservations;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using Moq;

namespace APIHandler.Tests.Services;

public class ReservationGuestSessionIdServiceTests
{
    private const string ReservationId = "reservation-1";
    private const string GroupReservationId = "group-reservation-1";
    private const string OwnerUserId = "owner-1";

    private readonly Mock<IReservationRepository> _reservationRepository = new();
    private readonly Mock<IGroupReservationRepository> _groupReservationRepository = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepository = new();
    private readonly GuestSessionService _service;

    public ReservationGuestSessionIdServiceTests()
    {
        _service = new GuestSessionService(
            _groupReservationRepository.Object,
            _guestSessionRepository.Object,
            Mock.Of<IDishRepository>(),
            _reservationRepository.Object,
            Mock.Of<IUserRepository>(),
            Mock.Of<ITableRepository>());
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_ReservationOwnerAndOwnerSessionExist_ReturnsOwnerSessionId()
    {
        Reservation reservation = CreateReservation(customerId: OwnerUserId);
        GroupReservation groupReservation = CreateGroupReservation();
        GuestSession guestSession = CreateSession("guest-session", isOwnerSession: false);
        GuestSession ownerSession = CreateSession("owner-session", isOwnerSession: true);
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(reservation);
        _groupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync(groupReservation);
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId))
            .ReturnsAsync([guestSession, ownerSession]);

        ReservationGuestSessionIdResponse response =
            await _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId);

        Assert.Equal("owner-session", response.GuestSessionId);
        _reservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(ReservationId),
            Times.Once);
        _groupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(ReservationId),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId),
            Times.Once);
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_CallerIsNotReservationOwner_ThrowsForbiddenWithoutReadingSessions()
    {
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: "another-user"));

        ActionForbiddenException exception = await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        Assert.Equal("Only the reservation owner can perform this action.", exception.Message);
        _groupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_ReservationDoesNotExist_ThrowsNotFound()
    {
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync((Reservation?)null);

        ReservationNotFoundException exception = await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        Assert.Equal("Reservation not found.", exception.Message);
        _groupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_GroupReservationDoesNotExist_ThrowsNotFound()
    {
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _groupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync((GroupReservation?)null);

        GuestSessionNotFoundException exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        Assert.Equal("Owner session not found for this reservation", exception.Message);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_OwnerSessionDoesNotExist_ThrowsNotFound()
    {
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _groupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync(CreateGroupReservation());
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId))
            .ReturnsAsync([CreateSession("guest-session", isOwnerSession: false)]);

        GuestSessionNotFoundException exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        Assert.Equal("Owner session not found for this reservation", exception.Message);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(GroupReservationId),
            Times.Once);
    }

    private static Reservation CreateReservation(string customerId) => new()
    {
        Id = ReservationId,
        TableId = "table-1",
        WaiterId = "waiter-1",
        CustomerId = customerId
    };

    private static GroupReservation CreateGroupReservation() => new()
    {
        GroupReservationId = GroupReservationId,
        ReservationId = ReservationId,
        ReservationToken = "reservation-token"
    };

    private static GuestSession CreateSession(string id, bool isOwnerSession) => new()
    {
        Id = id,
        GroupReservationId = GroupReservationId,
        GuestName = isOwnerSession ? "Owner" : "Guest",
        Dishes = [],
        IsOwnerSession = isOwnerSession
    };
}
