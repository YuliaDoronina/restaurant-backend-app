using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GroupReservations;

public class GroupReservationSubmitRepository : IGroupReservationSubmitRepository
{
    private const long SubmitLeaseDurationSeconds = 120;
    private const string StatusAttribute = "status";
    private const string SubmitAttemptIdAttribute = "submitAttemptId";
    private const string SubmitLeaseExpiresAtAttribute = "submitLeaseExpiresAt";
    private readonly IDynamoDBContext _dynamoDbContext;
    private readonly IAmazonDynamoDB _dynamoDbClient;

    public GroupReservationSubmitRepository(
        IDynamoDBContext dynamoDbContext,
        IAmazonDynamoDB dynamoDbClient)
    {
        _dynamoDbContext = dynamoDbContext ?? throw new ArgumentNullException(nameof(dynamoDbContext));
        _dynamoDbClient = dynamoDbClient ?? throw new ArgumentNullException(nameof(dynamoDbClient));
    }

    public async Task BeginSubmitAsync(GroupReservation groupReservation, string attemptId)
    {
        ArgumentNullException.ThrowIfNull(groupReservation);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);

        long leaseExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + SubmitLeaseDurationSeconds;
        GroupReservation submittingGroupReservation = new()
        {
            GroupReservationId = groupReservation.GroupReservationId,
            ReservationId = groupReservation.ReservationId,
            ReservationToken = groupReservation.ReservationToken,
            Status = GroupReservationStatus.Submitting,
            SubmitAttemptId = attemptId,
            SubmitLeaseExpiresAt = leaseExpiresAt
        };
        var submittingGroupItem = _dynamoDbContext.ToDocument(submittingGroupReservation).ToAttributeMap();

        try
        {
            await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = ResourceNames.GroupReservationsTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["groupReservationId"] = new() { S = groupReservation.GroupReservationId }
                },
                UpdateExpression = "SET #status = :submittingStatus, #attemptId = :attemptId, #leaseExpiresAt = :leaseExpiresAt",
                ConditionExpression = "#status = :openStatus OR (#status = :submittingStatus AND (attribute_not_exists(#leaseExpiresAt) OR #leaseExpiresAt <= :now))",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#status"] = StatusAttribute,
                    ["#attemptId"] = SubmitAttemptIdAttribute,
                    ["#leaseExpiresAt"] = SubmitLeaseExpiresAtAttribute
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":openStatus"] = new() { N = ((int)GroupReservationStatus.Open).ToString() },
                    [":submittingStatus"] = submittingGroupItem[StatusAttribute],
                    [":attemptId"] = new() { S = attemptId },
                    [":leaseExpiresAt"] = new() { N = leaseExpiresAt.ToString() },
                    [":now"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
                }
            });

            groupReservation.Status = GroupReservationStatus.Submitting;
            groupReservation.SubmitAttemptId = attemptId;
            groupReservation.SubmitLeaseExpiresAt = leaseExpiresAt;
        }
        catch (ConditionalCheckFailedException)
        {
            throw new GroupReservationSubmitConflictException();
        }
    }

    public async Task SubmitAsync(Order order, GroupReservation groupReservation, string attemptId)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(groupReservation);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);

        GroupReservation submittingGroupReservation = new()
        {
            GroupReservationId = groupReservation.GroupReservationId,
            ReservationId = groupReservation.ReservationId,
            ReservationToken = groupReservation.ReservationToken,
            Status = GroupReservationStatus.Submitting,
            SubmitAttemptId = attemptId,
            SubmitLeaseExpiresAt = groupReservation.SubmitLeaseExpiresAt
        };
        var submittingGroupItem = _dynamoDbContext.ToDocument(submittingGroupReservation).ToAttributeMap();
        var submittedGroupItem = _dynamoDbContext.ToDocument(groupReservation).ToAttributeMap();

        var transactionRequest = new TransactWriteItemsRequest
        {
            TransactItems =
            [
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = ResourceNames.OrdersTableName,
                        Item = _dynamoDbContext.ToDocument(order).ToAttributeMap()
                    }
                },
                new TransactWriteItem
                {
                    Put = new Put
                    {
                        TableName = ResourceNames.GroupReservationsTableName,
                        Item = submittedGroupItem,
                        ConditionExpression = "#status = :submittingStatus AND #attemptId = :attemptId AND #leaseExpiresAt > :now",
                        ExpressionAttributeNames = new Dictionary<string, string>
                        {
                            ["#status"] = StatusAttribute,
                            ["#attemptId"] = SubmitAttemptIdAttribute,
                            ["#leaseExpiresAt"] = SubmitLeaseExpiresAtAttribute
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":submittingStatus"] = submittingGroupItem[StatusAttribute],
                            [":attemptId"] = new() { S = attemptId },
                            [":now"] = new() { N = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
                        }
                    }
                }
            ]
        };

        try
        {
            await _dynamoDbClient.TransactWriteItemsAsync(transactionRequest);
        }
        catch (TransactionCanceledException exception) when (IsStatusConditionFailure(exception))
        {
            throw new GroupReservationSubmitConflictException();
        }
    }

    public async Task ReleaseSubmitAsync(string groupReservationId, string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupReservationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);

        GroupReservation submittingGroupReservation = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = string.Empty,
            ReservationToken = string.Empty,
            Status = GroupReservationStatus.Submitting,
            SubmitAttemptId = attemptId
        };
        var submittingGroupItem = _dynamoDbContext.ToDocument(submittingGroupReservation).ToAttributeMap();

        await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = ResourceNames.GroupReservationsTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["groupReservationId"] = new() { S = groupReservationId }
            },
            UpdateExpression = "SET #status = :openStatus REMOVE #attemptId, #leaseExpiresAt",
            ConditionExpression = "#status = :submittingStatus AND #attemptId = :attemptId",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = StatusAttribute,
                ["#attemptId"] = SubmitAttemptIdAttribute,
                ["#leaseExpiresAt"] = SubmitLeaseExpiresAtAttribute
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":openStatus"] = new() { N = ((int)GroupReservationStatus.Open).ToString() },
                [":submittingStatus"] = submittingGroupItem[StatusAttribute],
                [":attemptId"] = new() { S = attemptId }
            }
        });
    }

    private static bool IsStatusConditionFailure(TransactionCanceledException exception)
    {
        return exception.CancellationReasons?.Any(reason =>
            string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) == true;
    }
}
