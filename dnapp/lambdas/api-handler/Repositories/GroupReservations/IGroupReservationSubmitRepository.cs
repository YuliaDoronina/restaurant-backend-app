using APIHandler.Entities;
using System.Threading.Tasks;

namespace APIHandler.Repositories.GroupReservations;

public interface IGroupReservationSubmitRepository
{
    Task BeginSubmitAsync(GroupReservation groupReservation, string attemptId);

    Task SubmitAsync(Order order, GroupReservation groupReservation, string attemptId);

    Task ReleaseSubmitAsync(string groupReservationId, string attemptId);
}
