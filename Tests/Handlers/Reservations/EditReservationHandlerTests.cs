using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Handlers.Reservations;
using APIHandler.Models.Reservations;
using APIHandler.Services.Locations;
using APIHandler.Services.Reservations;
using APIHandler.Tests.Repositories;
using Moq;
using System.Text.Json;
using EntityTable = APIHandler.Entities.Table;

namespace APIHandler.Tests.Handlers.Reservations;

[Collection(DynamoDbRepositoryTestCollection.Name)]
public sealed class EditReservationHandlerTests : IDisposable
{
    private const string ReservationId = "reservation-1";
    private const string CustomerId = "customer-1";
    private const string TableId = "table-1";

    private readonly string? _originalReservationsTable =
        Environment.GetEnvironmentVariable("reservationsTable");
    private readonly string? _originalTablesTable =
        Environment.GetEnvironmentVariable("tablesTable");
    private readonly string? _originalReservationsIndex =
        Environment.GetEnvironmentVariable("reservationsTableGlobalIndex");

    private readonly Mock<IDynamoDBContext> _dbContext = new();
    private readonly Mock<ILocationService> _locationService = new();
    private readonly Mock<IReservationService> _reservationService = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();
    private readonly EditReservationHandler _handler;

    public EditReservationHandlerTests()
    {
        Environment.SetEnvironmentVariable("reservationsTable", "reservations-test");
        Environment.SetEnvironmentVariable("tablesTable", "tables-test");
        Environment.SetEnvironmentVariable("reservationsTableGlobalIndex", "reservations-by-table-test");

        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _handler = new EditReservationHandler(
            _dbContext.Object,
            _locationService.Object,
            _reservationService.Object);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 3)]
    [InlineData(2, 2)]
    [InlineData(1, 1)]
    public async Task HandleRequest_AfterSuccessfulSave_InvokesGroupLifecycleForUpdatedReservation(
        int previousGuestCount,
        int updatedGuestCount)
    {
        // Arrange
        ConfigureSuccessfulEdit(previousGuestCount);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(updatedGuestCount),
            _lambdaContext.Object);

        // Assert
        Assert.Equal(200, response.StatusCode);
        _dbContext.Verify(
            context => context.SaveAsync(
                It.Is<Reservation>(reservation =>
                    reservation.Id == ReservationId &&
                    reservation.GuestCount == updatedGuestCount),
                It.IsAny<SaveConfig>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _reservationService.Verify(
            service => service.EnsureGroupReservationForEditAsync(
                It.Is<Reservation>(reservation =>
                    reservation.Id == ReservationId &&
                    reservation.GuestCount == updatedGuestCount)),
            Times.Once);
    }

    [Fact]
    public async Task HandleRequest_WhenPreviousLifecycleAttemptFailed_RetriesAfterNextSuccessfulSave()
    {
        // Arrange
        ConfigureSuccessfulEdit(previousGuestCount: 1);
        InvalidOperationException previousFailure = new("Owner-session creation failed.");
        _reservationService
            .SetupSequence(service => service.EnsureGroupReservationForEditAsync(It.IsAny<Reservation>()))
            .ThrowsAsync(previousFailure)
            .Returns(Task.CompletedTask);

        // Act
        InvalidOperationException actualFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _handler.HandleRequest(CreateRequest(2), _lambdaContext.Object));
        APIGatewayProxyResponse retryResponse = await _handler.HandleRequest(
            CreateRequest(2),
            _lambdaContext.Object);

        // Assert
        Assert.Same(previousFailure, actualFailure);
        Assert.Equal(200, retryResponse.StatusCode);
        _dbContext.Verify(
            context => context.SaveAsync(
                It.Is<Reservation>(reservation =>
                    reservation.Id == ReservationId &&
                    reservation.GuestCount == 2),
                It.IsAny<SaveConfig>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _reservationService.Verify(
            service => service.EnsureGroupReservationForEditAsync(
                It.Is<Reservation>(reservation =>
                    reservation.Id == ReservationId &&
                    reservation.GuestCount == 2)),
            Times.Exactly(2));
    }

    [Fact]
    public async Task HandleRequest_GroupReservationExpired_ReturnsConflictWithExceptionMessage()
    {
        // Arrange
        ConfigureSuccessfulEdit(previousGuestCount: 1);
        GroupReservationExpiredException exception = new();
        _reservationService
            .Setup(service => service.EnsureGroupReservationForEditAsync(It.IsAny<Reservation>()))
            .ThrowsAsync(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(2),
            _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_GroupReservationSubmitted_ReturnsConflictWithExceptionMessage()
    {
        // Arrange
        ConfigureSuccessfulEdit(previousGuestCount: 1);
        GroupReservationSubmittedException exception = new();
        _reservationService
            .Setup(service => service.EnsureGroupReservationForEditAsync(It.IsAny<Reservation>()))
            .ThrowsAsync(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(2),
            _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    [Fact]
    public async Task HandleRequest_DuplicateOwnerSessionsExist_ReturnsConflictWithExceptionMessage()
    {
        // Arrange
        ConfigureSuccessfulEdit(previousGuestCount: 1);
        DuplicateOwnerSessionException exception = new("group-1");
        _reservationService
            .Setup(service => service.EnsureGroupReservationForEditAsync(It.IsAny<Reservation>()))
            .ThrowsAsync(exception);

        // Act
        APIGatewayProxyResponse response = await _handler.HandleRequest(
            CreateRequest(2),
            _lambdaContext.Object);

        // Assert
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(exception.Message, GetErrorMessage(response));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("reservationsTable", _originalReservationsTable);
        Environment.SetEnvironmentVariable("tablesTable", _originalTablesTable);
        Environment.SetEnvironmentVariable("reservationsTableGlobalIndex", _originalReservationsIndex);
    }

    private void ConfigureSuccessfulEdit(int previousGuestCount)
    {
        _dbContext
            .Setup(context => context.LoadAsync<Reservation>(
                ReservationId,
                It.IsAny<LoadConfig>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Reservation
            {
                Id = ReservationId,
                TableId = TableId,
                CustomerId = CustomerId,
                WaiterId = "waiter-1",
                GuestCount = previousGuestCount,
                Status = ReservationStatus.Reserved,
                StartTime = DateTime.UtcNow.AddHours(2),
                EndTime = DateTime.UtcNow.AddHours(3)
            });
        _dbContext
            .Setup(context => context.LoadAsync<EntityTable>(
                TableId,
                It.IsAny<LoadConfig>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityTable
            {
                Id = TableId,
                LocationId = "location-1",
                Capacity = 8,
                TableNumber = 1
            });

        Mock<AsyncSearch<Reservation>> search = new();
        search
            .Setup(asyncSearch => asyncSearch.GetRemainingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _dbContext
            .Setup(context => context.QueryAsync<Reservation>(
                TableId,
                It.IsAny<QueryConfig>()))
            .Returns(search.Object);
        _dbContext
            .Setup(context => context.SaveAsync(
                It.IsAny<Reservation>(),
                It.IsAny<SaveConfig>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _locationService
            .Setup(service => service.GetLocationByTableIdAsync(TableId))
            .ReturnsAsync(new Location
            {
                Id = "location-1",
                Address = "1 Main Street",
                Description = "Restaurant",
                TotalCapacity = 50,
                AverageOccupancy = "0"
            });
    }

    private static APIGatewayProxyRequest CreateRequest(int guestCount) => new()
    {
        Body = JsonSerializer.Serialize(new EditReservationRequest
        {
            ReservationId = ReservationId,
            StartTime = DateTime.UtcNow.AddHours(1),
            EndTime = DateTime.UtcNow.AddHours(2),
            GuestCount = guestCount
        }),
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = new Dictionary<string, string>
                {
                    ["sub"] = CustomerId,
                    ["cognito:groups"] = "[\"Customer\"]"
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
