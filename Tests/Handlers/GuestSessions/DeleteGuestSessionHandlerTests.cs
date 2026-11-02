using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class DeleteGuestSessionHandlerTests
{
    private const string GroupReservationId = "group-1";
    private const string GuestSessionId = "guest-1";
    private const string OwnerUserId = "owner-1";

    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly DeleteGuestSessionHandler _handler;

    public DeleteGuestSessionHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new DeleteGuestSessionHandler(_guestSessionService.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequest_ReturnsOkWithUpdatedSnapshot()
    {
        GroupSessionsResponse expected = new(
            [
                new GroupSessionParticipantResponse(
                    "owner-session",
                    ParticipantType.Owner,
                    "Olena",
                    [
                        new GroupSessionItemResponse(
                            new GroupSessionDishResponse("soup", "Soup", 80m),
                            1)
                    ],
                    80m,
                    true),
                new GroupSessionParticipantResponse(
                    "remaining-guest",
                    ParticipantType.Guest,
                    "Andrii",
                    [
                        new GroupSessionItemResponse(
                            new GroupSessionDishResponse("salad", "Caesar Salad", 180m),
                            2)
                    ],
                    360m,
                    false)
            ],
            440m);
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ReturnsAsync(expected);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        GroupSessionsResponse? actual = JsonSerializer.Deserialize<GroupSessionsResponse>(response.Body);
        Assert.NotNull(actual);
        Assert.Equal(440m, actual.Total);
        GroupSessionParticipantResponse[] participants = actual.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal("owner-session", participants[0].Id);
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal("Olena", participants[0].Name);
        Assert.Equal(80m, participants[0].Total);
        Assert.Equal("remaining-guest", participants[1].Id);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal("Andrii", participants[1].Name);
        Assert.Equal(360m, participants[1].Total);
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_GuestSessionDoesNotExist_ReturnsNotFound()
    {
        const string message = "Guest session with id guest-1 not found.";
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new GuestSessionNotFoundException(message));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(message, GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId),
            Times.Once);
    }

    [Theory]
    [InlineData("Owner sessions cannot be deleted.")]
    [InlineData("Guest session does not belong to this group reservation.")]
    public async Task HandleRequest_DeletionValidationFails_ReturnsBadRequest(string message)
    {
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new ValidationException(message));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(response.Body);
        Assert.Equal(message, document.RootElement.GetProperty("message").GetString());
        Assert.Equal("Unknown", document.RootElement.GetProperty("errorCode").GetString());
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_CallerIsNotReservationOwner_ReturnsForbidden()
    {
        const string message = "Only the reservation owner can perform this action.";
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new ActionForbiddenException(message));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(message, GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId),
            Times.Once);
    }

    [Theory]
    [InlineData("groupReservationId", "Missing 'groupReservationId' in path parameters.")]
    [InlineData("guestSessionId", "Missing 'guestSessionId' in path parameters.")]
    public async Task HandleRequest_PathParameterMissing_ReturnsBadRequestWithoutCallingService(
        string missingParameter,
        string expectedMessage)
    {
        APIGatewayProxyRequest request = CreateRequest();
        request.PathParameters.Remove(missingParameter);

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal(expectedMessage, GetErrorMessage(response));
        VerifyServiceNeverCalled();
    }

    [Fact]
    public async Task HandleRequest_GroupReservationDoesNotExist_ReturnsNotFound()
    {
        GroupReservationNotFoundException exception = new();
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(exception);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_ParentReservationDoesNotExist_ReturnsNotFound()
    {
        ReservationNotFoundException exception = new("Parent reservation not found.");
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(exception);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_GroupReservationExpired_ReturnsConflict()
    {
        GroupReservationExpiredException exception = new();
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(exception);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_GroupReservationSubmitted_ReturnsConflict()
    {
        GroupReservationSubmittedException exception = new();
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(exception);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_DuplicateOwnerSessionsExist_ReturnsConflict()
    {
        // Arrange
        DuplicateOwnerSessionException exception = new(GroupReservationId);
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_UnexpectedServiceException_PropagatesException()
    {
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(OwnerUserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new InvalidOperationException("Unexpected service error."));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.HandleRequest(CreateRequest(), _lambdaContext.Object));

        Assert.Equal("Unexpected service error.", exception.Message);
    }

    [Fact]
    public async Task HandleRequest_AuthenticationClaimsMissing_ReturnsUnauthorized()
    {
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(includeClaims: false),
            _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_SubClaimMissing_ReturnsUnauthorized()
    {
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(claims: new Dictionary<string, string>()),
            _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_SubClaimEmpty_ReturnsUnauthorized()
    {
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(claims: new Dictionary<string, string> { ["sub"] = string.Empty }),
            _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
    }

    private void VerifyServiceNeverCalled()
    {
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
    }

    private static APIGatewayProxyRequest CreateRequest(
        bool includeClaims = true,
        Dictionary<string, string>? claims = null)
    {
        APIGatewayProxyRequest request = new()
        {
            PathParameters = new Dictionary<string, string>
            {
                ["groupReservationId"] = GroupReservationId,
                ["guestSessionId"] = GuestSessionId
            }
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
