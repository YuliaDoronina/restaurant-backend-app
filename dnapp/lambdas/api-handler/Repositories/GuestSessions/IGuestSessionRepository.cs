using APIHandler.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GuestSessions;

public interface IGuestSessionRepository
{
    Task<int> CountByGroupReservationId(string groupReservationId);

    Task<GuestSession?> GetByGroupReservationId(string groupReservationId);

    Task<List<GuestSession>> GetOwnerSessionsByGroupReservationIdAsync(string groupReservationId);

    Task<GuestSession?> GetByGuestSessionId(string id);

    Task<GuestSession?> GetByGuestSessionIdConsistentAsync(string id);

    Task<GuestSession?> GetByTokenHash(string tokenHash);

    Task<List<GuestSession>> GetAllByGroupReservationIdAsync(string groupReservationId);

    Task<List<GuestSession>> GetAllByGroupReservationIdConsistentAsync(string groupReservationId);

    Task SaveAsync(GuestSession guestSession);

    Task SaveCartAsync(GuestSession guestSession);

    Task<bool> TrySaveOwnerSessionAsync(GuestSession guestSession);

    Task DeleteAsync(GuestSession session);
}
