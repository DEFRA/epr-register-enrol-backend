using System.Text.RegularExpressions;
using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EprRegisterEnrolBackend.Utils.Logging;

/// <summary>
/// Redacts PII from log events before they reach any sink. Must be registered
/// after <c>Enrich.WithEcsHttpContext(...)</c> and <c>Enrich.FromLogContext()</c>
/// so it has the final say over every property, regardless of source.
/// </summary>
public sealed class PiiRedactionEnricher : ILogEventEnricher
{
    public const string RedactedValue = "[REDACTED]";

    // Matches an email address anywhere within a string property's value
    // (e.g. embedded in a raw downstream-API error body), not just a
    // property whose own key happens to be named "Email".
    private static readonly Regex EmailPattern = new(
        @"[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250)
    );

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        RedactHttpContextClient(logEvent);
        RedactEmailsInStringProperties(logEvent);
    }

    private static void RedactHttpContextClient(LogEvent logEvent)
    {
        if (
            !logEvent.Properties.TryGetValue(
                SpecialProperties.SpecialKeys.HttpContext,
                out var value
            )
            || value
                is not ScalarValue { Value: SpecialProperties.HttpContextEnrichments enrichments }
        )
        {
            return;
        }

        if (enrichments.Client is not null)
        {
            // client.address is sometimes populated with the same raw IP as client.ip
            // (ECS spec: ".address" holds the raw value, duplicated to ".ip").
            enrichments.Client.Ip = RedactedValue;
            enrichments.Client.Address = RedactedValue;
            RedactUser(enrichments.Client.User);
        }

        RedactUser(enrichments.User);
        RedactAdminQuery(enrichments.Url);
    }

    // The Admin API's search terms (/admin/accreditation-applications?q=...)
    // can be a person's name, and the ECS enricher puts the request's URL,
    // query included, on every log event during the request. Keep the path,
    // drop the query.
    private static void RedactAdminQuery(Url? url)
    {
        if (!IsAdminPath(url?.Path))
        {
            return;
        }

        url.Query = null;
        url.Full = WithoutQuery(url.Full);
        url.Original = WithoutQuery(url.Original);
    }

    // /admin itself too, which is a 404 but still carries whatever query was sent.
    private static bool IsAdminPath(string? path) =>
        path is not null
        && (
            path.Equals("/admin", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)
        );

    private static string? WithoutQuery(string? url)
    {
        var queryStart = url?.IndexOf('?') ?? -1;
        return queryStart < 0 ? url : url![..queryStart];
    }

    // Id/Group/Domain are opaque identifiers, not PII, and are left as-is.
    // Name/FullName/Email/Hash are populated from the request's Name/Email
    // claims by the ECS enricher itself and are never touched by anything
    // else in this pipeline.
    private static void RedactUser(User? user)
    {
        if (user is null)
        {
            return;
        }

        user.Name = null;
        user.FullName = null;
        user.Email = null;
        user.Hash = null;
    }

    // Catches an email address embedded anywhere in any logged string
    // property - e.g. a downstream API's raw error-response body echoing
    // back a submitted address - not just a property named exactly "Email".
    private static void RedactEmailsInStringProperties(LogEvent logEvent)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (value is not ScalarValue { Value: string text })
            {
                continue;
            }

            string? redactedText;
            try
            {
                redactedText = EmailPattern.IsMatch(text)
                    ? EmailPattern.Replace(text, RedactedValue)
                    : null;
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail safe: a value too long to scan in time is treated as
                // unsafe to log verbatim, rather than let it through unredacted.
                redactedText = RedactedValue;
            }

            if (redactedText is not null)
            {
                logEvent.AddOrUpdateProperty(
                    new LogEventProperty(name, new ScalarValue(redactedText))
                );
            }
        }
    }
}
