using Amazon.DynamoDBv2.DataModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Helpers;
using APIHandler.Mappers;
using APIHandler.Models.Reservations;
using APIHandler.Models.Users;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using APIHandler.Services.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APIHandler.Handlers.Reservations;

public class GetReservationsForUserHandler : IApiRequestHandler
{
    private const int MaxConcurrentReservationConversions = 5;

    private static readonly string ReservationsTableName = ResourceNames.ReservationsTableName;
    private static readonly string ReservationsCustomerIdGlobalIndex = ResourceNames.ReservationsCustomerIdGlobalIndex;

    private readonly IReservationRepository _reservationRepository;
    private readonly ILocationService _locationService;
    private readonly IOrderService _orderService;
    private readonly IGroupReservationRepository _groupReservationRepository;
    private readonly IGuestSessionRepository? _guestSessionRepository;

    public GetReservationsForUserHandler(
        IReservationRepository reservationRepository,
        ILocationService locationService,
        IOrderService orderService,
        IGroupReservationRepository groupReservationRepository,
        IGuestSessionRepository? guestSessionRepository = null)
    {
        _reservationRepository = reservationRepository ?? throw new ArgumentNullException(nameof(reservationRepository));
        _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
        _orderService = orderService ?? throw new ArgumentNullException(nameof(orderService));
        _groupReservationRepository = groupReservationRepository ?? throw new ArgumentNullException(nameof(groupReservationRepository));
        _guestSessionRepository = guestSessionRepository;
    }

    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (request.RequestContext?.Authorizer is null ||
            !request.RequestContext.Authorizer.Claims.TryGetValue("sub", out var userId) ||
            string.IsNullOrEmpty(userId))
        {
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized: User ID not found in request context.", context.Logger);
        }

        if (!request.RequestContext.Authorizer.Claims.TryGetValue("cognito:groups", out var groups) ||
            string.IsNullOrEmpty(groups))
        {
            return ResponseBuilder.CreateErrorResponse(403, "Forbidden: User does not have the required permissions.", context.Logger);
        }

        if (groups.Contains(Roles.Waiter.ToString()))
        {
            return await GetReservationsForWaiterAsync(userId, context);
        }
        else if (groups.Contains(Roles.Customer.ToString()))
        {
            return await GetReservationsForCustomerAsync(userId, context);
        }
        else
        {
            return ResponseBuilder.CreateErrorResponse(403, "Forbidden: User does not have the required permissions.", context.Logger);
        }
    }

    private async Task<APIGatewayProxyResponse> GetReservationsForWaiterAsync(string userId, ILambdaContext context)
    {
        context.Logger.LogLine($"Querying DynamoDB with index: '{ResourceNames.ReservationsWaiterIdGlobalIndex}' on table: '{ResourceNames.ReservationsTableName}'");

        IEnumerable<Reservation> reservations = await _reservationRepository.GetReservationsByWaiterIdAsync(userId);

        var convertedReservations = await ConvertReservationsAsync(reservations, context, skipCorruptedReservations: false);
        if (convertedReservations.Any(response => response is null))
        {
            return ResponseBuilder.CreateErrorResponse(500, "Internal Server Error: Error while fetching reservation details.", context.Logger);
        }

        return ResponseBuilder.CreateSuccessResponse(convertedReservations.Select(response => response!));
    }

    private async Task<APIGatewayProxyResponse> GetReservationsForCustomerAsync(string userId, ILambdaContext context)
    {
        try
        {
            var reservations = await _reservationRepository.GetReservationsByCustomerIdAsync(userId);
            var convertedReservations = await ConvertReservationsAsync(reservations, context, skipCorruptedReservations: true);

            return ResponseBuilder.CreateSuccessResponse(convertedReservations.Where(response => response is not null).Select(response => response!));
        }
        catch (Exception ex)
        {
            context.Logger.LogLine($"Error retrieving reservations for user {userId}: {ex.Message}");
            return ResponseBuilder.CreateErrorResponse(500, "Internal Server Error: Unable to retrieve reservations.", context.Logger);
        }
    }

    private async Task<ReservationResponse?[]> ConvertReservationsAsync(
        IEnumerable<Reservation> reservations,
        ILambdaContext context,
        bool skipCorruptedReservations)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentReservationConversions);

        return await Task.WhenAll(reservations.Select(async reservation =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await ConvertToResponse(reservation);
            }
            catch (Exception ex)
            {
                var action = skipCorruptedReservations ? "Skipping corrupted" : "Error converting";
                context.Logger.LogLine($"{action} reservation {reservation.Id} to response: {ex.Message}");
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        }));
    }

    private async Task<ReservationResponse> ConvertToResponse(Reservation reservation)
    {
        try
        {
            var locationTask = _locationService.GetLocationByTableIdAsync(reservation.TableId);
            var orderTask = _orderService.GetOrderByReservationIdAsync(reservation.Id);
            var shouldLoadGroupReservation =
                reservation.GuestCount > 1 && reservation.Status != ReservationStatus.Completed;
            Task<GroupReservation?> groupReservationTask = shouldLoadGroupReservation
                ? _groupReservationRepository.GetByReservationIdAsync(reservation.Id)
                : Task.FromResult<GroupReservation?>(null);

            await Task.WhenAll(locationTask, orderTask, groupReservationTask);

            var location = await locationTask;
            var reservationOrder = await orderTask;
            var groupReservation = await groupReservationTask;
            var currentParticipants = groupReservation is not null && _guestSessionRepository is not null
                ? 1 + await _guestSessionRepository.CountByGroupReservationId(groupReservation.GroupReservationId)
                : 0;
            var guestInviteLinkAvailable =
                groupReservation?.Status == GroupReservationStatus.Open;

            return ReservationMapper.ToReservationResponse(
                reservation,
                location.Address,
                reservationOrder,
                guestInviteLinkAvailable: guestInviteLinkAvailable,
                currentParticipants: currentParticipants);
        }
        catch (Exception ex)
        {
            throw new Exception($"Error converting reservation with Id {reservation.Id}, TableId {reservation.TableId}: {ex.Message}", ex);
        }
    }
}