namespace APIHandler.Exceptions;

public class GroupReservationSubmitConflictException(string message = "Group reservation can no longer be submitted.")
    : System.Exception(message);
