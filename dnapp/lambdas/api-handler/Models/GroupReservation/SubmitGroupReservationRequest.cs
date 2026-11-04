using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace APIHandler.Models.GroupReservation;

public class SubmitGroupReservationRequest
{
    [Required]
    [JsonPropertyName("reservationId")]
    public string ReservationId { get; set; } = string.Empty;
}
