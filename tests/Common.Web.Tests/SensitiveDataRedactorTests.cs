using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    private static readonly Action<ILogger, string, Exception?> Failed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(7, nameof(Failed)),
            "Login failed with {Password}");

    private static readonly Action<ILogger, string, Exception?> Plain =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2, nameof(Plain)),
            "Customer {Customer} signed in");

    private static LogRecord EmitRecord(Action<ILogger> write)
    {
        List<LogRecord> exported = [];

        // Built exactly as AddObservability builds it (§13.2) — ILoggingBuilder,
        // the same extension, and IncludeFormattedMessage set the same way — so
        // the test covers the seam the host uses. Not the Logs Bridge API: that
        // is behind an experimental diagnostic and is not how any host here
        // produces a record.
        //
        // IncludeFormattedMessage is not decoration. With it set the exporter
        // sends FormattedMessage as the record's body, so a test that leaves it
        // off asserts against a pipeline shape no host runs.
        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return exported.Single();
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> Emit(Action<ILogger> write) =>
        EmitRecord(write).Attributes!;

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
    public void A_redacted_record_does_not_export_the_rendered_secret()
    {
        // The attribute redaction above is cosmetic without this one.
        // AddObservability sets IncludeFormattedMessage (§13.2), and the OTLP
        // exporter then uses FormattedMessage as the exported body — so the
        // fully substituted "Login for ada with hunter2" travels beside a
        // Password attribute reading "[redacted]", and it is the rendered
        // string that gets indexed and searched.
        LogRecord record = EmitRecord(logger => Login(logger, "ada", "hunter2", null));

        record.FormattedMessage.ShouldNotBeNull();
        record.FormattedMessage.ShouldNotContain("hunter2");

        // Falling back to the template keeps the record readable. The safe
        // values are still on the record as attributes, so nothing a reader
        // needs is lost — only the substitution is.
        record.FormattedMessage.ShouldBe("Login for {User} with {Password}");
    }

    [Fact]
    public void A_record_with_nothing_sensitive_keeps_its_formatted_message()
    {
        // The control, and the one that matters most: the fix above rewrites
        // the message only when something was actually redacted. Without this
        // assertion a processor that rewrote unconditionally would pass, and
        // every log line on the platform would silently lose its values.
        LogRecord record = EmitRecord(logger => Plain(logger, "ada", null));

        record.FormattedMessage.ShouldBe("Customer ada signed in");
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

    [Fact]
    public void A_state_with_no_template_loses_its_message_rather_than_re_exporting_it()
    {
        // Without {OriginalFormat} OpenTelemetry fills Body with the
        // formatter's own output, not a template — so the rendered secret is
        // sitting in Body, and falling back to it would re-export exactly what
        // the attribute scrub removed. Measured against 1.17.
        KeyValuePair<string, object?>[] state = [new("Password", "hunter2")];

        LogRecord record = EmitRecord(logger =>
            logger.Log(LogLevel.Information, new EventId(6), state, null, (s, _) => $"password is {s[0].Value}"));

        record.Attributes!.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.FormattedMessage.ShouldBe("[redacted]");
    }

    [Fact]
    public void An_exception_repeating_a_redacted_value_is_dropped()
    {
        // OTLP serialises Exception separately from Attributes and
        // FormattedMessage, as exception.message and exception.stacktrace, so
        // scrubbing those two and leaving the exception alone ships the secret
        // through a third channel.
        LogRecord record = EmitRecord(logger =>
            Failed(logger, "hunter2", new InvalidOperationException("auth rejected token hunter2")));

        record.Attributes!.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.Exception.ShouldBeNull();
    }

    [Fact]
    public void An_exception_that_reveals_nothing_is_kept()
    {
        // The control, and the reason this is narrower than "drop the
        // exception whenever anything was redacted": a stack trace is what an
        // operator needs most on the error path, and a record that merely has
        // a Password attribute beside an unrelated failure must keep it.
        InvalidOperationException failure = new("connection reset by peer");

        LogRecord record = EmitRecord(logger => Failed(logger, "hunter2", failure));

        record.Attributes!.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");
        record.Exception.ShouldBeSameAs(failure);
    }

    [Fact]
    public void No_other_logging_provider_survives_to_see_the_rendered_secret()
    {
        // The redactor only ever sees records inside the OpenTelemetry
        // pipeline, so a second provider would format the original state
        // itself and ship the secret. AddObservability clears providers for
        // exactly this reason (§13.4); this stands in for the Console, Debug
        // and EventSource providers WebApplication.CreateBuilder installs
        // before a host reaches AddCommonWebDefaults (§4.2).
        HostApplicationBuilder builder = TelemetryHost.Builder();
        CapturingProvider console = new();
        builder.Logging.AddProvider(console);

        builder.AddObservability();

        using IHost host = builder.Build();
        Login(host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("test"), "ada", "hunter2", null);

        console.Messages.ShouldBeEmpty();
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        internal readonly ConcurrentQueue<string> Messages = new();

        public ILogger CreateLogger(string categoryName) => new Sink(Messages);

        public void Dispose()
        {
        }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class CapturingProcessor(Action<LogRecord> capture) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord record) => capture(record);
    }
}
