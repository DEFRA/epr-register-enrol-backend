using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-448: caller-supplied (the caller, e.g. management-be, is the one who reliably
// knows which nation an application belongs to - this backend has no reliable
// internal source for it, see the RA-448 design notes). Values match NATIONS in
// epr-register-enrol-frontend's src/server/common/helpers/nation-from-postcode.js
// exactly, so the wire contract lines up with the rest of this ecosystem.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Nation
{
    England,
    Scotland,
    Wales,
    NorthernIreland,
}
