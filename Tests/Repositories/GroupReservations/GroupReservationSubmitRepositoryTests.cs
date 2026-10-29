using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Repositories.GroupReservations;
using Moq;

namespace APIHandler.Tests.Repositories.GroupReservations;

[Collection(DynamoDbRepositoryTestCollection.Name)]
public class GroupReservationSubmitRepositoryTests
{
    [Fact]
    public async Task BeginSubmitAsync_OpenOrExpiredSubmittingLease_UsesConditionalAttemptAndLeaseUpdate()
    {
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);
            UpdateItemRequest? capturedRequest = null;
            client
                .Setup(dynamoClient => dynamoClient.UpdateItemAsync(
                    It.IsAny<UpdateItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((request, _) => capturedRequest = request)
                .ReturnsAsync(new UpdateItemResponse());

            GroupReservationSubmitRepository repository = new(context.Object, client.Object);
            GroupReservation group = CreateGroupReservation(GroupReservationStatus.Submitting);
            const string attemptId = "attempt-new";

            await repository.BeginSubmitAsync(group, attemptId);

            Assert.NotNull(capturedRequest);
            Assert.Equal("groups-test", capturedRequest.TableName);
            Assert.Contains("OR", capturedRequest.ConditionExpression, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("attribute_not_exists", capturedRequest.ConditionExpression, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<=", capturedRequest.ConditionExpression);
            Assert.Contains("SET", capturedRequest.UpdateExpression, StringComparison.OrdinalIgnoreCase);

            string statusAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "status");
            string attemptAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "submitAttemptId");
            string leaseAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "submitLeaseExpiresAt");
            Assert.Contains(statusAlias, capturedRequest.ConditionExpression);
            Assert.Contains(leaseAlias, capturedRequest.ConditionExpression);
            Assert.Contains(attemptAlias, capturedRequest.UpdateExpression);
            Assert.Contains(leaseAlias, capturedRequest.UpdateExpression);
            Assert.Contains(
                capturedRequest.ExpressionAttributeValues.Values,
                value => value.S == attemptId);
            Assert.Contains(
                capturedRequest.ExpressionAttributeValues.Values,
                value => value.N == ((int)GroupReservationStatus.Open).ToString());
            Assert.Contains(
                capturedRequest.ExpressionAttributeValues.Values,
                value => value.N == ((int)GroupReservationStatus.Submitting).ToString());
            long currentEpochSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Assert.Contains(
                capturedRequest.ExpressionAttributeValues.Values,
                value => long.TryParse(value.N, out long expiry) && expiry > currentEpochSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    [Fact]
    public async Task BeginSubmitAsync_LeaseClaimRaceLosesConditionalCheck_ThrowsSubmitConflict()
    {
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);
            client
                .Setup(dynamoClient => dynamoClient.UpdateItemAsync(
                    It.IsAny<UpdateItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ConditionalCheckFailedException("another attempt owns the active lease"));
            GroupReservationSubmitRepository repository = new(context.Object, client.Object);

            await Assert.ThrowsAsync<APIHandler.Exceptions.GroupReservationSubmitConflictException>(
                () => repository.BeginSubmitAsync(CreateGroupReservation(GroupReservationStatus.Submitting), "attempt-new"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    [Fact]
    public async Task SubmitAsync_UsesAttemptAndUnexpiredLeaseAsTransactionFence()
    {
        string? originalOrdersTable = Environment.GetEnvironmentVariable("ordersTable");
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("ordersTable", "orders-test");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<Order>()))
                .Returns(new Document { ["orderId"] = "order-1" });
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);

            TransactWriteItemsRequest? capturedRequest = null;
            client
                .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                    It.IsAny<TransactWriteItemsRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<TransactWriteItemsRequest, CancellationToken>((request, _) => capturedRequest = request)
                .ReturnsAsync(new TransactWriteItemsResponse());

            GroupReservationSubmitRepository repository = new(context.Object, client.Object);
            GroupReservation group = CreateGroupReservation(GroupReservationStatus.Submitted);
            group.SubmitAttemptId = "attempt-current";
            group.SubmitLeaseExpiresAt = 1_900_000_000;

            await repository.SubmitAsync(CreateOrder(), group, "attempt-current");

            Assert.NotNull(capturedRequest);
            Assert.Equal(2, capturedRequest.TransactItems.Count);
            Assert.Equal("orders-test", capturedRequest.TransactItems[0].Put.TableName);
            Put groupPut = Assert.IsType<Put>(capturedRequest.TransactItems[1].Put);
            Assert.Equal("groups-test", groupPut.TableName);
            string statusAlias = FindAlias(groupPut.ExpressionAttributeNames, "status");
            string attemptAlias = FindAlias(groupPut.ExpressionAttributeNames, "submitAttemptId");
            string leaseAlias = FindAlias(groupPut.ExpressionAttributeNames, "submitLeaseExpiresAt");
            Assert.Contains(statusAlias, groupPut.ConditionExpression);
            Assert.Contains(attemptAlias, groupPut.ConditionExpression);
            Assert.Contains(leaseAlias, groupPut.ConditionExpression);
            Assert.Contains(">", groupPut.ConditionExpression);
            Assert.Contains(
                groupPut.ExpressionAttributeValues.Values,
                value => value.S == "attempt-current");
            Assert.Contains(
                groupPut.ExpressionAttributeValues.Values,
                value => value.N == ((int)GroupReservationStatus.Submitting).ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ordersTable", originalOrdersTable);
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    [Fact]
    public async Task SubmitAsync_OldAttemptCannotSubmitAfterLeaseWasReclaimed()
    {
        string? originalOrdersTable = Environment.GetEnvironmentVariable("ordersTable");
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("ordersTable", "orders-test");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<Order>()))
                .Returns(new Document());
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);
            TransactWriteItemsRequest? capturedRequest = null;
            client
                .Setup(dynamoClient => dynamoClient.TransactWriteItemsAsync(
                    It.IsAny<TransactWriteItemsRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<TransactWriteItemsRequest, CancellationToken>((request, _) => capturedRequest = request)
                .ThrowsAsync(new TransactionCanceledException("attempt token no longer matches")
                {
                    CancellationReasons =
                    [
                        new CancellationReason { Code = "None" },
                        new CancellationReason { Code = "ConditionalCheckFailed" }
                    ]
                });
            GroupReservationSubmitRepository repository = new(context.Object, client.Object);
            GroupReservation oldAttemptGroup = CreateGroupReservation(GroupReservationStatus.Submitted);
            oldAttemptGroup.SubmitAttemptId = "attempt-old";
            oldAttemptGroup.SubmitLeaseExpiresAt = 1_900_000_000;

            await Assert.ThrowsAsync<APIHandler.Exceptions.GroupReservationSubmitConflictException>(
                () => repository.SubmitAsync(CreateOrder(), oldAttemptGroup, "attempt-old"));

            Assert.NotNull(capturedRequest);
            Put groupPut = Assert.IsType<Put>(capturedRequest.TransactItems[1].Put);
            Assert.Contains(
                groupPut.ExpressionAttributeValues.Values,
                value => value.S == "attempt-old");
            Assert.Contains(
                groupPut.ExpressionAttributeValues.Values,
                value => value.N == ((int)GroupReservationStatus.Submitting).ToString());
            Assert.Contains(">", groupPut.ConditionExpression);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ordersTable", originalOrdersTable);
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    [Fact]
    public async Task ReleaseSubmitAsync_OnlyReopensGroupOwnedByMatchingAttemptAndRemovesLeaseFields()
    {
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);
            UpdateItemRequest? capturedRequest = null;
            client
                .Setup(dynamoClient => dynamoClient.UpdateItemAsync(
                    It.IsAny<UpdateItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((request, _) => capturedRequest = request)
                .ReturnsAsync(new UpdateItemResponse());
            GroupReservationSubmitRepository repository = new(context.Object, client.Object);

            await repository.ReleaseSubmitAsync("group-1", "attempt-current");

            Assert.NotNull(capturedRequest);
            string statusAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "status");
            string attemptAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "submitAttemptId");
            string leaseAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "submitLeaseExpiresAt");
            Assert.Contains(statusAlias, capturedRequest.ConditionExpression);
            Assert.Contains(attemptAlias, capturedRequest.ConditionExpression);
            Assert.Contains("SET", capturedRequest.UpdateExpression, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("REMOVE", capturedRequest.UpdateExpression, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(attemptAlias, capturedRequest.UpdateExpression);
            Assert.Contains(leaseAlias, capturedRequest.UpdateExpression);
            Assert.Contains(
                capturedRequest.ExpressionAttributeValues.Values,
                value => value.S == "attempt-current");
        }
        finally
        {
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    [Fact]
    public async Task ReleaseSubmitAsync_OldAttemptCannotReleaseReclaimedLeaseAndPropagatesConditionalFailure()
    {
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);
            UpdateItemRequest? capturedRequest = null;
            client
                .Setup(dynamoClient => dynamoClient.UpdateItemAsync(
                    It.IsAny<UpdateItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .Callback<UpdateItemRequest, CancellationToken>((request, _) => capturedRequest = request)
                .ThrowsAsync(new ConditionalCheckFailedException("attempt token no longer matches"));
            GroupReservationSubmitRepository repository = new(context.Object, client.Object);

            await Assert.ThrowsAsync<ConditionalCheckFailedException>(
                () => repository.ReleaseSubmitAsync("group-1", "attempt-old"));

            Assert.NotNull(capturedRequest);
            string attemptAlias = FindAlias(capturedRequest.ExpressionAttributeNames, "submitAttemptId");
            Assert.Contains(attemptAlias, capturedRequest.ConditionExpression);
            Assert.Contains(
                capturedRequest.ExpressionAttributeValues.Values,
                value => value.S == "attempt-old");
        }
        finally
        {
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    [Fact]
    public async Task ReleaseSubmitAsync_NonConditionalFailureIsPropagated()
    {
        string? originalGroupsTable = Environment.GetEnvironmentVariable("groupReservationsTable");
        Environment.SetEnvironmentVariable("groupReservationsTable", "groups-test");

        try
        {
            Mock<IDynamoDBContext> context = new();
            Mock<IAmazonDynamoDB> client = new();
            context
                .Setup(dynamoContext => dynamoContext.ToDocument(It.IsAny<GroupReservation>()))
                .Returns<GroupReservation>(CreateGroupDocument);
            ProvisionedThroughputExceededException failure = new("throttled");
            client
                .Setup(dynamoClient => dynamoClient.UpdateItemAsync(
                    It.IsAny<UpdateItemRequest>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);
            GroupReservationSubmitRepository repository = new(context.Object, client.Object);

            ProvisionedThroughputExceededException exception = await Assert.ThrowsAsync<ProvisionedThroughputExceededException>(
                () => repository.ReleaseSubmitAsync("group-1", "attempt-current"));

            Assert.Same(failure, exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable("groupReservationsTable", originalGroupsTable);
        }
    }

    private static Document CreateGroupDocument(GroupReservation group) => new()
    {
        ["status"] = (int)group.Status,
        ["submitAttemptId"] = group.SubmitAttemptId,
        ["submitLeaseExpiresAt"] = group.SubmitLeaseExpiresAt
    };

    private static string FindAlias(IReadOnlyDictionary<string, string> expressionNames, string attributeName) =>
        Assert.Single(expressionNames, pair => pair.Value == attributeName).Key;

    private static Order CreateOrder() => new()
    {
        OrderId = "order-1",
        CustomerId = "owner-1",
        ReservationId = "reservation-1",
        ReservationAddress = "address",
        ReservationTime = "time",
        Dishes = []
    };

    private static GroupReservation CreateGroupReservation(GroupReservationStatus status) => new()
    {
        GroupReservationId = "group-1",
        ReservationId = "reservation-1",
        ReservationToken = "token-1",
        Status = status
    };
}
