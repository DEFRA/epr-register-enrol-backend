using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>The system an entity's data was copied from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceSystem
{
    /// <summary>Built from an application in this service (the migration and dual-write).</summary>
    Application,

    /// <summary>Imported or refreshed from ReEx.</summary>
    ReEx,
}
