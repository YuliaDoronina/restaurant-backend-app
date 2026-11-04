using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GuestSessions;

public record GroupSessionsResponse(
    [property: JsonPropertyName("participants")] IEnumerable<GroupSessionParticipantResponse> Participants,
    [property: JsonPropertyName("total")] decimal Total);

public record GroupSessionParticipantResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type"), JsonConverter(typeof(JsonStringEnumConverter<ParticipantType>))] ParticipantType Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] IEnumerable<GroupSessionItemResponse> Items,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("isSubmitted")] bool IsSubmitted);

public record GroupSessionItemResponse(
    [property: JsonPropertyName("dish")] GroupSessionDishResponse Dish,
    [property: JsonPropertyName("quantity")] int Quantity);

public record GroupSessionDishResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] decimal Price);
