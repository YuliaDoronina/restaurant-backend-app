using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public class GroupReservationOrderItemResponse
{
    [JsonPropertyName("dishId")]
    public required string DishId { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}
