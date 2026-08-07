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
/// <b>Scope.</b> This governs the OpenTelemetry pipeline and nothing else, so
/// <c>AddObservability</c> clears every other logging provider before adding
/// it (§13.2, §13.4) — a provider outside this pipeline formats the original
/// state itself and would ship the secret the processor just scrubbed.
/// <para>
/// <b>What it does.</b> Matching is by attribute <em>key</em>, and a match
/// rewrites two things: the attribute's value, and — because <c>§13.2</c> sets
/// <c>IncludeFormattedMessage</c> and the exporter then ships
/// <c>FormattedMessage</c> as the record's body — the rendered message, which
/// falls back to the un-substituted template. A record with nothing sensitive
/// on it is left exactly as it arrived.
/// </para>
/// <para>
/// Four limits worth stating rather than discovering. An exception whose
/// text repeats a redacted value causes the exception to be dropped, but one
/// carrying a secret the attributes never named survives — there is nothing
/// to match it against, which is the interpolation case in another costume. Redaction is by key
/// alone, which is the argument for naming a placeholder <c>{Token}</c> and
/// never interpolating: an interpolated secret produces no attribute to match
/// and lands in the template itself, so the fallback carries it too. It cannot
/// help with a whole object logged as one attribute — that is what the "never
/// log full request bodies" half of the rule is for. And it does not read
/// scopes: <c>IncludeScopes</c> is on, but the processor inspects
/// <c>Attributes</c> only, so a sensitive key in a <c>BeginScope</c> dictionary
/// is exported unredacted. Nothing leaks today, because the platform opens
/// exactly two scopes and neither can carry a secret — <c>LoggingBehavior</c>'s
/// <c>RequestType</c> (§13.3) is a type name, and <c>UseCorrelationId</c>'s
/// <c>CorrelationId</c> (§10.4) is a trace ID or a GUID. A third one carrying a
/// secret would leak it silently. Widening the processor to walk the scope
/// provider is a design change, not a fix.
/// </para>
/// </remarks>
public sealed class SensitiveDataRedactor : BaseProcessor<LogRecord>
{
    // The key ILogger puts the message template under. Its presence is what
    // makes Body a template rather than a rendered line — see OnEnd.
    private const string OriginalFormat = "{OriginalFormat}";

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
        List<string>? secrets = null;
        bool hasTemplate = false;

        for (int i = 0; i < record.Attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = record.Attributes[i];

            if (attribute.Key == OriginalFormat)
                hasTemplate = true;

            if (!IsSensitive(attribute.Key))
                continue;

            // Copy only when something actually matches — the common case is
            // no match, and this runs on every log record on every request.
            scrubbed ??= [.. record.Attributes];

            // Keep what was removed. The exception check below needs the
            // values, not the keys — see the rule it enforces.
            if (attribute.Value?.ToString() is { Length: > 0 } secret)
                (secrets ??= []).Add(secret);

            scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
        }

        if (scrubbed is null)
            return;

        record.Attributes = scrubbed;

        // Attributes alone are not enough, and stopping there is the failure
        // this rewrite exists to prevent. AddObservability sets
        // IncludeFormattedMessage (§13.2), and with it the exporter sends
        // FormattedMessage as the record's body — the template with every
        // argument already substituted. Redacting Password to "[redacted]"
        // while "Login for ada with hunter2" ships beside it protects nothing
        // and reads in review as though it does.
        //
        // Body is only the un-substituted template when the state actually
        // carried {OriginalFormat}. Without it OpenTelemetry fills Body with
        // the formatter's own output — the rendered line, secret and all — so
        // falling back to Body there would re-export precisely what the
        // attribute scrub just removed. Measured against 1.17 rather than
        // assumed: a state of [Password=hunter2] with a formatter returning
        // "password is hunter2" produces that string in Body.
        //
        // With a template, Body is safe and is the most readable thing left:
        // every non-sensitive value is still on the record as an attribute.
        // Without one there is nothing to fall back to, so the message goes
        // entirely rather than partly.
        record.FormattedMessage = hasTemplate && record.Body is not null
            ? record.Body
            : "[redacted]";

        // The rule this enforces: never export a value the processor has just
        // decided is sensitive. OTLP serialises Exception separately from both
        // Attributes and FormattedMessage — as exception.message and
        // exception.stacktrace — so scrubbing those two and leaving the
        // exception alone ships the secret through a third channel.
        //
        // ToString() rather than Message, because it covers inner exceptions
        // and the stack trace too. Dropped rather than rewritten: Exception
        // .Message is read-only, and reconstructing the type is not something
        // to attempt on a logging path. The record keeps its level, template
        // and every non-sensitive attribute, so the error is still visible —
        // what is lost is the trace, on the records that demonstrably carry a
        // live secret in it.
        //
        // Deliberately narrower than "drop the exception whenever anything was
        // redacted": that would destroy stack traces on every record that
        // merely has a Password attribute beside an unrelated failure, which
        // is most of them.
        if (record.Exception is not null && secrets is not null && Reveals(record.Exception, secrets))
            record.Exception = null;
    }

    private static bool Reveals(Exception exception, List<string> secrets)
    {
        string text = exception.ToString();

        foreach (string secret in secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // A foreach rather than Sensitive.Any(s => key.Contains(s, ...)): the
    // lambda would capture `key`, so the closure allocates once per attribute
    // inspected — including on the no-match path the copy above is written to
    // keep allocation-free. This runs on every attribute of every log record.
    private static bool IsSensitive(string key)
    {
        foreach (string term in Sensitive)
        {
            if (key.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
