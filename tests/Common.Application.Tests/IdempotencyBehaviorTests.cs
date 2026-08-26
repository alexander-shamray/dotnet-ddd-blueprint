using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>
/// §8.5's behaviour, driven directly rather than through the container. What is
/// under test is which store call happens on which path, and a pipeline around
/// it would only add ways for a failure to be attributed to the wrong
/// behaviour. The one test that does build a container is the fail-open gate at
/// the bottom, where the container's own selection <em>is</em> the subject.
/// </summary>
public class IdempotencyBehaviorTests
{
    private static readonly Guid Caller = Guid.Parse("0195e4b2-0000-7000-8000-00000000000a");
    private static readonly Guid Other = Guid.Parse("0195e4b2-0000-7000-8000-00000000000b");
    private static readonly Guid Command = Guid.Parse("0195e4b2-0000-7000-8000-0000000000ff");

    private static IdempotencyBehavior<ProtectedCommand, Result<Guid>> Behaviour(
        RecordingIdempotencyStore store,
        ICurrentUser? user = null) =>
        new(store, user ?? StubCurrentUser.Authenticated(Caller));

    [Fact]
    public async Task A_first_attempt_claims_the_key_runs_the_handler_and_records_the_outcome()
    {
        RecordingIdempotencyStore store = new();
        int handlerRuns = 0;
        Guid placed = Guid.CreateVersion7();

        Result<Guid> result = await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () =>
            {
                handlerRuns++;
                return Task.FromResult(Result.Success(placed));
            },
            TestContext.Current.CancellationToken);

