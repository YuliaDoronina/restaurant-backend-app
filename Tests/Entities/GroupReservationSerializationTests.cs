using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.Runtime;
using APIHandler.Entities;

namespace APIHandler.Tests.Entities;

public class GroupReservationSerializationTests
{
    [Fact]
    public void ToDocument_OpenStatus_SerializesAsNumericZero()
    {
        // Arrange
        using AmazonDynamoDBClient dynamoDbClient = new(
            new BasicAWSCredentials("test-access-key", "test-secret-key"),
            RegionEndpoint.USEast1);
        IDynamoDBContext context = new DynamoDBContextBuilder()
            .WithDynamoDBClient(() => dynamoDbClient)
            .Build();
        GroupReservation groupReservation = new()
        {
            GroupReservationId = "group-1",
            ReservationId = "reservation-1",
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        // Act
        var attributes = context.ToDocument(groupReservation).ToAttributeMap();

        // Assert
        Assert.Equal("0", attributes["status"].N);
        Assert.Null(attributes["status"].S);
    }
}
