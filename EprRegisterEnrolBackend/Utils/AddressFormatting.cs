namespace EprRegisterEnrolBackend.Utils;

// RA-697: one join for every flattened display address, so ReEx's addresses and the domain model's read the same.
public static class AddressFormatting
{
    /// <summary>The non-blank parts, comma separated; empty when every part is blank.</summary>
    public static string Join(params string?[] parts) =>
        string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
