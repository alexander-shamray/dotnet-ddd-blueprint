namespace Common.Application;

/// <summary>
/// A request that changes state. <typeparamref name="TResult"/> is what the
/// handler returns — a <see cref="Result"/> or <see cref="Result{T}"/> for
/// anything the domain may refuse (§6.4).
/// </summary>
public interface ICommand<out TResult>;

/// <summary>
/// Handles exactly one command type. One handler per command: the dispatcher
/// resolves it with <c>GetRequiredService</c>, so a second registration is a
/// bug the container reports rather than a race it silently picks a winner for.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken ct);
}
