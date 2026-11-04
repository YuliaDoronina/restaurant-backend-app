namespace APIHandler.Exceptions;

public class DuplicateOwnerSessionException(string groupReservationId)
    : System.Exception($"Multiple Owner sessions exist for group reservation '{groupReservationId}'.");
