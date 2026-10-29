using System.Text.Json.Serialization;

namespace APIHandler.Models.Reservations;

public class ReservationGuestSessionIdResponse
{
    [JsonPropertyName("guestSessionId")]
    public required string GuestSessionId { get; set; }
}
