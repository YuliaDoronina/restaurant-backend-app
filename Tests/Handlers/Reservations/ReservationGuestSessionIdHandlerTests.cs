using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.Reservations;
using APIHandler.Models.Reservations;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.Reservations;

public class ReservationGuestSessionIdHandlerTests
{
    private const string ReservationId = "reservation-1";
    private const string OwnerUserId = "owner-1";

    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly GetReservationGuestSessionIdHandler _handler;

    public ReservationGuestSessionIdHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new GetReservationGuestSessionIdHandler(_guestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_ReservationOwner_ReturnsGuestSessionId()
    {
        ReservationGuestSessionIdResponse expected = new() { GuestSessionId = "owner-session-1" };
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ReturnsAsync(expected);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(response.Body);
        Assert.Equal("owner-session-1", document.RootElement.GetProperty("guestSessionId").GetString());
        _guestSessionService.Verify(
            service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId),
            Times.Once);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "")]
    public async Task HandleRequest_ReservationIdMissingOrEmpty_ReturnsBadRequest(
        bool includeReservationId,
        string? reservationId)
    {
        APIGatewayProxyRequest request = CreateRequest(includeReservationId: false);
        if (includeReservationId)
            request.PathParameters!["reservationId"] = reservationId!;

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Missing 'reservationId' in path parameters.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.GetReservationGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    public async Task HandleRequest_ClaimsOrSubMissingOrEmpty_ReturnsUnauthorized(
        bool includeClaims,
        string? sub)
    {
        Dictionary<string, string>? claims = sub is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["sub"] = sub };

        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(includeClaims: includeClaims, claims: claims),
            _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.GetReservationGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_CallerIsNotReservationOwner_ReturnsForbidden()
    {
        const string message = "Only the reservation owner can perform this action.";
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(new ActionForbiddenException(message));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(message, GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFound_ReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(new ReservationNotFoundException("Reservation not found."));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Reservation not found.", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_OwnerSessionNotFound_ReturnsNotFoundWithExpectedMessage()
    {
        const string message = "Owner session not found for this reservation";
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(new GuestSessionNotFoundException(message));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_ServiceValidationException_ReturnsBadRequest()
    {
        const string message = "Invalid reservation.";
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(new ValidationException(message));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_IoExceptionFromService_IsNotSwallowed()
    {
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(new IOException("Repository I/O failed."));

        await Assert.ThrowsAsync<IOException>(
            () => _handler.HandleRequest(CreateRequest(), _lambdaContext.Object));
    }

    [Fact]
    public async Task HandleRequest_UnexpectedServiceException_IsNotSwallowed()
    {
        _guestSessionService
            .Setup(service => service.GetReservationGuestSessionIdAsync(OwnerUserId, ReservationId))
            .ThrowsAsync(new InvalidOperationException("Unexpected service error."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.HandleRequest(CreateRequest(), _lambdaContext.Object));
    }

    private static APIGatewayProxyRequest CreateRequest(
        bool includeReservationId = true,
        bool includeClaims = true,
        Dictionary<string, string>? claims = null)
    {
        APIGatewayProxyRequest request = new()
        {
            PathParameters = includeReservationId
                ? new Dictionary<string, string> { ["reservationId"] = ReservationId }
                : new Dictionary<string, string>()
        };

        if (includeClaims)
        {
            request.RequestContext = new()
            {
                Authorizer = new()
                {
                    Claims = claims ?? new Dictionary<string, string> { ["sub"] = OwnerUserId }
                }
            };
        }

        return request;
    }

    private static string GetErrorMessage(APIGatewayProxyResponse response)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("error").GetString()!;
    }
}