        result.Value.ShouldBe(placed);
        handlerRuns.ShouldBe(1);
        store.Calls.ShouldBe([$"claim {ExpectedKey}", $"complete {ExpectedKey}"]);
        store.Entries[ExpectedKey].ShouldBe(new IdempotencyEntry(false, $"\"{placed}\""));
    }

    [Fact]
    public async Task The_outcome_is_recorded_under_the_token_the_claim_returned()
    {
        // #127: the store can only refuse a write from an attempt that has
        // lost its claim if the behaviour carries the token that claim minted.
        // Nothing else in this suite would notice the behaviour inventing one
        // — the double would refuse the write, the entry would be left in
        // progress, and every assertion above is about the CALL rather than
        // about what it wrote.
        RecordingIdempotencyStore store = new();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            TestContext.Current.CancellationToken);

        store.MintedToken.ShouldNotBeNull();
        store.WrittenUnder.ShouldBe(store.MintedToken);
    }

    [Fact]
    public async Task The_release_after_a_fault_carries_the_token_the_claim_returned()
    {
        // The other write path, and the one where a wrong token would be
        // worse: a release that cannot prove ownership either deletes a
        // successor's live claim or leaves this one held for a day, and which
        // of those it is depends on the store rather than on the behaviour.
        RecordingIdempotencyStore store = new();

        await Should.ThrowAsync<InvalidOperationException>(
            Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => throw new InvalidOperationException("boom"),
                TestContext.Current.CancellationToken));

        store.MintedToken.ShouldNotBeNull();
        store.WrittenUnder.ShouldBe(store.MintedToken);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_retry_of_a_completed_command_replays_the_value_without_running_the_handler()
    {
        // The whole point of the section: the second dispatch of one CommandId
        // must not place a second order. A replay that ran the handler and
        // discarded its result would look identical from the caller's side and
        // be the defect.
        RecordingIdempotencyStore store = new();
        Guid placed = Guid.CreateVersion7();
        store.Completed(ExpectedKey, $"\"{placed}\"");
        int handlerRuns = 0;

        Result<Guid> result = await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () =>
            {
                handlerRuns++;
                return Task.FromResult(Result.Success(Guid.CreateVersion7()));
            },
            TestContext.Current.CancellationToken);

        handlerRuns.ShouldBe(0, "the handler must not run on a replay");
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(placed, "the replayed value is the first attempt's, not a new one");
    }

    [Fact]
    public async Task A_void_command_replays_a_success_carrying_no_value()
    {
        // The NoValue path, and the reason the marker is "null" rather than the
        // empty string: an implementation reading "" as absent would replay
        // every void command as ConcurrentRequestException for a day.
        RecordingIdempotencyStore store = new();
        store.Completed(VoidKey, "null");
        int handlerRuns = 0;

        IdempotencyBehavior<VoidProtectedCommand, Result> behaviour =
            new(store, StubCurrentUser.Authenticated(Caller));

        Result result = await behaviour.HandleAsync(
            new VoidProtectedCommand(Command),
            () =>
            {
                handlerRuns++;
                return Task.FromResult(Result.Success());
            },
            TestContext.Current.CancellationToken);

        handlerRuns.ShouldBe(0);
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_request_while_the_first_is_in_flight_is_refused()
    {
        RecordingIdempotencyStore store = new();
        store.InFlight(ExpectedKey);

        ConcurrentRequestException thrown = await Should.ThrowAsync<ConcurrentRequestException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                TestContext.Current.CancellationToken));

        thrown.CommandId.ShouldBe(Command);
    }

    [Fact]
    public async Task An_entry_that_vanished_between_the_claim_and_the_read_is_refused()
    {
        // TryClaim says held, Get says nothing — the entry expired in the
        // window between them. Refusing is the only honest answer: the
        // behaviour cannot tell that from an attempt still running, and
        // running the handler would be a duplicate write if it was.
        RecordingIdempotencyStore store = new();
        VanishingStore vanishing = new(store);

        await Should.ThrowAsync<ConcurrentRequestException>(
            () => new IdempotencyBehavior<ProtectedCommand, Result<Guid>>(
                    vanishing,
                    StubCurrentUser.Authenticated(Caller))
                .HandleAsync(
                    new ProtectedCommand(Command),
                    () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                    TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_handler_that_throws_releases_the_claim_and_the_original_fault_survives()
    {
        // Both halves matter. Releasing lets the caller legitimately retry;
        // the fault surviving is what stops a store call from replacing the
        // domain's own exception with a Redis one.
        RecordingIdempotencyStore store = new();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => throw new InvalidOperationException("handler exploded"),
                TestContext.Current.CancellationToken));

        thrown.Message.ShouldBe("handler exploded");
        store.Calls.ShouldBe([$"claim {ExpectedKey}", $"release {ExpectedKey}"]);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_failed_Result_releases_the_claim()
    {
        // A refusal is rolled back by ExecuteAsync disposing an uncommitted
        // transaction (§6.3), so there is no outcome worth replaying — and
        // holding the key would replay the refusal to the caller who fixed
        // their request and retried under the same key.
        RecordingIdempotencyStore store = new();

        Result<Guid> result = await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Failure<Guid>(Error.Rule("test.refused", "No."))),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        store.Calls.ShouldBe([$"claim {ExpectedKey}", $"release {ExpectedKey}"]);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    [Fact]
    public async Task A_store_failure_after_the_handler_holds_the_claim_rather_than_releasing_it()
    {
        // The §8.5 release table's third row. The work is durable by now, so
        // releasing would permit the duplicate outright; holding postpones it
        // to the retention. The assertion is the ABSENCE of a release — which
        // is exactly the regression #70 was.
        RecordingIdempotencyStore store = new()
        {
            CompleteFault = new TimeoutException("redis went away")
        };

        await Should.ThrowAsync<TimeoutException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
                TestContext.Current.CancellationToken));

        store.Calls.ShouldBe(
            [$"claim {ExpectedKey}", $"complete {ExpectedKey}"],
            "a fault raised after the transaction committed must not release the claim");
    }

    [Fact]
    public async Task The_key_carries_the_authenticated_subject_and_not_the_command()
    {
        RecordingIdempotencyStore store = new();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            TestContext.Current.CancellationToken);

        store.Calls[0].ShouldBe($"claim {Caller}:{ProtectedCommand.OperationName}:{Command}");
    }

    [Fact]
    public async Task Two_subjects_sending_one_CommandId_do_not_collide()
    {
        // #40's scenario, as a test rather than a paragraph. CommandId is
        // client-generated, so A can name B's value; without the subject
        // segment A would be handed B's order id by the replay branch.
        RecordingIdempotencyStore store = new();
        Guid mine = Guid.CreateVersion7();
        Guid theirs = Guid.CreateVersion7();

        Result<Guid> first = await Behaviour(store, StubCurrentUser.Authenticated(Caller)).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(mine)),
            TestContext.Current.CancellationToken);

        Result<Guid> second = await Behaviour(store, StubCurrentUser.Authenticated(Other)).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(theirs)),
            TestContext.Current.CancellationToken);

        first.Value.ShouldBe(mine);
        second.Value.ShouldBe(theirs, "the second caller ran their own command rather than replaying the first's");
        store.Entries.Count.ShouldBe(2, "one key per subject");
    }

    [Fact]
    public async Task A_caller_with_no_principal_claims_under_the_shared_system_segment()
    {
        // Stated as a test because §8.5 names it as the section's largest
        // residual rather than as a property to be pleased about: "system" is
        // not one caller, it is every caller who is not one. The rule that
        // follows from it — an idempotent command's endpoint must require
        // authentication — is asserted per service, not here.
        RecordingIdempotencyStore store = new();

        await Behaviour(store, StubCurrentUser.Anonymous()).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            TestContext.Current.CancellationToken);

        store.Calls[0].ShouldBe($"claim system:{ProtectedCommand.OperationName}:{Command}");
    }

    [Fact]
    public void The_operation_segment_is_declared_and_is_not_the_type_name()
    {
        // #114. A key built from typeof(TCommand).Name changes under an
        // ordinary rename, and a rolling deployment then serves both
        // spellings — so one CommandId is protected by neither claim. The
        // assertion is not that the string is any particular value but that it
        // is NOT the CLR name, which is the value a later reader is most
        // likely to "simplify" it back to.
        ProtectedCommand.OperationName.ShouldNotBe(nameof(ProtectedCommand));
        ProtectedCommand.OperationName.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_command_that_does_not_opt_in_runs_unprotected_and_says_nothing()
    {
        // The fail-open route, pinned rather than lamented. The container
        // omits an open-generic registration whose constraints the closed type
        // does not satisfy — silently, with no diagnostic — so a command that
        // forgets IIdempotentCommand is dispatched with no claim at all. This
        // is why each service carries a reflection gate over the SHAPE of its
        // commands; this test is what establishes that the gate is needed.
        RecordingIdempotencyStore store = new();

        using ServiceProvider provider = TestContainer.Build(services =>
        {
            services.AddSingleton<IIdempotencyStore>(store);
            services.AddSingleton<ICurrentUser>(StubCurrentUser.Authenticated(Caller));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        });

        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result result = await dispatcher.SendAsync(
            new UnprotectedCommand(),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        store.Calls.ShouldBeEmpty("the behaviour was never selected, and nothing said so");
    }

    [Fact]
    public async Task The_completion_is_made_with_None_rather_than_the_callers_token()
    {
        // §8.5's rule, and the one the suite could not see. The handler has
        // committed by this line, so a completion that honoured a cancelled
        // caller would leave the key claimed with the work durable — a retry
        // then meets ConcurrentRequestException until the retention expires,
        // and runs a second time after it.
        RecordingIdempotencyStore store = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Success(Guid.CreateVersion7())),
            cancelled.Token);

        // The claim is the positive control: it proves the map records the
        // argument, so the assertion below is not passing on an absent entry
        // reading back as default — which is what CancellationToken.None is.
        store.Tokens["claim"].ShouldBe(cancelled.Token);
        store.Tokens["complete"].ShouldBe(CancellationToken.None);
    }

    [Fact]
    public async Task The_release_after_a_refusal_is_made_with_None_rather_than_the_callers_token()
    {
        RecordingIdempotencyStore store = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Behaviour(store).HandleAsync(
            new ProtectedCommand(Command),
            () => Task.FromResult(Result.Failure<Guid>(Error.Rule("test.refused", "No."))),
            cancelled.Token);

        store.Tokens["claim"].ShouldBe(cancelled.Token);
        store.Tokens["release"].ShouldBe(CancellationToken.None);
    }

    [Fact]
    public async Task The_release_after_a_thrown_handler_is_made_with_None_rather_than_the_callers_token()
    {
        // The sharpest of the three: the commonest reason to be releasing at
        // all is the caller's own cancellation, so honouring the token here
        // would abandon the release exactly when it is most needed and leak
        // the claim for a day.
        RecordingIdempotencyStore store = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<InvalidOperationException>(
            () => Behaviour(store).HandleAsync(
                new ProtectedCommand(Command),
                () => throw new InvalidOperationException("handler exploded"),
                cancelled.Token));

        store.Tokens["claim"].ShouldBe(cancelled.Token);
        store.Tokens["release"].ShouldBe(CancellationToken.None);
        store.Entries.ShouldNotContainKey(ExpectedKey);
    }

    private static string ExpectedKey => $"{Caller}:{ProtectedCommand.OperationName}:{Command}";

    private static string VoidKey => $"{Caller}:{VoidProtectedCommand.OperationName}:{Command}";

    /// <summary>
    /// Claims like the real store and then reports nothing — the expiry that
    /// lands between <c>TryClaimAsync</c> and <c>GetAsync</c>.
    /// </summary>
    private sealed class VanishingStore(IIdempotencyStore inner) : IIdempotencyStore
    {
        public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult<IdempotencyEntry?>(null);

        public Task CompleteAsync(
            string key,
            string claim,
            string payload,
            TimeSpan retention,
            CancellationToken ct) =>
            inner.CompleteAsync(key, claim, payload, retention, ct);

        public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
            inner.ReleaseAsync(key, claim, ct);
    }
}

