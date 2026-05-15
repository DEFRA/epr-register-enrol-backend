using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.FileUpload.Models;

namespace EprRegisterEnrolBackend.FileUpload.Services;

public interface IFileUploadPersistence
{
    Task<FileUploadModel?> CreateAsync(FileUploadModel fileUpload);
    Task<IEnumerable<FileUploadModel>> GetByOrganisationAsync(string organisationId);
    Task<FileUploadModel?> GetByIdAsync(string fileUploadId);
    Task<IEnumerable<FileUploadModel>> GetByOrganisationMaterialAndYearAsync(
        string organisationId, MaterialType material, int year);
}
