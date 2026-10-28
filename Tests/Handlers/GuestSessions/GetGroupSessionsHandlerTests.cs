using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class GetGroupSessionsHandlerTests
{
    private const string GroupReservationId = "group-1";
    private const string OwnerUserId = "owner-1";

    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly GetGroupSessionsHandler _handler;

    public GetGroupSessionsHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new GetGroupSessionsHandler(_guestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationIdMissing_ReturnsBadRequest()
    {
        APIGatewayProxyRequest request = CreateRequest(includeGroupReservationId: false);

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Missing 'groupReservationId' in path parameters.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_AuthenticationClaimsMissing_ReturnsUnauthorized()
    {
        APIGatewayProxyRequest request = CreateRequest(includeClaims: false);

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_SubClaimMissing_ReturnsUnauthorized()
    {
        APIGatewayProxyRequest request = CreateRequest(claims: new Dictionary<string, string>());

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ValidOwnerRequest_ReturnsOkWithGroupSessions()
    {
        GroupSessionsResponse expected = new(
            [
                new GroupSessionParticipantResponse(
                    "guest-session",
                    ParticipantType.Guest,
                    "Andrii",
                    [
                        new GroupSessionItemResponse(
                            new GroupSessionDishResponse("dish-1", "Caesar Salad", 180m),
                            2)
                    ],
                    360m,
                    true)
            ],
            360m);
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId))
            .ReturnsAsync(expected);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        GroupSessionsResponse? actual = JsonSerializer.Deserialize<GroupSessionsResponse>(response.Body);
        Assert.NotNull(actual);
        Assert.Equal(360m, actual.Total);
        GroupSessionParticipantResponse participant = Assert.Single(actual.Participants);
        Assert.Equal("guest-session", participant.Id);
        Assert.Equal(ParticipantType.Guest, participant.Type);
        Assert.Equal("Andrii", participant.Name);
        Assert.True(participant.IsSubmitted);
        GroupSessionItemResponse item = Assert.Single(participant.Items);
        Assert.Equal("dish-1", item.Dish.Id);
        Assert.Equal("Caesar Salad", item.Dish.Name);
        Assert.Equal(180m, item.Dish.Price);
        Assert.Equal(2, item.Quantity);
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFound_ReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Group reservation was not found", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFound_ReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId))
            .ThrowsAsync(new ReservationNotFoundException("Reservation not found."));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Reservation not found.", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_CallerIsNotReservationOwner_ReturnsForbidden()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("Only the reservation owner can perform this action.", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_UnexpectedServiceException_IsNotSwallowed()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(OwnerUserId, GroupReservationId))
            .ThrowsAsync(new InvalidOperationException("Unexpected service error."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.HandleRequest(CreateRequest(), _lambdaContext.Object));
    }

    private static APIGatewayProxyRequest CreateRequest(
        bool includeGroupReservationId = true,
        bool includeClaims = true,
        Dictionary<string, string>? claims = null)
    {
        APIGatewayProxyRequest request = new()
        {
            PathParameters = includeGroupReservationId
                ? new Dictionary<string, string> { ["groupReservationId"] = GroupReservationId }
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
