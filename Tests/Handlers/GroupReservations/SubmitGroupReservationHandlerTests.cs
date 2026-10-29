using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GroupReservations;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GroupReservations;

public class SubmitGroupReservationHandlerTests
{
    private const string GroupReservationId = "group-1";
    private const string ReservationId = "reservation-1";
    private const string OwnerUserId = "owner-1";

    private readonly Mock<IGroupReservationService> _service = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly SubmitGroupReservationHandler _handler;

    public SubmitGroupReservationHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new SubmitGroupReservationHandler(_service.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidOwnerRequest_ReturnsAggregatedItemsAndCallsService()
    {
        IReadOnlyList<GroupReservationOrderItemResponse> items =
        [
            new() { DishId = "dish-1", Quantity = 5 },
            new() { DishId = "dish-2", Quantity = 2 }
        ];
        _service
            .Setup(service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId))
            .ReturnsAsync(items);

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(200, response.StatusCode);
        GroupReservationOrderItemResponse[]? result =
            JsonSerializer.Deserialize<GroupReservationOrderItemResponse[]>(response.Body);
        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("dish-1", result[0].DishId);
        Assert.Equal(5, result[0].Quantity);
        Assert.Equal("dish-2", result[1].DishId);
        Assert.Equal(2, result[1].Quantity);
        _service.Verify(
            service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationIdMissing_ReturnsBadRequestWithoutCallingService()
    {
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(includeGroupReservationId: false),
            _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Missing 'groupReservationId' in path parameters.", GetErrorMessage(response));
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(null, "Body is empty")]
    [InlineData("{", "Invalid json body")]
    [InlineData("{}", "data-annotation")]
    [InlineData("{\"reservationId\":\"\"}", "data-annotation")]
    public async Task HandleRequest_InvalidBody_ReturnsBadRequestWithoutCallingService(string? body, string expectedMessage)
    {
        APIGatewayProxyRequest request = CreateRequest();
        request.Body = body;

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        if (expectedMessage == "data-annotation")
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);
            Assert.Equal("One or more validation errors occurred.", document.RootElement.GetProperty("title").GetString());
            Assert.Contains("The ReservationId field is required.", response.Body);
        }
        else
        {
            Assert.Equal(expectedMessage, GetErrorMessage(response));
        }
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    public async Task HandleRequest_AuthenticationOrSubjectMissing_ReturnsUnauthorizedWithoutCallingService(
        bool includeClaims,
        string? subject)
    {
        APIGatewayProxyRequest request = CreateRequest(includeClaims: includeClaims);
        if (includeClaims && subject is not null)
            request.RequestContext!.Authorizer!.Claims!["sub"] = subject;
        if (includeClaims && subject is null)
            request.RequestContext!.Authorizer!.Claims!.Remove("sub");

        APIGatewayProxyResponse response = await _handler.HandleRequest(request, _lambdaContext.Object);

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Unauthorized.", GetErrorMessage(response));
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFound_ReturnsNotFound()
    {
        _service
            .Setup(service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Group reservation was not found", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFound_ReturnsNotFound()
    {
        _service
            .Setup(service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId))
            .ThrowsAsync(new ReservationNotFoundException("Reservation was not found."));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Reservation not found.", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_ReservationIdMismatch_ReturnsBadRequest()
    {
        _service
            .Setup(service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId))
            .ThrowsAsync(new ValidationException("The reservationId does not match the group reservation."));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("The reservationId does not match the group reservation.", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_CallerIsNotReservationOwner_ReturnsForbidden()
    {
        _service
            .Setup(service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can submit the group pre-order."));

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("Only the reservation owner can submit the group pre-order.", GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_SubmitConflict_ReturnsConflict()
    {
        _service
            .Setup(service => service.SubmitGroupReservationAsync(GroupReservationId, ReservationId, OwnerUserId))
            .ThrowsAsync(new GroupReservationSubmitConflictException());

        APIGatewayProxyResponse response = await _handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("Group reservation can no longer be submitted.", GetErrorMessage(response));
    }

    private static APIGatewayProxyRequest CreateRequest(bool includeGroupReservationId = true, bool includeClaims = true) => new()
    {
        PathParameters = includeGroupReservationId
            ? new Dictionary<string, string> { ["groupReservationId"] = GroupReservationId }
            : new Dictionary<string, string>(),
        Body = "{\"reservationId\":\"reservation-1\"}",
        RequestContext = includeClaims
            ? new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    Claims = new Dictionary<string, string> { ["sub"] = OwnerUserId }
                }
            }
            : null
    };

    private static string GetErrorMessage(APIGatewayProxyResponse response)
    {
        using JsonDocument document = JsonDocument.Parse(response.Body);
        return document.RootElement.GetProperty("error").GetString()!;
    }

    private void VerifyServiceNotCalled() =>
        _service.Verify(
            service => service.SubmitGroupReservationAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Never);
}
