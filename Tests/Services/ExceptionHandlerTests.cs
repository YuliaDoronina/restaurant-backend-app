using Amazon.Lambda.Core;
using Function.Services;
using Moq;
using System;

namespace APIHandler.Tests.Services;

public class ExceptionHandlerTests
{
    private readonly Mock<ILambdaContext> _lambdaContext = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public ExceptionHandlerTests()
    {
        _lambdaContext.SetupGet(context => context.Logger).Returns(_logger.Object);
    }

    [Fact]
    public void Handle_UnexpectedException_ReturnsInternalServerErrorAndLogsException()
    {
        // Arrange
        InvalidOperationException exception = new("Unexpected service error.");

        // Act
        var response = ExceptionHandler.Handle(exception, _lambdaContext.Object);

        // Assert
        Assert.Equal(500, response.StatusCode);
        Assert.Equal("""{"error":"Internal Server Error"}""", response.Body);
        _logger.Verify(
            logger => logger.LogError($"An error occurred while processing the request: {exception}"),
            Times.Once);
    }
}
