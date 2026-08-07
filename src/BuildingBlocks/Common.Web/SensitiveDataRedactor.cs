using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Common.Web;

/// <summary>
/// §13.4's never-log rule, given a mechanism. Added to the OpenTelemetry
/// logging pipeline by <c>AddObservability</c> (§13.2), which is the point:
/// every host calls it, so the rule applies to all of them. In a service's own
/// project it would protect that service alone.
/// </summary>
/// <remarks>
/// Two limits worth stating rather than discovering. The processor sees
/// <em>attributes</em>, not the formatted message, so a value is redacted by
/// its key alone — which is the argument for naming a placeholder
/// <c>{Token}</c> and never interpolating. And it cannot help with a whole
/// object logged as one attribute; that is what the "never log full request
/// bodies" half of the rule is for.
/// </remarks>
public sealed class SensitiveDataRedactor : BaseProcessor<LogRecord>
{
    // Substring match, not equality: the field that leaks is never named
    // exactly "password" — it is "NewPassword", "card_number", "id_token".
    private static readonly string[] Sensitive =
        ["password", "secret", "token", "authorization", "cardnumber", "card_number", "ssn", "nationalid"];

    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        if (record.Attributes is null)
            return;

        List<KeyValuePair<string, object?>>? scrubbed = null;

        for (int i = 0; i < record.Attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = record.Attributes[i];
            if (!IsSensitive(attribute.Key))
                continue;

            // Copy only when something actually matches — the common case is
            // no match, and this runs on every log record on every request.
            scrubbed ??= [.. record.Attributes];
            scrubbed[i] = new(attribute.Key, "[redacted]");
        }

        if (scrubbed is not null)
            record.Attributes = scrubbed;
    }

    private static bool IsSensitive(string key) =>
        Sensitive.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));
}
