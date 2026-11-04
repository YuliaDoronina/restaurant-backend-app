using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GuestSessions;

public class UpdateGroupSessionDishHandler : IApiRequestHandler
{
    private readonly IGuestSessionService _guestSessionService;

    public UpdateGroupSessionDishHandler(IGuestSessionService guestSessionService)
    {
        _guestSessionService = guestSessionService ?? throw new ArgumentNullException(nameof(guestSessionService));
    }

    public async Task<APIGatewayProxyResponse> HandleRequest(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!request.TryGetPathValue("groupReservationId", out string? groupReservationId) ||
            string.IsNullOrWhiteSpace(groupReservationId))
        {
            return ResponseBuilder.CreateErrorResponse(
                400,
                "Missing 'groupReservationId' in path parameters.",
                context.Logger);
        }

        if (!request.TryGetPathValue("guestSessionId", out string? guestSessionId) ||
            string.IsNullOrWhiteSpace(guestSessionId))
        {
            return ResponseBuilder.CreateErrorResponse(
                400,
                "Missing 'guestSessionId' in path parameters.",
                context.Logger);
        }

        if (request.RequestContext?.Authorizer?.Claims is null ||
            !request.RequestContext.Authorizer.Claims.TryGetValue("sub", out string? userId) ||
            string.IsNullOrWhiteSpace(userId))
        {
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized.", context.Logger);
        }

        if (!TryDeserializeRequest(request, out UpdateGroupSessionDishRequest? updateRequest))
        {
            return ResponseBuilder.CreateErrorResponse(400, "Invalid request body.", context.Logger);
        }

        try
        {
            GroupSessionsResponse response = await _guestSessionService.UpdateGroupSessionDishAsync(
                userId,
                groupReservationId,
                guestSessionId,
                updateRequest!.DishId,
                updateRequest.Quantity);
            return ResponseBuilder.CreateSuccessResponse(response);
        }
        catch (GroupReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (GuestSessionNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ActionForbiddenException exception)
        {
            return ResponseBuilder.CreateErrorResponse(403, exception.Message, context.Logger);
        }
        catch (GroupReservationExpiredException exception)
        {
            return ResponseBuilder.CreateErrorResponse(409, exception.Message, context.Logger);
        }
        catch (GroupReservationSubmittedException exception)
        {
            return ResponseBuilder.CreateErrorResponse(409, exception.Message, context.Logger);
        }
        catch (GuestSessionSubmittedException exception)
        {
            return ResponseBuilder.CreateErrorResponse(409, exception.Message, context.Logger);
        }
        catch (DishNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ValidationException exception)
        {
            return ResponseBuilder.CreateErrorCodeResponse(exception);
        }
    }

    private static bool TryDeserializeRequest(
        APIGatewayProxyRequest request,
        out UpdateGroupSessionDishRequest? updateRequest)
    {
        try
        {
            updateRequest = JsonSerializer.Deserialize<UpdateGroupSessionDishRequest>(
                request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return updateRequest is not null;
        }
        catch (JsonException)
        {
            updateRequest = null;
            return false;
        }
    }
}
