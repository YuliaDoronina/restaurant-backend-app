using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using APIHandler.Constants;
using APIHandler.Entities;
using APIHandler.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GuestSessions;

public class GuestSessionRepository(IDynamoDBContext context, IAmazonDynamoDB dynamoDbClient) : IGuestSessionRepository
{
    private const string StatusAttribute = "status";

    public async Task<GuestSession?> GetByGuestSessionId(string id) => await context.LoadAsync<GuestSession?>(id, new LoadConfig()
    {
        OverrideTableName = ResourceNames.GuestSessionTableName
    });

    public async Task<GuestSession?> GetByGuestSessionIdConsistentAsync(string id) =>
        await context.LoadAsync<GuestSession?>(id, new LoadConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            ConsistentRead = true
        });

    public async Task<GuestSession?> GetByGroupReservationId(string groupReservationId)
    {
        List<GuestSession> result = await context.QueryAsync<GuestSession>(groupReservationId, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.GroupReservationId
        }).GetRemainingAsync();

        return result.FirstOrDefault();
    }

    public async Task<GuestSession?> GetByTokenHash(string tokenHash)
    {
        List<GuestSession> result = await context.QueryAsync<GuestSession>(tokenHash, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.SessionTokenHash
        }).GetRemainingAsync();

        return result.FirstOrDefault();
    }

    public async Task<List<GuestSession>> GetAllByGroupReservationIdAsync(string groupReservationId)
    {
        return await context.QueryAsync<GuestSession>(groupReservationId, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.GroupReservationId
        }).GetRemainingAsync();
    }

    public async Task<List<GuestSession>> GetAllByGroupReservationIdConsistentAsync(string groupReservationId)
    {
        List<ScanCondition> conditions =
        [
            new ScanCondition(nameof(GuestSession.GroupReservationId), ScanOperator.Equal, groupReservationId)
        ];

        ScanConfig scanConfig = DynamoConfig.ScanConfig(ResourceNames.GuestSessionTableName);
        scanConfig.ConsistentRead = true;
        return await context.ScanAsync<GuestSession>(conditions, scanConfig).GetRemainingAsync();
    }

    public async Task<List<GuestSession>> GetOwnerSessionsByGroupReservationIdAsync(string groupReservationId)
    {
        List<GuestSession> sessions = await GetAllByGroupReservationIdAsync(groupReservationId);
        return sessions.Where(session => session.IsOwnerSession).ToList();
    }

    public async Task SaveAsync(GuestSession guestSession) => await context.SaveAsync(guestSession, new SaveConfig
    {
        OverrideTableName = ResourceNames.GuestSessionTableName
    });

    public async Task SaveCartAsync(GuestSession guestSession)
    {
        ArgumentNullException.ThrowIfNull(guestSession);

        TransactWriteItem saveSession = new()
        {
            Put = new Put
            {
                TableName = ResourceNames.GuestSessionTableName,
                Item = context.ToDocument(guestSession).ToAttributeMap()
            }
        };
        await ExecuteCartMutationAsync(guestSession.GroupReservationId, saveSession);
    }

    public async Task<bool> TrySaveOwnerSessionAsync(GuestSession guestSession)
    {
        ArgumentNullException.ThrowIfNull(guestSession);

        TransactWriteItem saveSession = new()
        {
            Put = new Put
            {
                TableName = ResourceNames.GuestSessionTableName,
                Item = context.ToDocument(guestSession).ToAttributeMap(),
                ConditionExpression = "attribute_not_exists(guestSessionId)"
            }
        };

        try
        {
            await dynamoDbClient.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    CreateOpenGroupConditionCheck(guestSession.GroupReservationId),
                    saveSession
                ]
            });
            return true;
        }
        catch (TransactionCanceledException exception) when (IsGroupStatusConditionFailure(exception))
        {
            await ThrowGroupStatusConflictAsync(guestSession.GroupReservationId);
            throw;
        }
        catch (TransactionCanceledException exception) when (IsOwnerSessionKeyConflict(exception))
        {
            return false;
        }
    }

    public async Task<int> CountByGroupReservationId(string groupReservationId)
    {
        List<GuestSession> result = await context.QueryAsync<GuestSession>(groupReservationId, new QueryConfig
        {
            OverrideTableName = ResourceNames.GuestSessionTableName,
            IndexName = GlobalSecondaryIndexes.GuestSession.GroupReservationId
        }).GetRemainingAsync();

        // Only guest sessions are counted; the Owner is added once by callers, regardless of stored Owner rows.
        return result.Count(session => !session.IsOwnerSession);
    }

    public async Task DeleteAsync(GuestSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        TransactWriteItem deleteSession = new()
        {
            Delete = new Delete
            {
                TableName = ResourceNames.GuestSessionTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["guestSessionId"] = new() { S = session.Id }
                }
            }
        };
        await ExecuteCartMutationAsync(session.GroupReservationId, deleteSession);
    }

    private async Task ExecuteCartMutationAsync(string groupReservationId, TransactWriteItem mutation)
    {
        try
        {
            await dynamoDbClient.TransactWriteItemsAsync(new TransactWriteItemsRequest
            {
                TransactItems =
                [
                    CreateOpenGroupConditionCheck(groupReservationId),
                    mutation
                ]
            });
        }
        catch (TransactionCanceledException exception) when (IsGroupStatusConditionFailure(exception))
        {
            await ThrowGroupStatusConflictAsync(groupReservationId);
        }
    }

    private TransactWriteItem CreateOpenGroupConditionCheck(string groupReservationId)
    {
        GroupReservation openGroup = new()
        {
            GroupReservationId = groupReservationId,
            ReservationId = string.Empty,
            ReservationToken = string.Empty,
            Status = GroupReservationStatus.Open
        };
        AttributeValue openStatus = context.ToDocument(openGroup).ToAttributeMap()[StatusAttribute];

        return new TransactWriteItem
        {
            ConditionCheck = new ConditionCheck
            {
                TableName = ResourceNames.GroupReservationsTableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["groupReservationId"] = new() { S = groupReservationId }
                },
                ConditionExpression = "#status = :openStatus",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#status"] = StatusAttribute
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":openStatus"] = openStatus
                }
            }
        };
    }

    private static bool IsGroupStatusConditionFailure(TransactionCanceledException exception)
    {
        return exception.CancellationReasons?.FirstOrDefault()?.Code == "ConditionalCheckFailed";
    }

    private static bool IsOwnerSessionKeyConflict(TransactionCanceledException exception)
    {
        return exception.CancellationReasons?.Count > 1 &&
               exception.CancellationReasons[1].Code == "ConditionalCheckFailed";
    }

    private async Task ThrowGroupStatusConflictAsync(string groupReservationId)
    {
        GroupReservation? group = await context.LoadAsync<GroupReservation>(
            groupReservationId,
            DynamoConfig.LoadConfig(ResourceNames.GroupReservationsTableName));

        if (group?.Status == GroupReservationStatus.Closed)
            throw new GroupReservationExpiredException();

        throw new GroupReservationSubmittedException();
    }
}
