using System.Reflection;
using Common.Application;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §8.5's opt-in gate. <c>IdempotencyBehavior</c> is constrained to
/// <see cref="IIdempotentCommand"/>, and the container omits an open-generic
/// registration whose constraints the closed type does not satisfy —
/// <b>silently</b>. So a command that carries a <c>CommandId</c> and forgets
/// the interface is dispatched unprotected, with no error and no warning, and
/// a retry runs the whole command a second time.
/// </summary>
/// <remarks>
/// The shape of the command is what is read, not the author's intent: a
/// <c>CommandId</c> member is a claim that retrying is safe, so it is the one
/// signal available that does not depend on remembering. The measurement
/// behind this gate is in <c>Common.Application.Tests</c> —
/// <c>A_command_that_does_not_opt_in_runs_unprotected_and_says_nothing</c> —
/// which establishes that nothing else reports the omission.
/// </remarks>
public class IdempotencyOptInTests
{
    private static readonly Assembly Application = typeof(Ordering.Application.DependencyInjection).Assembly;

    [Fact]
    public void Commands_carrying_a_CommandId_declare_IIdempotentCommand()
    {
        IEnumerable<string> offenders = Commands()
            .Where(t => t.GetProperty("CommandId") is not null)
            .Where(t => !typeof(IIdempotentCommand).IsAssignableFrom(t))
            .Select(t => t.Name);

        offenders.ShouldBeEmpty(
            "a CommandId without IIdempotentCommand is a field that promises protection " +
            "the pipeline never applies (§6.4, §8.5)");
    }

    [Fact]
    public void The_gate_above_is_looking_at_this_service_s_commands()
    {
        // The gate-coverage rule: an empty offender list is the same green
        // whether every command opted in or the selector stopped matching
        // anything. This is the half that fails when ICommand<> moves,
        // Ordering's namespaces are reorganised, or the assembly anchor is
        // renamed — none of which the assertion above can see.
        Commands().ShouldNotBeEmpty("Ordering declares commands; the selector above found none");
    }

    [Fact]
    public void Every_idempotent_command_declares_a_stable_operation_name()
    {
        // #114. The key's middle segment must not be derivable from the type,
        // because a rename then changes a live key and a rolling deployment
        // serves both spellings at once. The compiler already refuses a
        // command that supplies no OperationName; what it cannot refuse is one
        // that supplies the type's own name back.
        foreach (Type command in Idempotent())
        {
            string name = OperationNameOf(command);

            name.ShouldNotBeNullOrWhiteSpace();
            name.ShouldNotBe(command.Name, $"{command.Name} keys on its own CLR name (§8.5)");
        }
    }

    [Fact]
    public void Every_idempotent_command_returns_a_Result()
    {
        // The second constraint, and until this test the behaviour's own
        // remarks claimed a gate that did not exist. `where TResult : Result`
        // fails open exactly as the interface constraint does: a command
        // declaring IIdempotentCommand and returning something else is a
        // registration the container silently omits, so the opt-in is written,
        // read as done, and never applied.
        //
        // Appendix D.5 makes Result and Result<T> the whole universe, so this
        // is a floor rather than a restriction — and it is the floor that has
        // to be asserted, because nothing in the compiler connects the
        // interface to the return type.
        foreach (Type command in Idempotent())
        {
            Type result = command
                .GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>))
                .GetGenericArguments()[0];

            typeof(Result).IsAssignableFrom(result).ShouldBeTrue(
                $"{command.Name} opts into idempotency and returns {result.Name}, which is not a Result — " +
                "so IdempotencyBehavior's second constraint drops the registration and it runs unprotected (§8.5)");
        }
    }

    [Fact]
    public void Operation_names_are_distinct_within_this_service()
    {
        // OperationName is the middle segment of a Redis key whose other two
        // are the subject and the caller's CommandId, so two commands sharing
        // one collapse into a single keyspace: the same caller reusing a
        // CommandId across them is served the FIRST command's stored payload,
        // deserialised into the second's result type. The docs say "give it a
        // value the domain would recognise", which is precisely the advice
        // that makes a copied string plausible.
        string[] names = [.. Idempotent().Select(OperationNameOf)];

        // The gate-coverage floor, and it carries more weight here than
        // usual: with one idempotent command a distinctness check cannot
        // fail, so this assertion is the only part of the test that is
        // live today. It arms itself on the second command, which is the
        // moment the check is worth having — and until then it at least
        // fails when the selector stops finding anything.
        names.ShouldNotBeEmpty("Ordering declares an idempotent command; the selector above found none");

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(
            names.Length,
            "two commands sharing an OperationName share a keyspace, and a replay then " +
            "reconstructs the wrong result type (§8.5)");
    }

    private static string OperationNameOf(Type command) =>
        (string)command
            .GetProperty(nameof(IIdempotentCommand.OperationName), BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    private static Type[] Idempotent() =>
        [.. Commands().Where(typeof(IIdempotentCommand).IsAssignableFrom)];

    private static Type[] Commands() =>
    [
        .. Application
            .GetTypes()
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
    ];
}
