using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Entities;
using APIHandler.Handlers.TimeSlots;
using APIHandler.Models.TimeSlots;
using APIHandler.Tests.Repositories;
using System.Text.Json;
using Moq;

namespace APIHandler.Tests.Handlers.TimeSlots;

[Collection(DynamoDbRepositoryTestCollection.Name)]
public sealed class GetAvailableTimeSlotsHandlerTests : IDisposable
{
    private readonly string? _originalReservationsTable =
        Environment.GetEnvironmentVariable("reservationsTable");
    private readonly string? _originalReservationsIndex =
        Environment.GetEnvironmentVariable("reservationsTableGlobalIndex");

    private readonly Mock<IDynamoDBContext> _dbContext = new();
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public GetAvailableTimeSlotsHandlerTests()
    {
        Environment.SetEnvironmentVariable("reservationsTable", "reservations-test");
        Environment.SetEnvironmentVariable("reservationsTableGlobalIndex", "reservations-by-table-test");
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
    }

    [Theory]
    [InlineData(30, "10:00")]
    [InlineData(29, "11:45")]
    public async Task HandleRequest_ReturnsOnlySlotsAtLeast30MinutesAway(
        int minutesUntilSlot,
        string expectedFirstSlot)
    {
        DateTime now = new(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);
        now = now.AddMinutes(-minutesUntilSlot);
        Mock<AsyncSearch<Reservation>> search = new();
        search
            .Setup(asyncSearch => asyncSearch.GetRemainingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _dbContext
            .Setup(context => context.QueryAsync<Reservation>(
                "table-1",
                It.IsAny<QueryConfig>()))
            .Returns(search.Object);

        GetAvailableTimeSlotsHandler handler = new(_dbContext.Object, () => now);
        APIGatewayProxyResponse response = await handler.HandleRequest(
            new APIGatewayProxyRequest
            {
                PathParameters = new Dictionary<string, string> { ["id"] = "table-1" },
                QueryStringParameters = new Dictionary<string, string>
                {
                    ["startTime"] = "2026-10-17T10:00:00Z"
                }
            },
            _lambdaContext.Object);

        List<TimeSlotResponse> slots =
            JsonSerializer.Deserialize<List<TimeSlotResponse>>(response.Body)!;

        Assert.Equal(200, response.StatusCode);
        Assert.NotEmpty(slots);
        Assert.Equal(expectedFirstSlot, slots[0].Start.ToUniversalTime().ToString("HH:mm"));
        Assert.All(
            slots,
            slot => Assert.True(
                slot.Start.ToUniversalTime() >= now.AddMinutes(30)));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("reservationsTable", _originalReservationsTable);
        Environment.SetEnvironmentVariable("reservationsTableGlobalIndex", _originalReservationsIndex);
    }
}
