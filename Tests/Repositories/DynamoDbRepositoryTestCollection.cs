namespace APIHandler.Tests.Repositories;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DynamoDbRepositoryTestCollection
{
    public const string Name = "DynamoDB repository tests";
}
