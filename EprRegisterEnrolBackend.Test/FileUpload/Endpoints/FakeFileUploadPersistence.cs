using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.FileUpload.Models;
using EprRegisterEnrolBackend.FileUpload.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.FileUpload.Endpoints;

public class FakeFileUploadPersistence : IFileUploadPersistence
{
    private readonly List<FileUploadModel> _store = [];

    public void Seed(FileUploadModel fileUpload) => _store.Add(fileUpload);
    public void Clear() => _store.Clear();

    public Task<FileUploadModel?> CreateAsync(FileUploadModel fileUpload)
    {
        if (fileUpload.Id is null || fileUpload.Id == ObjectId.Empty)
            fileUpload.Id = ObjectId.GenerateNewId();
        _store.Add(fileUpload);
        return Task.FromResult<FileUploadModel?>(fileUpload);
    }

    public Task<IEnumerable<FileUploadModel>> GetByOrganisationAsync(string organisationId) =>
        Task.FromResult<IEnumerable<FileUploadModel>>(
            _store.Where(f => f.OrganisationId == organisationId).ToList());

    public Task<FileUploadModel?> GetByIdAsync(string fileUploadId)
    {
        if (!ObjectId.TryParse(fileUploadId, out var oid))
            return Task.FromResult<FileUploadModel?>(null);
        var found = _store.FirstOrDefault(f => f.Id == oid);
        return Task.FromResult(found);
    }

    public Task<IEnumerable<FileUploadModel>> GetByOrganisationMaterialAndYearAsync(
        string organisationId, MaterialType material, int year) =>
        Task.FromResult<IEnumerable<FileUploadModel>>(
            _store.Where(f =>
                f.OrganisationId == organisationId &&
                f.Material == material &&
                f.YearOfAccreditation == year).ToList());
}
