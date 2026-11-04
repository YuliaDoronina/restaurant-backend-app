using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class UpdateGroupSessionDishHandlerTests
{
    private readonly Mock<IGuestSessionService> service = new();
    private readonly Mock<ILambdaContext> context = new();
    private readonly Mock<ILambdaLogger> logger = new();
    private readonly UpdateGroupSessionDishHandler handler;

    public UpdateGroupSessionDishHandlerTests()
    {
        context.SetupGet(value => value.Logger).Returns(logger.Object);
        handler = new UpdateGroupSessionDishHandler(service.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequestPassesOwnerAndDishUpdateToService()
    {
        GroupSessionsResponse expected = new([], 0m);
        service
            .Setup(value => value.UpdateGroupSessionDishAsync("owner-1", "group-1", "guest-1", "dish-1", 0))
            .ReturnsAsync(expected);

        APIGatewayProxyResponse response = await handler.HandleRequest(CreateRequest(), context.Object);

        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(JsonSerializer.Deserialize<GroupSessionsResponse>(response.Body));
        service.Verify(
            value => value.UpdateGroupSessionDishAsync("owner-1", "group-1", "guest-1", "dish-1", 0),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_MissingPathParameterReturnsBadRequest()
    {
        APIGatewayProxyRequest request = CreateRequest();
        request.PathParameters!.Remove("guestSessionId");

        APIGatewayProxyResponse response = await handler.HandleRequest(request, context.Object);

        Assert.Equal(400, response.StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleRequest_MissingSubReturnsUnauthorized()
    {
        APIGatewayProxyRequest request = CreateRequest();
        request.RequestContext = new() { Authorizer = new() { Claims = new Dictionary<string, string>() } };

        APIGatewayProxyResponse response = await handler.HandleRequest(request, context.Object);

        Assert.Equal(401, response.StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HandleRequest_InvalidQuantityReturnsValidationError()
    {
        service
            .Setup(value => value.UpdateGroupSessionDishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new ValidationException("Quantity cannot be negative."));

        APIGatewayProxyResponse response = await handler.HandleRequest(
            CreateRequest("""{"dishId":"dish-1","quantity":-1}"""), context.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Quantity cannot be negative.", GetProperty(response, "message"));
    }

    private static APIGatewayProxyRequest CreateRequest(string body = """{"dishId":"dish-1","quantity":0}""")
    {
        return new APIGatewayProxyRequest
        {
            Body = body,
            PathParameters = new Dictionary<string, string>
            {
                ["groupReservationId"] = "group-1",
                ["guestSessionId"] = "guest-1"
            },
            RequestContext = new()
            {
                Authorizer = new()
                {
                    Claims = new Dictionary<string, string> { ["sub"] = "owner-1" }
                }
            }
        };
    }

    private static string GetProperty(APIGatewayProxyResponse response, string property)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty(property).GetString()!;
    }
}
