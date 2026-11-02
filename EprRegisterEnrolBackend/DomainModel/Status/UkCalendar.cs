namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>UK calendar dates for instants. A host without the Europe/London zone falls back to UTC.</summary>
public static class UkCalendar
{
    private static readonly TimeZoneInfo s_zone = ResolveZone();

    /// <summary>The UK date of <paramref name="instant"/>. An unspecified kind is taken as UTC.</summary>
    public static DateOnly ToUkDate(DateTime instant)
    {
        var utc =
            instant.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(instant, DateTimeKind.Utc)
                : instant.ToUniversalTime();

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, s_zone));
    }

    private static TimeZoneInfo ResolveZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
