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
        foreach (Type command in Commands().Where(typeof(IIdempotentCommand).IsAssignableFrom))
        {
            string? name = (string?)command
                .GetProperty(nameof(IIdempotentCommand.OperationName), BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null);

            name.ShouldNotBeNullOrWhiteSpace();
            name.ShouldNotBe(command.Name, $"{command.Name} keys on its own CLR name (§8.5)");
        }
    }

    private static Type[] Commands() =>
    [
        .. Application
            .GetTypes()
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
    ];
}
