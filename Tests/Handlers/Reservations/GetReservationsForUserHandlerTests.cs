using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Handlers.Reservations;
using APIHandler.Models.Reservations;
using APIHandler.Models.Users;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.Reservations;
using APIHandler.Services.Locations;
using APIHandler.Services.Orders;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.Reservations;

public sealed class GetReservationsForUserHandlerTests
{
    private const string CustomerId = "customer-1";

    private readonly Mock<IReservationRepository> _reservationRepository = new();
    private readonly Mock<ILocationService> _locationService = new();
    private readonly Mock<IOrderService> _orderService = new();
    private readonly Mock<IGroupReservationRepository> _groupReservationRepository = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public GetReservationsForUserHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
        _locationService
            .Setup(service => service.GetLocationByTableIdAsync(It.IsAny<string>()))
            .ReturnsAsync(new Location
            {
                Id = "location-1",
                Address = "Test Street 1",
                Description = string.Empty,
                AverageOccupancy = string.Empty
            });
        _orderService
            .Setup(service => service.GetOrderByReservationIdAsync(It.IsAny<string>()))
            .ReturnsAsync((Order?)null);
    }

    [Fact]
    public async Task HandleRequest_CustomerUsesQueriedReservationsAndPreservesTheirOrder()
    {
        Reservation[] reservations =
        [
            CreateReservation("reservation-1"),
            CreateReservation("reservation-2")
        ];
        _reservationRepository
            .Setup(repository => repository.GetReservationsByCustomerIdAsync(CustomerId))
            .ReturnsAsync(reservations);

        GetReservationsForUserHandler handler = CreateHandler();

        APIGatewayProxyResponse response =
            await handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        ReservationResponse[] result = JsonSerializer.Deserialize<ReservationResponse[]>(response.Body)!;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(new[] { "reservation-1", "reservation-2" }, result.Select(reservation => reservation.Id));
        Assert.All(result, reservation => Assert.Equal("Test Street 1", reservation.Address));
        _reservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ConvertsNoMoreThanFiveReservationsAtOnce()
    {
        Reservation[] reservations = Enumerable.Range(1, 6)
            .Select(index => CreateReservation($"reservation-{index}"))
            .ToArray();
        _reservationRepository
            .Setup(repository => repository.GetReservationsByCustomerIdAsync(CustomerId))
            .ReturnsAsync(reservations);

        TaskCompletionSource<Location> releaseLocations = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> firstFiveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeLocationRequests = 0;
        int maxActiveLocationRequests = 0;

        _locationService
            .Setup(service => service.GetLocationByTableIdAsync(It.IsAny<string>()))
            .Returns(async (string _) =>
            {
                int active = Interlocked.Increment(ref activeLocationRequests);
                UpdateMaximum(ref maxActiveLocationRequests, active);
                if (active == 5)
                {
                    firstFiveStarted.TrySetResult(true);
                }

                try
                {
                    return await releaseLocations.Task;
                }
                finally
                {
                    Interlocked.Decrement(ref activeLocationRequests);
                }
            });

        GetReservationsForUserHandler handler = CreateHandler();
        Task<APIGatewayProxyResponse> responseTask =
            handler.HandleRequest(CreateRequest(), _lambdaContext.Object);

        await firstFiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, activeLocationRequests);
        Assert.Equal(5, maxActiveLocationRequests);

        releaseLocations.SetResult(new Location
        {
            Id = "location-1",
            Address = "Test Street 1",
            Description = string.Empty,
            AverageOccupancy = string.Empty
        });

        APIGatewayProxyResponse response = await responseTask;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(5, maxActiveLocationRequests);
    }

    private GetReservationsForUserHandler CreateHandler() =>
        new(
            _reservationRepository.Object,
            _locationService.Object,
            _orderService.Object,
            _groupReservationRepository.Object);

    private static APIGatewayProxyRequest CreateRequest() => new()
    {
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext
            {
                Claims = new Dictionary<string, string>
                {
                    ["sub"] = CustomerId,
                    ["cognito:groups"] = Roles.Customer.ToString()
                }
            }
        }
    };

    private static Reservation CreateReservation(string id) => new()
    {
        Id = id,
        TableId = "table-1",
        WaiterId = "waiter-1",
        GuestCount = 1,
        Status = ReservationStatus.Reserved
    };

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maximum);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref maximum, value, current) != current);
    }
}
