using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Models.GuestReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.Locations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Services.GroupReservations;

public class GroupReservationService(
    IGroupReservationRepository groupReservationRepository,
    ITableRepository tableRepository,
    IReservationRepository reservationRepository,
    IGuestSessionRepository guestSessionRepository,
    IUserService userService,
    ILocationService locationService,
    IOrderRepository? orderRepository = null,
    IGroupReservationSubmitRepository? groupReservationSubmitRepository = null) : IGroupReservationService
{
    private const string DefaultRestaurantName = "Green & Tasty";

    public async Task<GetGroupReservationResponse> GetGroupReservationByTokenAsync(string token)
    {
        GroupReservation groupReservation = await groupReservationRepository.GetGroupReservationByTokenAsync(token)
            ?? throw new GroupReservationNotFoundException();

        if (groupReservation.Status != GroupReservationStatus.Open)
            throw new GroupReservationExpiredException();

        Task<Reservation?> reservationTask = reservationRepository.GetReservationByIdAsync(groupReservation.ReservationId);
        Task<int> countTask = guestSessionRepository.CountByGroupReservationId(groupReservation.GroupReservationId);

        Reservation reservation = await reservationTask ?? throw new ValidationException("Associated reservation not found.");
        int joinedGuestsCount = await countTask;

        if (joinedGuestsCount + 1 >= reservation.GuestCount)
            throw new GuestLimitExceededException();

        Table table = await tableRepository.GetTableAsync(reservation.TableId)
            ?? throw new InvalidOperationException("Associated table not found.");

        Location? location = await locationService.GetLocationByIdAsync(table.LocationId);
        string restaurantName = DefaultRestaurantName;
        string address = location?.Address ?? "Unknown Address";

        string inviterName = "Unknown";
        if (!string.IsNullOrWhiteSpace(reservation.CustomerId))
        {
            inviterName = await userService.GetUserName(reservation.CustomerId) ?? "Unknown";
        }
        return new GetGroupReservationResponse(
            groupReservation.GroupReservationId,
            table.LocationId,
            restaurantName,
            address,
            reservation.StartTime,
            reservation.EndTime,
            reservation.GuestCount,
            table.TableNumber,
            inviterName);
    }

    public async Task<GroupReservation> GetGroupReservationAsync(string groupReservationId)
    {
        GroupReservation? groupReservation = await groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId);
        return groupReservation ?? throw new InvalidOperationException($"Group reservation with ID {groupReservationId} not found.");
    }

    public async Task<GroupReservation> CreateGroupReservationAsync(string reservationId)
    {
        var groupReservation = new GroupReservation
        {
            GroupReservationId = Guid.NewGuid().ToString(),
            ReservationId = reservationId,
            ReservationToken = Guid.NewGuid().ToString("N"),
            Status = GroupReservationStatus.Open,
        };

        await groupReservationRepository.SaveGroupReservationAsync(groupReservation);
        return groupReservation;
    }

    public async Task CreateGroupReservationIfDoesNotExistAsync(string userId, string reservationId)
    {
        GroupReservation? group = await groupReservationRepository.GetByReservationIdAsync(reservationId);
        if (group is not null)
            return;

        await CreateGroupReservationAsync(reservationId);
    }

    public async Task UpdateGroupReservationStatusAsync(string groupReservationId, GroupReservationStatus status)
    {
        var groupReservation = await GetGroupReservationAsync(groupReservationId);

        groupReservation.Status = status;

        await groupReservationRepository.SaveGroupReservationAsync(groupReservation);
    }

    public async Task DeleteGroupReservationAsync(string groupReservationId)
    {
        var groupReservation = await GetGroupReservationAsync(groupReservationId);

        await groupReservationRepository.DeleteGroupReservationAsync(groupReservation);
    }

    public async Task CloseGroupReservationAsync(string reservationId)
    {
        GroupReservation? groupReservation = await groupReservationRepository.GetByReservationIdAsync(reservationId);
        if (groupReservation is null)
            return;

        groupReservation.Status = GroupReservationStatus.Closed;

        await groupReservationRepository.SaveGroupReservationAsync(groupReservation);
    }

    public async Task<GetGroupInviteTokenResponse> GetGroupReservationInviteTokenAsync(string reservationId)
    {
        var reservation = await reservationRepository.GetReservationByIdAsync(reservationId)
            ?? throw new ReservationNotFoundException($"Reservation {reservationId} was not found.");

        if (reservation.GuestCount < 2)
            throw new ValidationException("Retrieving invitation token for the reservation with only one guest is not permitted.");

        if (reservation.Status != ReservationStatus.Reserved)
            throw new GroupReservationExpiredException("The group reservation is expired.");

        GroupReservation groupReservation = (await groupReservationRepository.GetByReservationIdAsync(reservationId))!;

        if (groupReservation.Status == GroupReservationStatus.Closed)
            throw new GroupReservationExpiredException();

        if (groupReservation.Status != GroupReservationStatus.Open)
            throw new GroupReservationSubmittedException();

        return new GetGroupInviteTokenResponse(groupReservation.ReservationToken);
    }

    public async Task<IReadOnlyList<GroupReservationOrderItemResponse>> SubmitGroupReservationAsync(
        string groupReservationId,
        string reservationId,
        string userId)
    {
        GroupReservation groupReservation = await groupReservationRepository.GetGroupReservationByIdAsync(groupReservationId)
            ?? throw new GroupReservationNotFoundException();
        Reservation reservation = await reservationRepository.GetReservationByIdAsync(groupReservation.ReservationId)
            ?? throw new ReservationNotFoundException($"Reservation {groupReservation.ReservationId} was not found.");

        ValidateSubmitRequest(groupReservation, reservationId, userId, reservation);

        if (groupReservation.Status is not (GroupReservationStatus.Open or GroupReservationStatus.Submitting))
            throw new GroupReservationSubmitConflictException();

        IGroupReservationSubmitRepository submitRepository = groupReservationSubmitRepository
            ?? throw new InvalidOperationException("Group reservation submit repository is not configured.");
        string attemptId = Guid.NewGuid().ToString("N");
        bool acquired = false;

        try
        {
            await submitRepository.BeginSubmitAsync(groupReservation, attemptId);
            acquired = true;
            return await SubmitGroupOrderAsync(groupReservation, submitRepository, attemptId);
        }
        catch (Exception submissionException)
        {
            if (submissionException is GroupReservationSubmitConflictException && !acquired)
                throw;

            if (acquired)
                groupReservation.Status = GroupReservationStatus.Submitting;

            try
            {
                await submitRepository.ReleaseSubmitAsync(groupReservation.GroupReservationId, attemptId);
                groupReservation.Status = GroupReservationStatus.Open;
                groupReservation.SubmitAttemptId = null;
                groupReservation.SubmitLeaseExpiresAt = null;
            }
            catch (ConditionalCheckFailedException) when (submissionException is GroupReservationSubmitConflictException)
            {
                // A later attempt owns the lease, so cleanup must not replace the submission conflict.
            }
            catch (Exception releaseException)
            {
                throw new AggregateException(
                    "Group reservation submission failed and its attempt could not be released.",
                    submissionException,
                    releaseException);
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<GroupReservationOrderItemResponse>> SubmitGroupOrderAsync(
        GroupReservation groupReservation,
        IGroupReservationSubmitRepository submitRepository,
        string attemptId)
    {
        IOrderRepository orders = orderRepository
            ?? throw new InvalidOperationException("Order repository is not configured.");

        List<GuestSession> submittedSessions = (await guestSessionRepository.GetAllByGroupReservationIdConsistentAsync(groupReservation.GroupReservationId))
            .Where(session => session.IsSubmitted)
            .ToList();
        if (submittedSessions.Count == 0)
            throw new ValidationException("No submitted guest sessions were found.");
        if (!submittedSessions.Any(session => session.Dishes?.Count > 0))
            throw new ValidationException("Submitted guest sessions contain no dishes.");

        Order order = await orders.GetOrderByReservationIdAsync(groupReservation.ReservationId)
            ?? throw new ValidationException("Order was not found for the reservation.");

        order.Dishes = MergeSubmittedDishes(order.Dishes, submittedSessions);

        order.Status = OrderStatus.SubmittedByCustomer;
        groupReservation.Status = GroupReservationStatus.Submitted;
        await submitRepository.SubmitAsync(order, groupReservation, attemptId);

        return MapOrderItems(order.Dishes);
    }

    private static void ValidateSubmitRequest(
        GroupReservation groupReservation,
        string reservationId,
        string userId,
        Reservation reservation)
    {
        if (!string.Equals(groupReservation.ReservationId, reservationId, StringComparison.Ordinal))
            throw new ValidationException("The reservationId does not match the group reservation.");

        if (!string.Equals(reservation.CustomerId, userId, StringComparison.Ordinal))
            throw new ActionForbiddenException("Only the reservation owner can submit the group pre-order.");
    }

    private static List<OrderItem> MergeSubmittedDishes(
        List<OrderItem> existingDishes,
        IEnumerable<GuestSession> submittedSessions)
    {
        var mergedDishes = new Dictionary<string, OrderItem>(StringComparer.Ordinal);
        foreach (OrderItem existingDish in existingDishes ?? [])
            AddOrIncreaseDish(mergedDishes, existingDish, preserveState: true);

        foreach (GuestSession session in submittedSessions)
        {
            foreach (OrderItem sessionDish in session.Dishes ?? [])
                AddOrIncreaseDish(mergedDishes, sessionDish, preserveState: false);
        }

        return mergedDishes.Values.ToList();
    }

    private static void AddOrIncreaseDish(
        IDictionary<string, OrderItem> dishesById,
        OrderItem dish,
        bool preserveState)
    {
        if (dishesById.TryGetValue(dish.DishId, out OrderItem? existingDish))
        {
            existingDish.Quantity += dish.Quantity;
            return;
        }

        dishesById.Add(dish.DishId, new OrderItem
        {
            DishId = dish.DishId,
            Quantity = dish.Quantity,
            State = preserveState ? dish.State : OrderItemState.Pending
        });
    }

    private static IReadOnlyList<GroupReservationOrderItemResponse> MapOrderItems(IEnumerable<OrderItem> orderItems)
    {
        return orderItems
            .Select(dish => new GroupReservationOrderItemResponse
            {
                DishId = dish.DishId,
                Quantity = dish.Quantity
            })
            .ToList();
    }
}
