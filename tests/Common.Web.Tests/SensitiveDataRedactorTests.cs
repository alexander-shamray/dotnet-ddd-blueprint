using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class SensitiveDataRedactorTests
{
    // CA1848 is enforced repo-wide (ADR-019) and does not exempt test
    // projects, so the template goes through LoggerMessage.Define exactly as
    // production logging does. §13.4's point survives intact: the attribute
    // keys still come from a message template, read through ILogger.
    private static readonly Action<ILogger, string, string, Exception?> Login =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, nameof(Login)),
            "Login for {User} with {Password}");

    private static readonly Action<ILogger, string, Exception?> Plain =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(Plain)),
            "Customer {Customer} signed in");

    private static IReadOnlyList<KeyValuePair<string, object?>> Emit(Action<ILogger> write)
    {
        List<LogRecord> exported = [];

        // Built exactly as AddObservability builds it (§13.2) — ILoggingBuilder,
        // the same extension — so the test covers the seam the host uses. Not
        // the Logs Bridge API: that is behind an experimental diagnostic and is
        // not how any host here produces a record.
        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return exported.Single().Attributes!;
    }

    [Fact]
    public void Sensitive_attributes_are_redacted()
    {
        IReadOnlyList<KeyValuePair<string, object?>> attributes =
            Emit(logger => Login(logger, "ada", "hunter2", null));

        attributes.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");

        // The other half, and the one that catches a deny-list grown careless:
        // everything not on it survives intact.
        attributes.Single(a => a.Key == "User").Value.ShouldBe("ada");
    }

    [Fact]
    public void Matching_is_by_substring_and_ignores_case()
    {
        // ILogger.Log with an explicit state rather than a template, because
        // CA1727 requires PascalCase placeholders and `card_number` — the exact
        // key the deny list carries an entry for — cannot be written as one.
        // The state is what the processor actually sees, so this reaches the
        // same seam by the only route the analyser leaves open.
        KeyValuePair<string, object?>[] state =
        [
            new("NewPassword", "a"),
            new("card_number", "b"),
            new("Authorization", "c"),
            new("Customer", "ada")
        ];

        IReadOnlyList<KeyValuePair<string, object?>> attributes = Emit(logger =>
            logger.Log(LogLevel.Information, new EventId(3), state, null, (_, _) => "signed in"));

        attributes.Single(a => a.Key == "NewPassword").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "card_number").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Authorization").Value.ShouldBe("[redacted]");
        attributes.Single(a => a.Key == "Customer").Value.ShouldBe("ada");
    }

    [Fact]
    public void A_record_with_no_attributes_at_all_is_left_alone()
    {
        // The guard clause. Reachable through ILogger with a null state, and
        // worth pinning because the loop below it dereferences Attributes
        // twice per record on every request.
        List<LogRecord> exported = [];

        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            factory.CreateLogger("test").Log<object?>(
                LogLevel.Information, new EventId(4), null, null, (_, _) => "no state");
        }

        exported.Single().Attributes.ShouldBeNull();
    }

    // Pins the `scrubbed ??=` fast path, which exists because this runs on
    // every log record on every request. Nothing else would catch its removal
    // — the redaction tests above pass whether or not it copies.
    //
    // Measured either side of the redactor by two capturing processors, with
    // NO exporter in the pipeline. AddInMemoryExporter cannot be used for an
    // identity assertion: its export path calls LogRecord.Copy(), which
    // unconditionally reallocates the attribute list as the SDK's defence
    // against record pooling. Verified by decompiling
    // OpenTelemetry.Exporter.InMemory 1.17.0, after an earlier version of this
    // test failed against it for that reason and nothing to do with the code
    // under test.
    private static (object? Before, object? After) AttributesEitherSideOf(Action<ILogger> write)
    {
        object? before = null;
        object? after = null;

        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.AddProcessor(new CapturingProcessor(r => before = r.Attributes));
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddProcessor(new CapturingProcessor(r => after = r.Attributes));
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return (before, after);
    }

    [Fact]
    public void A_record_with_nothing_sensitive_is_not_copied()
    {
        (object? before, object? after) =
            AttributesEitherSideOf(logger => Plain(logger, "ada", null));

        // Same instance, not merely an equal one: the processor returned
        // without allocating.
        after.ShouldBeSameAs(before);
    }

    [Fact]
    public void A_record_with_something_sensitive_is_copied()
    {
        // The control. Without it the test above passes against a redactor
        // that never copies anything — including one that never redacts.
        (object? before, object? after) =
            AttributesEitherSideOf(logger => Login(logger, "ada", "hunter2", null));

        after.ShouldNotBeSameAs(before);
    }

    private sealed class CapturingProcessor(Action<LogRecord> capture) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record) => capture(record);
    }
}
