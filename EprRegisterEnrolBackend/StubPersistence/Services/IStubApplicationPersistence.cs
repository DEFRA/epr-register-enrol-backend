using EprRegisterEnrolBackend.StubPersistence.Models;

namespace EprRegisterEnrolBackend.StubPersistence.Services;

public interface IStubApplicationPersistence
{
    Task<IEnumerable<StubApplicationDocument>> GetByOrgAsync(string organisationId);
    Task<StubApplicationDocument?> GetByIdAsync(string organisationId, string stubApplicationId);
    Task UpsertAsync(StubApplicationDocument document);
}
