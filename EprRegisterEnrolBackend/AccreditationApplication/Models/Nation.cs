using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-448: GenerateOrUpdateRegulatoryNumberRequest.Nation stays caller-supplied (the caller,
// e.g. management-be, is the one who reliably knows which nation an application belongs to
// for that endpoint's wire contract). Values match NATIONS in epr-register-enrol-frontend's
// src/server/common/helpers/nation-from-postcode.js exactly, so the wire contract lines up
// with the rest of this ecosystem.
//
// RA-526: that "no reliable internal source" premise no longer holds universally - at Seed
// time, AccreditationApplicationModel.Nation IS derived internally from the source
// registration's own SubmittedToRegulator (see RegulatorNationMapper and
// HttpReExApiAdapter.GetAccreditationAsync), replacing what used to be postcode-derived
// nation lookup downstream. This backend-derived Nation is distinct from, and does not (yet)
// flow into, the still-caller-supplied Nation on GenerateOrUpdateRegulatoryNumberRequest -
// unifying those two is a cross-repo change (frontend's nation-from-postcode.js, management-be's
// NationResolver) out of scope here.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Nation
{
    England,
    Scotland,
    Wales,
    NorthernIreland,
}