/// <summary>An opted-in command returning a value — §6.4's shape.</summary>
public sealed record ProtectedCommand(Guid CommandId) : ICommand<Result<Guid>>, IIdempotentCommand
{
    public static string OperationName => "tests.protected";
}

public sealed class ProtectedCommandHandler : ICommandHandler<ProtectedCommand, Result<Guid>>
{
    public Task<Result<Guid>> HandleAsync(ProtectedCommand command, CancellationToken ct) =>
        Task.FromResult(Result.Success(Guid.CreateVersion7()));
}

/// <summary>An opted-in command returning nothing — the <c>NoValue</c> path.</summary>
public sealed record VoidProtectedCommand(Guid CommandId) : ICommand<Result>, IIdempotentCommand
{
    public static string OperationName => "tests.void";
}

/// <summary>
/// Returns a <see cref="Result"/> and does <b>not</b> declare
/// <see cref="IIdempotentCommand"/>. It satisfies the behaviour's second
/// constraint and fails the first, which is what makes it the right subject for
/// the fail-open test: the omission under test is the opt-in, not the shape of
/// the result.
/// </summary>
public sealed record UnprotectedCommand : ICommand<Result>;

public sealed class UnprotectedCommandHandler : ICommandHandler<UnprotectedCommand, Result>
{
    public Task<Result> HandleAsync(UnprotectedCommand command, CancellationToken ct) =>
        Task.FromResult(Result.Success());
}
