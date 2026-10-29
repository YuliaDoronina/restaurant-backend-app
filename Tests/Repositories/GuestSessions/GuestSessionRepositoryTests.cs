using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Repositories.GuestSessions;
using Moq;

namespace APIHandler.Tests.Repositories.GuestSessions;

[Collection(DynamoDbRepositoryTestCollection.Name)]
public sealed class GuestSessionRepositoryTests : IDisposable
{
    private readonly string? _originalGuestSessionTable = Environment.GetEnvironmentVariable("guestSessionTable");
    private readonly string? _originalGroupReservationsTable = Environment.GetEnvironmentVariable("groupReservationsTable");

    public GuestSessionRepositoryTests()
    {
        Environment.SetEnvironmentVariable("guestSessionTable", "guest-sessions-test");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");
    }

    [Fact]
    public async Task SaveCartAsync_WritesSessionInTransactionConditionedOnOpenGroup()
    {
        Mock<IDynamoDBContext> context = CreateContextMock();
        Mock<IAmazonDynamoDB> client = new();
        TransactWriteItemsRequest? capturedRequest = null;
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new TransactWriteItemsResponse());
        GuestSessionRepository repository = new(context.Object, client.Object);

        await repository.SaveCartAsync(CreateSession());

        Assert.NotNull(capturedRequest);
        Assert.Equal(2, capturedRequest.TransactItems.Count);
        ConditionCheck condition = Assert.IsType<ConditionCheck>(capturedRequest.TransactItems[0].ConditionCheck);
        Assert.Equal("groups-test", condition.TableName);
        Assert.Equal("#status = :openStatus", condition.ConditionExpression);
        Assert.Equal("status", condition.ExpressionAttributeNames["#status"]);
        Assert.Equal("0", condition.ExpressionAttributeValues[":openStatus"].N);
        Put sessionPut = Assert.IsType<Put>(capturedRequest.TransactItems[1].Put);
        Assert.Equal("guest-sessions-test", sessionPut.TableName);
        client.Verify(
            dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_DeletesSessionInTransactionConditionedOnOpenGroup()
    {
        Mock<IDynamoDBContext> context = CreateContextMock();
        Mock<IAmazonDynamoDB> client = new();
        TransactWriteItemsRequest? capturedRequest = null;
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new TransactWriteItemsResponse());
        GuestSessionRepository repository = new(context.Object, client.Object);

        await repository.DeleteAsync(CreateSession());

        Assert.NotNull(capturedRequest);
        Assert.Equal(2, capturedRequest.TransactItems.Count);
        Assert.NotNull(capturedRequest.TransactItems[0].ConditionCheck);
        Delete sessionDelete = Assert.IsType<Delete>(capturedRequest.TransactItems[1].Delete);
        Assert.Equal("guest-sessions-test", sessionDelete.TableName);
        Assert.Equal("session-1", sessionDelete.Key["guestSessionId"].S);
    }

    [Theory]
    [InlineData(GroupReservationStatus.Submitting)]
    [InlineData(GroupReservationStatus.Submitted)]
    public async Task SaveCartAsync_GroupIsNoLongerOpen_TransactionCannotWriteSession(GroupReservationStatus currentStatus)
    {
        Mock<IDynamoDBContext> context = CreateContextMock();
        context
            .Setup(dynamoContext => dynamoContext.LoadAsync<GroupReservation>(
                It.IsAny<object>(),
                It.IsAny<LoadConfig>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateGroup(currentStatus));
        Mock<IAmazonDynamoDB> client = new();
        TransactWriteItemsRequest? capturedRequest = null;
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<TransactWriteItemsRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ThrowsAsync(new TransactionCanceledException("group status condition failed")
            {
                CancellationReasons =
                [
                    new CancellationReason { Code = "ConditionalCheckFailed" },
                    new CancellationReason { Code = "None" }
                ]
            });
        GuestSessionRepository repository = new(context.Object, client.Object);

        await Assert.ThrowsAsync<GroupReservationSubmittedException>(() => repository.SaveCartAsync(CreateSession()));

        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest.TransactItems[0].ConditionCheck);
        Assert.NotNull(capturedRequest.TransactItems[1].Put);
        context.Verify(
            dynamoContext => dynamoContext.SaveAsync(It.IsAny<GuestSession>(), It.IsAny<CancellationToken>()),
            Times.Never);
        client.Verify(
            dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SaveCartAsync_GroupClosedBeforeTransaction_ThrowsExpiredWithoutWritingSession()
    {
        Mock<IDynamoDBContext> context = CreateContextMock();
        context
            .Setup(dynamoContext => dynamoContext.LoadAsync<GroupReservation>(
                It.IsAny<object>(),
                It.IsAny<LoadConfig>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateGroup(GroupReservationStatus.Closed));
        Mock<IAmazonDynamoDB> client = new();
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransactionCanceledException("group status condition failed")
            {
                CancellationReasons =
                [
                    new CancellationReason { Code = "ConditionalCheckFailed" },
                    new CancellationReason { Code = "None" }
                ]
            });
        GuestSessionRepository repository = new(context.Object, client.Object);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => repository.SaveCartAsync(CreateSession()));
    }

    [Fact]
    public async Task GetAllByGroupReservationIdConsistentAsync_ScansWithStrongConsistency()
    {
        Mock<IDynamoDBContext> context = new();
        Mock<IAmazonDynamoDB> client = new();
        Mock<AsyncSearch<GuestSession>> search = new();
        search
            .Setup(asyncSearch => asyncSearch.GetRemainingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        ScanConfig? capturedConfig = null;
        IEnumerable<ScanCondition>? capturedConditions = null;
        context
            .Setup(dynamoContext => dynamoContext.ScanAsync<GuestSession>(
                It.IsAny<IEnumerable<ScanCondition>>(),
                It.IsAny<ScanConfig>()))
            .Callback<IEnumerable<ScanCondition>, ScanConfig>((conditions, config) =>
            {
                capturedConditions = conditions;
                capturedConfig = config;
            })
            .Returns(search.Object);
        GuestSessionRepository repository = new(context.Object, client.Object);

        List<GuestSession> result = await repository.GetAllByGroupReservationIdConsistentAsync("group-1");

        Assert.Empty(result);
        Assert.NotNull(capturedConfig);
        Assert.True(capturedConfig.ConsistentRead);
        Assert.Equal("guest-sessions-test", capturedConfig.OverrideTableName);
        ScanCondition condition = Assert.Single(capturedConditions!);
        Assert.Equal(nameof(GuestSession.GroupReservationId), condition.PropertyName);
        Assert.Equal("group-1", Assert.Single(condition.Values).ToString());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("guestSessionTable", _originalGuestSessionTable);
        Environment.SetEnvironmentVariable("groupReservationsTable", _originalGroupReservationsTable);
    }

    private static Mock<IDynamoDBContext> CreateContextMock()
    {
        Mock<IDynamoDBContext> context = new();
        context
            .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GuestSession>()))
            .Returns(new Document { ["guestSessionId"] = "session-1" });
        context
            .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
            .Returns<GroupReservation>(group => new Document { ["status"] = (int)group.Status });
        return context;
    }

    private static GuestSession CreateSession() => new()
    {
        Id = "session-1",
        GroupReservationId = "group-1",
        GuestName = "Guest",
        IsSubmitted = false,
        Dishes = []
    };

    private static GroupReservation CreateGroup(GroupReservationStatus status) => new()
    {
        GroupReservationId = "group-1",
        ReservationId = "reservation-1",
        ReservationToken = "token-1",
        Status = status
    };
}
