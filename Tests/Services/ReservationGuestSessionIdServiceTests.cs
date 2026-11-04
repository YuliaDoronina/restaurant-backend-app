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
using APIHandler.Services.Notifications;
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
            Mock.Of<ITableRepository>(),
            Mock.Of<INotificationService>());
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_ReservationOwnerAndLegacyOwnerSessionExist_PreservesLegacySessionId()
    {
        // Arrange
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
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([ownerSession]);

        // Act
        ReservationGuestSessionIdResponse response =
            await _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId);

        // Assert
        Assert.Equal("owner-session", response.GuestSessionId);
        _reservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(ReservationId),
            Times.Once);
        _groupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(ReservationId),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionIdConsistentAsync(
                It.Is<string>(id => id.StartsWith("owner-", StringComparison.Ordinal))),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.GetOwnerSessionsByGroupReservationIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_WhenNoOwnerSessionExists_CreatesOwnerSessionForReservationCustomer()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);
        _guestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(It.IsAny<string>()))
            .ReturnsAsync((GuestSession?)null);
        _guestSessionRepository
            .Setup(repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()))
            .ReturnsAsync(true);

        // Act
        GuestSession result = await _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation);

        // Assert
        Assert.True(result.IsOwnerSession);
        Assert.Equal(OwnerUserId, result.OwnerUserId);
        Assert.Equal(GroupReservationId, result.GroupReservationId);
        Assert.Equal("Owner", result.GuestName);
        Assert.Empty(result.Dishes);
        Assert.StartsWith("owner-", result.Id);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.Is<GuestSession>(session =>
                session.IsOwnerSession &&
                session.OwnerUserId == OwnerUserId &&
                session.GroupReservationId == GroupReservationId)),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionIdConsistentAsync(result.Id),
            Times.Once);
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_RepeatedEnsureUsesTheSameDeterministicSessionKey()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        GuestSession? storedSession = null;
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);
        _guestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(It.IsAny<string>()))
            .Returns<string>(_ => Task.FromResult(storedSession));
        _guestSessionRepository
            .Setup(repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(session => storedSession = session)
            .ReturnsAsync(true);

        // Act
        GuestSession firstResult = await _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation);
        GuestSession secondResult = await _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation);

        // Assert
        Assert.Same(storedSession, firstResult);
        Assert.Same(firstResult, secondResult);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Once);
        Assert.Equal(firstResult.Id, secondResult.Id);
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_WhenCalledConcurrently_CreatesOneStableSessionSharedByBothCalls()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        TaskCompletionSource<bool> bothWritesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GuestSession? storedSession = null;
        object storedSessionLock = new();
        int writeAttempts = 0;
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);
        _guestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(It.IsAny<string>()))
            .Returns<string>(_ => Task.FromResult(storedSession));
        _guestSessionRepository
            .Setup(repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()))
            .Returns<GuestSession>(async candidate =>
            {
                if (Interlocked.Increment(ref writeAttempts) == 2)
                    bothWritesStarted.TrySetResult(true);

                await bothWritesStarted.Task;
                lock (storedSessionLock)
                {
                    if (storedSession is not null)
                        return false;

                    storedSession = candidate;
                    return true;
                }
            });

        // Act
        GuestSession[] results = await Task.WhenAll(
            _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation),
            _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation));

        // Assert
        Assert.Same(results[0], results[1]);
        Assert.StartsWith("owner-", results[0].Id);
        Assert.Same(storedSession, results[0]);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.Is<GuestSession>(session =>
                session.Id == results[0].Id &&
                session.IsOwnerSession &&
                session.OwnerUserId == OwnerUserId)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_WhenGroupQueryDoesNotReturnStableSession_ReusesStronglyReadSessionWithoutInsert()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        GuestSession existingStableSession = CreateSession("owner-stable-session", isOwnerSession: true);
        existingStableSession.OwnerUserId = OwnerUserId;
        string? requestedStableSessionId = null;
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);
        _guestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(It.IsAny<string>()))
            .Callback<string>(id =>
            {
                requestedStableSessionId = id;
                existingStableSession.Id = id;
            })
            .ReturnsAsync(existingStableSession);

        // Act
        GuestSession result = await _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation);

        // Assert
        Assert.Same(existingStableSession, result);
        Assert.Equal(requestedStableSessionId, existingStableSession.Id);
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionIdConsistentAsync(
                It.Is<string>(id => id.StartsWith("owner-", StringComparison.Ordinal))),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_WhenLegacyOwnerSessionExists_PreservesItsRandomIdWithoutCreatingStableSession()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        const string legacySessionId = "legacy-owner-5f17e8c2";
        GuestSession existingSession = CreateSession(legacySessionId, isOwnerSession: true);
        existingSession.OwnerUserId = OwnerUserId;
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([existingSession]);

        // Act
        GuestSession result = await _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation);

        // Assert
        Assert.Same(existingSession, result);
        Assert.Equal(legacySessionId, result.Id);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_WhenConditionalInsertLosesRace_ReturnsSessionWrittenByOtherCall()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        GuestSession? competingSession = null;
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);
        _guestSessionRepository
            .Setup(repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(candidate =>
            {
                competingSession = CreateSession(candidate.Id, isOwnerSession: true);
                competingSession.OwnerUserId = OwnerUserId;
            })
            .ReturnsAsync(false);
        _guestSessionRepository
            .Setup(repository => repository.GetByGuestSessionIdConsistentAsync(It.IsAny<string>()))
            .Returns<string>(_ => Task.FromResult(competingSession));

        // Act
        GuestSession result = await _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation);

        // Assert
        Assert.NotNull(competingSession);
        Assert.Same(competingSession, result);
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionIdConsistentAsync(competingSession.Id),
            Times.Exactly(2));
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionId(It.IsAny<string>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Once);
    }

    [Fact]
    public async Task EnsureOwnerSessionAsync_WhenConditionalInsertFailsForAnotherReason_PropagatesFailure()
    {
        // Arrange
        GroupReservation groupReservation = CreateGroupReservation();
        InvalidOperationException expectedException = new("write failed");
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);
        _guestSessionRepository
            .Setup(repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()))
            .ThrowsAsync(expectedException);

        // Act
        InvalidOperationException actualException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.EnsureOwnerSessionAsync(OwnerUserId, groupReservation));

        // Assert
        Assert.Same(expectedException, actualException);
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionId(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_ReservationHasNoCustomerId_ThrowsForbiddenWithoutReadingGroupOrSession()
    {
        // Arrange
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: null));

        // Act
        ActionForbiddenException exception = await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        // Assert
        Assert.Equal("Only the reservation owner can perform this action.", exception.Message);
        _groupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(It.IsAny<string>()),
            Times.Never);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Never);
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
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([]);

        GuestSessionNotFoundException exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        Assert.Equal("Owner session not found for this reservation", exception.Message);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId),
            Times.Once);
    }

    [Fact]
    public async Task GetReservationGuestSessionIdAsync_GroupQueryReturnsMultipleOwnerSessions_ThrowsConflict()
    {
        // Arrange
        _reservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(ReservationId))
            .ReturnsAsync(CreateReservation(customerId: OwnerUserId));
        _groupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(ReservationId))
            .ReturnsAsync(CreateGroupReservation());
        _guestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId))
            .ReturnsAsync([
                CreateSession("owner-session-1", isOwnerSession: true),
                CreateSession("owner-session-2", isOwnerSession: true)
            ]);

        // Act
        DuplicateOwnerSessionException exception = await Assert.ThrowsAsync<DuplicateOwnerSessionException>(
            () => _service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId));

        // Assert
        Assert.Contains(GroupReservationId, exception.Message);
        _guestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdConsistentAsync(GroupReservationId),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.GetByGuestSessionIdConsistentAsync(
                It.Is<string>(id => id.StartsWith("owner-", StringComparison.Ordinal))),
            Times.Once);
        _guestSessionRepository.Verify(
            repository => repository.TrySaveOwnerSessionAsync(It.IsAny<GuestSession>()),
            Times.Never);
    }

    private static Reservation CreateReservation(string? customerId) => new()
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
        IsOwnerSession = isOwnerSession,
        OwnerUserId = isOwnerSession ? OwnerUserId : null
    };
}
