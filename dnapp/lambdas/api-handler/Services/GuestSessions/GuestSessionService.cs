using Amazon.CognitoIdentityProvider.Model;
using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Mappers;
using APIHandler.Models.GuestSessions;
using APIHandler.Models.Orders;
using APIHandler.Models.Reservations;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using APIHandler.Services.Notifications;

namespace APIHandler.Services.GuestSessions;

public class GuestSessionService(
    IGroupReservationRepository groupReservationRepository,
    IGuestSessionRepository guestSessionRepository,
    IDishRepository dishRepository,
    IReservationRepository reservationRepository,
    IUserRepository userRepository,
    ITableRepository tableRepository) : IGuestSessionService
{
    private const string DefaultOwnerName = "Owner";
    private const int OwnerParticipantCount = 1;
    private const string GivenNameAttribute = "given_name";
    private const string OwnerSessionNotFoundMessage = "Owner session not found for this reservation";

    public async Task<bool> IsAuthorizedAsync(string guestSessionToken, string guestSessionId)
    {
        string hash = Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(guestSessionToken)));
        GuestSession? guestSession = await guestSessionRepository.GetByTokenHash(hash);
        if (guestSession is null)
            return false;

        return guestSession.Id == guestSessionId;
    }

    public async Task<CreatGuestSessionResponse> CreateGuestSessionAsync(CreateGuestSessionRequest request)
    {
        GroupReservation group = await GetGroupReservationAndValidateAsync(request.GroupReservationId);

        Task<Reservation?> reservationTask = reservationRepository.GetReservationByIdAsync(group.ReservationId);
        Task<int> countTask = guestSessionRepository.CountByGroupReservationId(group.GroupReservationId);

        Reservation reservation = await reservationTask ??
                                  throw new GroupReservationExpiredException(
                                      $"Associated reservation for group '{request.GroupReservationId}' not found.");

        if (reservation.Status != ReservationStatus.Reserved)
        {
            throw new GuestSessionExpiredException(
                $"This pre-order session has expired or is no longer active. Current reservation status: '{reservation.Status}'.");
        }

        int joinedGuestsCount = await countTask;

        if (joinedGuestsCount + 1 >= reservation.GuestCount)
            throw new GuestLimitExceededException(
                $"Guest limit exceeded. Maximum capacity is {reservation.GuestCount}, but {joinedGuestsCount} guests have already joined.");

        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);

        var session = new GuestSession()
        {
            Id = Guid.NewGuid().ToString(),
            GuestName = request.GuestName,
            GroupReservationId = group.GroupReservationId,
            SessionTokenHash = Convert.ToBase64String(SHA256.HashData(tokenBytes)),
            Dishes = [],
        };

        await guestSessionRepository.SaveCartAsync(session);

        return new CreatGuestSessionResponse(session.Id, Convert.ToBase64String(tokenBytes));
    }

    public async Task<GuestMenuResponse> GetGuestMenuAsync(string guestSessionId)
    {
        var session = await guestSessionRepository.GetByGuestSessionId(guestSessionId)
                      ?? throw new ValidationException($"Guest session '{guestSessionId}' not found.");

        await ValidateGroupReservationId(session.GroupReservationId);

        string locationId = await GetLocationIdForGroupAsync(session.GroupReservationId);
        var dishes = await dishRepository.GetDishesByLocationIdAsync(locationId);

        var dishesMapped = dishes
            .GroupBy(d => d.DishType)
            .ToDictionary(k => k.Key, GuestMenuDishMapper.Map);

        return new GuestMenuResponse(dishesMapped);
    }

    public async Task AddDishAsync(string guestSessionId, string dishId, int quantity)
    {
        if (quantity <= 0)
            throw new ValidationException($"Quantity ({quantity}) must be greater than zero.");

        GuestSession session = await GetGuestSessionAsync(guestSessionId);

        string locationId = await GetLocationIdForGroupAsync(session.GroupReservationId);
        var locationDishes = await dishRepository.GetDishesByLocationIdAsync(locationId);

        var dish = locationDishes.FirstOrDefault(d => d.Id == dishId)
                   ?? throw new ValidationException(
                       $"Dish '{dishId}' does not exist or is not available at this location.");

        if (dish.State != DishState.Available)
            throw new DishIsNotAvailableException($"Dish '{dishId}' is currently unavailable. State: '{dish.State}'.");

        session.Dishes ??= [];

        var existingDish = session.Dishes.FirstOrDefault(d => d.DishId == dishId);

        if (existingDish != null)
        {
            existingDish.Quantity += quantity;
        }
        else
        {
            session.Dishes.Add(new OrderItem
            {
                DishId = dishId,
                Quantity = quantity,
                State = OrderItemState.Pending
            });
        }

        await guestSessionRepository.SaveCartAsync(session);
    }

    public async Task RemoveDishAsync(string guestSessionId, string dishId, int quantity)
    {
        if (quantity <= 0)
            throw new ValidationException("Quantity must be greater than zero.");

        GuestSession session = await GetGuestSessionAsync(guestSessionId);

        if (session.Dishes == null || session.Dishes.Count == 0)
            throw new DishNotFoundException($"Dish {dishId} not found in guest session.");

        var existingDish = session.Dishes.FirstOrDefault(d => d.DishId == dishId)
                           ?? throw new DishNotFoundException($"Dish {dishId} not found in guest session.");

        existingDish.Quantity -= quantity;

        if (existingDish.Quantity <= 0)
        {
            session.Dishes.Remove(existingDish);
        }

        await guestSessionRepository.SaveCartAsync(session);
    }

    public async Task<GroupReservationStatusResponse> GetGroupReservationStatusAsync(string groupReservationId)
    {
        var group = await groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId)
                    ?? throw new GroupReservationNotFoundException();

        var reservation = await reservationRepository.GetReservationByIdAsync(group.ReservationId)
                          ?? throw new InvalidOperationException("Associated reservation not found.");

        int currentParticipants = await GetCurrentParticipantCountAsync(groupReservationId);

        return new GroupReservationStatusResponse
        {
            CurrentParticipants = currentParticipants,
            MaxCapacity = reservation.GuestCount,
            Status = group.Status
        };
    }


    public async Task<GroupSessionsResponse> DeleteGuestSessionAsync(string userId, string groupReservationId,
        string guestSessionId)
    {
        Reservation reservation = await ValidateReservationOwnerAsync(groupReservationId, userId);

        GuestSession session = await guestSessionRepository.GetByGuestSessionIdConsistentAsync(guestSessionId)
                      ?? throw new GuestSessionNotFoundException($"Guest session with id {guestSessionId} not found.");

        if (session.GroupReservationId != groupReservationId)
        {
            throw new ValidationException("Guest session does not belong to this group reservation.");
        }

        if (session.IsOwnerSession)
        {
            throw new ValidationException("Owner sessions cannot be deleted.");
        }

        await guestSessionRepository.DeleteAsync(session);

        List<GuestSession> sessions = await GetConfirmedPostDeleteSessionsAsync(groupReservationId, guestSessionId);
        GuestSession? ownerSession = await FindOwnerSessionAsync(groupReservationId);
        return await BuildGroupSessionsResponseAsync(reservation, groupReservationId, ownerSession, sessions);
    }

    private async Task<List<GuestSession>> GetConfirmedPostDeleteSessionsAsync(
        string groupReservationId,
        string guestSessionId)
    {
        GuestSession? deletedSession = await guestSessionRepository.GetByGuestSessionIdConsistentAsync(guestSessionId);
        if (deletedSession is not null)
        {
            throw new InvalidOperationException(
                $"Guest session '{guestSessionId}' still exists after the delete operation.");
        }

        return await guestSessionRepository.GetAllByGroupReservationIdConsistentAsync(groupReservationId);
    }

    public async Task<GroupSessionsResponse> GetGroupSessionsAsync(string userId, string groupReservationId)
    {
        Reservation reservation = await ValidateReservationOwnerAsync(groupReservationId, userId);
        GuestSession? ownerSession = await FindOwnerSessionAsync(groupReservationId);
        var sessions = await guestSessionRepository.GetAllByGroupReservationIdAsync(groupReservationId);
        return await BuildGroupSessionsResponseAsync(reservation, groupReservationId, ownerSession, sessions);
    }

    private async Task<GroupSessionsResponse> BuildGroupSessionsResponseAsync(
        Reservation reservation,
        string groupReservationId,
        GuestSession? ownerSession,
        List<GuestSession> sessions)
    {
        List<GuestSession> guestSessions = sessions.Where(session => !session.IsOwnerSession).ToList();
        IEnumerable<GuestSession> dishSessions = ownerSession is null ? guestSessions : [ownerSession, .. guestSessions];
        Dictionary<string, Dish> dishesById = await GetSessionDishesByIdAsync(dishSessions);
        GroupSessionParticipantResponse ownerParticipant = await MapOwnerParticipantAsync(
            ownerSession, reservation, groupReservationId, dishesById);
        IEnumerable<GroupSessionParticipantResponse> guestParticipants = MapGuestParticipants(guestSessions, dishesById);
        GroupSessionParticipantResponse[] participants = [ownerParticipant, .. guestParticipants];

        return new GroupSessionsResponse(participants, participants.Sum(participant => participant.Total));
    }

    private async Task<Dictionary<string, Dish>> GetSessionDishesByIdAsync(IEnumerable<GuestSession> sessions)
    {
        string[] dishIds = sessions
            .SelectMany(session => session.Dishes)
            .Select(item => item.DishId)
            .Distinct()
            .ToArray();
        IEnumerable<Dish> dishes = await dishRepository.GetDishesByIdsAsync(dishIds);
        return dishes.ToDictionary(dish => dish.Id);
    }

    private async Task<GroupSessionParticipantResponse> MapOwnerParticipantAsync(
        GuestSession? ownerSession,
        Reservation reservation,
        string groupReservationId,
        Dictionary<string, Dish> dishesById)
    {
        string ownerName = await GetOwnerNameAsync(ownerSession, reservation, groupReservationId);
        GroupSessionItemResponse[] items = MapSessionItems(ownerSession?.Dishes ?? [], dishesById);

        return new GroupSessionParticipantResponse(
            ownerSession?.Id,
            ParticipantType.Owner,
            ownerName,
            items,
            items.Sum(item => item.Quantity * item.Dish.Price),
            ownerSession?.IsSubmitted ?? false);
    }

    private async Task<string> GetOwnerNameAsync(
        GuestSession? ownerSession,
        Reservation reservation,
        string groupReservationId)
    {
        string ownerUserId = ownerSession?.OwnerUserId ?? reservation.CustomerId
            ?? throw new InvalidOperationException(
                $"Owner identity for group reservation '{groupReservationId}' was not found.");
        var owner = await userRepository.GetUserByIdAsync(ownerUserId);
        if (owner is null)
            return LogOwnerNameFallback(
                $"Owner user '{ownerUserId}' for group reservation '{groupReservationId}' was not found.");

        string? givenName = owner.Attributes.FirstOrDefault(attribute => attribute.Name == GivenNameAttribute)?.Value;
        return string.IsNullOrWhiteSpace(givenName)
            ? LogOwnerNameFallback($"Owner user '{ownerUserId}' has no '{GivenNameAttribute}' attribute.")
            : givenName;
    }

    private static string LogOwnerNameFallback(string reason)
    {
        LambdaLogger.Log($"[WARN] {reason} Falling back to '{DefaultOwnerName}'.");
        return DefaultOwnerName;
    }

    private static IEnumerable<GroupSessionParticipantResponse> MapGuestParticipants(
        IEnumerable<GuestSession> sessions,
        Dictionary<string, Dish> dishesById)
        => sessions
            .Where(session => !session.IsOwnerSession)
            .Select(session => MapGuestParticipant(session, dishesById));

    private static GroupSessionParticipantResponse MapGuestParticipant(
        GuestSession session,
        Dictionary<string, Dish> dishesById)
    {
        GroupSessionItemResponse[] items = MapSessionItems(session.Dishes, dishesById);
        return new GroupSessionParticipantResponse(
            session.Id,
            ParticipantType.Guest,
            session.GuestName,
            items,
            items.Sum(item => item.Quantity * item.Dish.Price),
            session.IsSubmitted);
    }

    private static GroupSessionItemResponse[] MapSessionItems(
        IEnumerable<OrderItem> orderItems,
        Dictionary<string, Dish> dishesById)
        => orderItems.Select(item =>
        {
            if (!dishesById.TryGetValue(item.DishId, out Dish? dish))
            {
                throw new InvalidOperationException(
                    $"Dish '{item.DishId}' referenced by a guest session was not found.");
            }

            return new GroupSessionItemResponse(
                new GroupSessionDishResponse(dish.Id, dish.Name, dish.Price),
                item.Quantity);
        }).ToArray();

    public async Task SubmitCartAsync(string guestSessionId)
    {
        GuestSession guestSession = await GetGuestSessionAsync(guestSessionId);

        if (!guestSession.Dishes.Any())
            throw new CartIsEmptyException();

        await ValidateDishesAsync(guestSession.Dishes);

        guestSession.IsSubmitted = true;
        await guestSessionRepository.SaveCartAsync(guestSession);
    }

    private async Task<GroupReservation> GetGroupReservationAndValidateAsync(string groupReservationId)
    {
        GroupReservation? group = await groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId);
        ValidateGroupReservation(group);
        return group;
    }

    private async Task<int> GetCurrentParticipantCountAsync(string groupReservationId)
    {
        var sessions = await guestSessionRepository.GetAllByGroupReservationIdAsync(groupReservationId);

        // Guests are counted separately; exactly one logical Owner is added,
        // independent of how many Owner session rows are stored.
        int guestCount = sessions?.Count(session => !session.IsOwnerSession) ?? 0;
        return guestCount + OwnerParticipantCount;
    }

    private async Task ValidateGroupReservationId(string groupReservationId) =>
        await GetGroupReservationAndValidateAsync(groupReservationId);

    private static void ValidateGroupReservation([NotNull] GroupReservation? group)
    {
        if (group is null)
            throw new GroupReservationNotFoundException();

        if (group.Status == GroupReservationStatus.Closed)
            throw new GroupReservationExpiredException();

        if (group.Status is GroupReservationStatus.Submitted or GroupReservationStatus.Submitting)
            throw new GroupReservationSubmittedException();
    }

    private async Task<GuestSession> GetGuestSessionAsync(string sessionId)
    {
        var session = await guestSessionRepository.GetByGuestSessionId(sessionId)
                      ?? throw new GuestSessionNotFoundException($"Guest session {sessionId} not found.");

        if (session.IsSubmitted)
            throw new GuestSessionSubmittedException();

        await ValidateGroupReservationId(session.GroupReservationId);

        return session;
    }

    private async Task ValidateDishesAsync(IEnumerable<OrderItem> orders)
    {
        IEnumerable<string> dishIds = orders.Select(o => o.DishId).ToArray();
        IEnumerable<Dish> dishes = await dishRepository.GetDishesByIdsAsync(dishIds);

        if (dishes.FirstOrDefault(d => d.State != DishState.Available) is Dish dish)
            throw new DishIsNotAvailableException($"The dish '{dish.Name}' ({dish.Id}) is not available.");
    }

    private async Task<string> GetLocationIdForGroupAsync(string groupReservationId)
    {
        var group = await groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId)
                    ?? throw new GroupReservationNotFoundException();

        var reservation = await reservationRepository.GetReservationByIdAsync(group.ReservationId)
                          ?? throw new ReservationNotFoundException("Parent reservation not found.");

        var table = await tableRepository.GetTableAsync(reservation.TableId)
                    ?? throw new ValidationException("Associated table not found.");

        return table.LocationId;
    }


    private async Task<Reservation> ValidateReservationOwnerAsync(string groupReservationId, string userId)
    {
        var group = await groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId)
                    ?? throw new GroupReservationNotFoundException();

        var reservation = await reservationRepository.GetReservationByIdAsync(group.ReservationId)
                          ?? throw new ReservationNotFoundException("Parent reservation not found.");

        if (reservation.CustomerId != userId)
        {
            throw new ActionForbiddenException("Only the reservation owner can perform this action.");
        }

        return reservation;
    }

    public async Task<GuestSession> EnsureOwnerSessionAsync(string ownerUserId, GroupReservation groupReservation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerUserId);
        ArgumentNullException.ThrowIfNull(groupReservation);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupReservation.GroupReservationId);

        Reservation reservation = await reservationRepository.GetReservationByIdAsync(groupReservation.ReservationId)
                                  ?? throw new ReservationNotFoundException("Reservation not found.");
        if (string.IsNullOrWhiteSpace(reservation.CustomerId) || reservation.CustomerId != ownerUserId)
            throw new ActionForbiddenException("Only the reservation owner can have an Owner session.");

        GuestSession? existingSession = await FindOwnerSessionAsync(groupReservation.GroupReservationId);
        if (existingSession is not null)
        {
            return ValidateOwnerSession(existingSession, ownerUserId, groupReservation.GroupReservationId);
        }

        string ownerSessionId = GetStableOwnerSessionId(groupReservation.GroupReservationId);

        GuestSession session = new()
        {
            Id = ownerSessionId,
            GroupReservationId = groupReservation.GroupReservationId,
            GuestName = DefaultOwnerName,
            Dishes = [],
            IsOwnerSession = true,
            OwnerUserId = ownerUserId,
        };

        bool created = await guestSessionRepository.TrySaveOwnerSessionAsync(session);
        if (created)
            return session;

        GuestSession? concurrentSession = await guestSessionRepository.GetByGuestSessionIdConsistentAsync(session.Id);
        if (concurrentSession is null)
            throw new InvalidOperationException("Owner session key was occupied, but the existing session could not be loaded.");

        return ValidateOwnerSession(concurrentSession, ownerUserId, groupReservation.GroupReservationId);
    }

    public async Task<GetGuestSessionResponse> GetGuestSessionInfoAsync(string guestSessionId)
    {
        var session = await guestSessionRepository.GetByGuestSessionId(guestSessionId)
                      ?? throw new GuestSessionNotFoundException("Guest session was not found.");

        var dishIds = session.Dishes
            .Select(s => s.DishId)
            .Distinct()
            .ToArray();

        var allDishes = await dishRepository.GetDishesByIdsAsync(dishIds);
        var dishesById = allDishes.ToDictionary(d => d.Id);

        var items = session.Dishes.Select(d => new OrderItemResponse
        {
            Dish = DishMapper.ToDishResponse(dishesById[d.DishId]),
            Quantity = d.Quantity
        }).ToArray();

        decimal total = items.Sum(i => i.Quantity * i.Dish.Price);

        string name = session.GuestName;
        if (session.IsOwnerSession)
        {
            name = await GetStoredOwnerNameAsync(session);
        }

        return new GetGuestSessionResponse(name, items, total, session.IsSubmitted);
    }

    private async Task<string> GetStoredOwnerNameAsync(GuestSession ownerSession)
    {
        GroupReservation groupReservation =
            await groupReservationRepository.GetGroupReservationByIdAsync(ownerSession.GroupReservationId)
            ?? throw new GroupReservationNotFoundException();
        Reservation reservation = await reservationRepository.GetReservationByIdAsync(groupReservation.ReservationId)
                                  ?? throw new ReservationNotFoundException("Reservation not found.");

        return await GetOwnerNameAsync(ownerSession, reservation, ownerSession.GroupReservationId);
    }

    public async Task<ReservationGuestSessionIdResponse> GetReservationGuestSessionIdAsync(
        string userId,
        string reservationId)
    {
        Reservation reservation = await reservationRepository.GetReservationByIdAsync(reservationId)
                                  ?? throw new ReservationNotFoundException("Reservation not found.");

        if (reservation.CustomerId != userId)
            throw new ActionForbiddenException("Only the reservation owner can perform this action.");

        GroupReservation? groupReservation = await groupReservationRepository.GetByReservationIdAsync(reservationId);
        if (groupReservation is null)
            throw new GuestSessionNotFoundException(OwnerSessionNotFoundMessage);

        GuestSession? ownerSession = await FindOwnerSessionAsync(groupReservation.GroupReservationId);
        if (ownerSession is null || ownerSession.OwnerUserId != reservation.CustomerId)
            throw new GuestSessionNotFoundException(OwnerSessionNotFoundMessage);

        return new ReservationGuestSessionIdResponse { GuestSessionId = ownerSession.Id };
    }

    private async Task<GuestSession?> FindOwnerSessionAsync(string groupReservationId)
    {
        // Stable-ID owner session: strongly consistent point read (O(1)); new Owner sessions are only
        // created under this key via a conditional write, so it cannot be missed after a successful create.
        GuestSession? stableSession = await guestSessionRepository.GetByGuestSessionIdConsistentAsync(
            GetStableOwnerSessionId(groupReservationId));

        // Legacy Owner sessions may have random IDs. The GSI is eventually consistent and could miss them,
        // so a strongly consistent lookup is used to keep EnsureOwnerSessionAsync idempotent.
        List<GuestSession> groupSessions =
            await guestSessionRepository.GetAllByGroupReservationIdConsistentAsync(groupReservationId);
        List<GuestSession> indexedOwnerSessions = groupSessions.Where(session => session.IsOwnerSession).ToList();

        List<GuestSession> ownerSessions = indexedOwnerSessions
            .Where(session => session.Id != stableSession?.Id)
            .ToList();
        if (stableSession is not null)
            ownerSessions.Add(stableSession);

        if (ownerSessions.Count > 1)
            throw new DuplicateOwnerSessionException(groupReservationId);

        return ownerSessions.FirstOrDefault();
    }

    private static GuestSession ValidateOwnerSession(
        GuestSession session,
        string ownerUserId,
        string groupReservationId)
    {
        if (!session.IsOwnerSession ||
            session.GroupReservationId != groupReservationId ||
            session.OwnerUserId != ownerUserId)
        {
            throw new InvalidOperationException(
                $"An incompatible Owner session already exists for group reservation '{groupReservationId}'.");
        }

        return session;
    }

    private static string GetStableOwnerSessionId(string groupReservationId)
    {
        byte[] groupIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(groupReservationId));
        return $"owner-{Convert.ToHexString(groupIdHash)}";
    }
}
