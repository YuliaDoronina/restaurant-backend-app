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
    public async Task TrySaveOwnerSessionAsync_WritesConditionallyInTransactionWithOpenGroupCheck()
    {
        // Arrange
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

        // Act
        bool result = await repository.TrySaveOwnerSessionAsync(CreateOwnerSession());

        // Assert
        Assert.True(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(2, capturedRequest.TransactItems.Count);
        ConditionCheck groupCondition = Assert.IsType<ConditionCheck>(
            capturedRequest.TransactItems[0].ConditionCheck);
        Assert.Equal("groups-test", groupCondition.TableName);
        Assert.Equal("#status = :openStatus", groupCondition.ConditionExpression);
        Put ownerPut = Assert.IsType<Put>(capturedRequest.TransactItems[1].Put);
        Assert.Equal("guest-sessions-test", ownerPut.TableName);
        Assert.Equal("attribute_not_exists(guestSessionId)", ownerPut.ConditionExpression);
        client.Verify(
            dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TrySaveOwnerSessionAsync_WhenOwnerKeyConditionFails_ReturnsFalseForCallerToReread()
    {
        // Arrange
        Mock<IDynamoDBContext> context = CreateContextMock();
        Mock<IAmazonDynamoDB> client = new();
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TransactionCanceledException("owner key already exists")
            {
                CancellationReasons =
                [
                    new CancellationReason { Code = "None" },
                    new CancellationReason { Code = "ConditionalCheckFailed" }
                ]
            });
        GuestSessionRepository repository = new(context.Object, client.Object);

        // Act
        bool result = await repository.TrySaveOwnerSessionAsync(CreateOwnerSession());

        // Assert
        Assert.False(result);
        client.Verify(
            dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TrySaveOwnerSessionAsync_WhenTransactionFailsForAnotherReason_PropagatesFailure()
    {
        // Arrange
        Mock<IDynamoDBContext> context = CreateContextMock();
        Mock<IAmazonDynamoDB> client = new();
        ProvisionedThroughputExceededException expectedException =
            new("DynamoDB capacity is unavailable");
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);
        GuestSessionRepository repository = new(context.Object, client.Object);

        // Act
        ProvisionedThroughputExceededException actualException =
            await Assert.ThrowsAsync<ProvisionedThroughputExceededException>(
                () => repository.TrySaveOwnerSessionAsync(CreateOwnerSession()));

        // Assert
        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task TrySaveOwnerSessionAsync_WhenTransactionIsCancelledWithoutKeyConditionConflict_PropagatesCancellation()
    {
        // Arrange
        Mock<IDynamoDBContext> context = CreateContextMock();
        Mock<IAmazonDynamoDB> client = new();
        TransactionCanceledException expectedException = new("transaction cancelled for another reason")
        {
            CancellationReasons =
            [
                new CancellationReason { Code = "None" },
                new CancellationReason { Code = "TransactionConflict" }
            ]
        };
        client
            .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                It.IsAny<TransactWriteItemsRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);
        GuestSessionRepository repository = new(context.Object, client.Object);

        // Act
        TransactionCanceledException actualException = await Assert.ThrowsAsync<TransactionCanceledException>(
            () => repository.TrySaveOwnerSessionAsync(CreateOwnerSession()));

        // Assert
        Assert.Same(expectedException, actualException);
    }

    [Fact]
    public async Task GetByGuestSessionIdConsistentAsync_LoadsWithStrongConsistency()
    {
        // Arrange
        Mock<IDynamoDBContext> context = CreateContextMock();
        GuestSession expectedSession = CreateOwnerSession();
        LoadConfig? capturedConfig = null;
        context
            .Setup(dynamoContext => dynamoContext.LoadAsync<GuestSession?>(
                "owner-session-1",
                It.IsAny<LoadConfig>(),
                It.IsAny<CancellationToken>()))
            .Callback<object, LoadConfig, CancellationToken>((_, config, _) => capturedConfig = config)
            .ReturnsAsync(expectedSession);
        GuestSessionRepository repository = new(context.Object, Mock.Of<IAmazonDynamoDB>());

        // Act
        GuestSession? actualSession = await repository.GetByGuestSessionIdConsistentAsync("owner-session-1");

        // Assert
        Assert.Same(expectedSession, actualSession);
        Assert.NotNull(capturedConfig);
        Assert.True(capturedConfig.ConsistentRead);
        Assert.Equal("guest-sessions-test", capturedConfig.OverrideTableName);
    }

    [Fact]
    public async Task GetOwnerSessionsByGroupReservationIdAsync_QueriesGroupIndexAndReturnsOnlyOwnerSessions()
    {
        // Arrange
        Mock<IDynamoDBContext> context = CreateContextMock();
        GuestSession expectedOwnerSession = CreateOwnerSession();
        GuestSession guestSession = CreateSession();
        Mock<AsyncSearch<GuestSession>> search = new();
        search
            .Setup(asyncSearch => asyncSearch.GetRemainingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([guestSession, expectedOwnerSession]);
        QueryConfig? capturedConfig = null;
        object? capturedGroupReservationId = null;
        context
            .Setup(dynamoContext => dynamoContext.QueryAsync<GuestSession>(
                It.IsAny<object>(),
                It.IsAny<QueryConfig>()))
            .Callback<object, QueryConfig>((groupReservationId, config) =>
            {
                capturedGroupReservationId = groupReservationId;
                capturedConfig = config;
            })
            .Returns(search.Object);
        GuestSessionRepository repository = new(context.Object, Mock.Of<IAmazonDynamoDB>());

        // Act
        List<GuestSession> result =
            await repository.GetOwnerSessionsByGroupReservationIdAsync("group-1");

        // Assert
        Assert.Same(expectedOwnerSession, Assert.Single(result));
        Assert.NotNull(capturedConfig);
        Assert.Equal("guest-sessions-test", capturedConfig.OverrideTableName);
        Assert.Equal(GlobalSecondaryIndexes.GuestSession.GroupReservationId, capturedConfig.IndexName);
        Assert.Equal("group-1", capturedGroupReservationId);
        context.Verify(
            dynamoContext => dynamoContext.ScanAsync<GuestSession>(
                It.IsAny<IEnumerable<ScanCondition>>(),
                It.IsAny<ScanConfig>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 2, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(3, 2, 3)]
    public async Task CountByGroupReservationId_StoredOwnerRows_AreExcludedFromGuestCount(
        int guestRows,
        int ownerRows,
        int expectedCount)
    {
        // Arrange
        Mock<IDynamoDBContext> context = CreateContextMock();
        List<GuestSession> stored = [];
        for (int i = 0; i < guestRows; i++)
            stored.Add(CreateSession());
        for (int i = 0; i < ownerRows; i++)
            stored.Add(CreateOwnerSession());
        Mock<AsyncSearch<GuestSession>> search = new();
        search
            .Setup(asyncSearch => asyncSearch.GetRemainingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);
        QueryConfig? capturedConfig = null;
        context
            .Setup(dynamoContext => dynamoContext.QueryAsync<GuestSession>(
                It.IsAny<object>(),
                It.IsAny<QueryConfig>()))
            .Callback<object, QueryConfig>((_, config) => capturedConfig = config)
            .Returns(search.Object);
        GuestSessionRepository repository = new(context.Object, Mock.Of<IAmazonDynamoDB>());

        // Act
        int count = await repository.CountByGroupReservationId("group-1");

        // Assert
        Assert.Equal(expectedCount, count);
        Assert.NotNull(capturedConfig);
        Assert.Equal(GlobalSecondaryIndexes.GuestSession.GroupReservationId, capturedConfig.IndexName);
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

    private static GuestSession CreateOwnerSession() => new()
    {
        Id = "owner-session-1",
        GroupReservationId = "group-1",
        GuestName = "Owner",
        IsOwnerSession = true,
        OwnerUserId = "owner-1",
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
