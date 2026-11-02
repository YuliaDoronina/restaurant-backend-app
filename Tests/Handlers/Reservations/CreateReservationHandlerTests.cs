using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.Reservations;
using APIHandler.Models.Reservations;
using APIHandler.Services.Reservations;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.Reservations;

public sealed class CreateReservationHandlerTests
{
    private const string CallerUserId = "customer-1";

    private readonly Mock<IReservationService> _reservationService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly CreateReservationHandler _handler;

    public CreateReservationHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new CreateReservationHandler(_reservationService.Object);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationExpired_ReturnsConflictWithExceptionMessage()
    {
        // Arrange
        GroupReservationExpiredException exception = new();
        SetupServiceToThrow(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_GroupReservationSubmitted_ReturnsConflictWithExceptionMessage()
    {
        // Arrange
        GroupReservationSubmittedException exception = new();
        SetupServiceToThrow(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_DuplicateOwnerSessionsExist_ReturnsConflictWithExceptionMessage()
    {
        // Arrange
        DuplicateOwnerSessionException exception = new("group-1");
        SetupServiceToThrow(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    private void SetupServiceToThrow(Exception exception)
    {
        _reservationService
            .Setup(service => service.CreateReservationAsync(
                It.IsAny<CreateReservationRequest>(),
                CallerUserId,
                false,
                _logger.Object))
            .ThrowsAsync(exception);
    }

    private static APIGatewayProxyRequest CreateRequest() => new()
    {
        Body = JsonSerializer.Serialize(new CreateReservationRequest
        {
            TableId = "table-1",
            StartTime = DateTime.UtcNow.AddHours(1),
            EndTime = DateTime.UtcNow.AddHours(2),
            GuestCount = 2
        }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = new Dictionary<string, string>
                {
                    ["sub"] = CallerUserId
                }
            }
        }
    };

    private static string GetErrorMessage(APIGatewayProxyResponse response)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("error").GetString()!;
    }
}
