namespace Common.Application;

/// <summary>
/// The outcome of a command that returns nothing. There is no <c>Unit</c> type
/// and no <c>Result&lt;void&gt;</c> — this is the void case, and
/// <see cref="Result{TValue}"/> derives from it.
/// </summary>
/// <remarks>
/// The derivation is what lets one pattern test any command's outcome:
/// <c>result is Result { IsFailure: true }</c> in §6.3's transaction behaviour,
/// which sees a handler's return value as <c>object</c> and cannot know its
/// value type. Two sibling types would force that behaviour to reflect over
/// every closed generic it might be handed.
/// </remarks>
public class Result
{
    private readonly Error? _error;

    private protected Result(Error? error)
    {
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    /// <summary>
    /// Non-nullable, and throws on a success rather than returning null. §9.4's
    /// consumer reads <c>result.Error.Code</c> inside an
    /// <c>if (result.IsFailure)</c>, and a nullable property would put a
    /// null-forgiving operator on every such call site — which is the operator
    /// that stops meaning anything once it is everywhere.
    /// </summary>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result carries no error.");

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<TValue> Success<TValue>(TValue value) => new(value, null);

    public static Result<TValue> Failure<TValue>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(default, error);
    }
}

/// <summary>
/// The outcome of a command that returns a value. Constructed through
/// <see cref="Result.Success{TValue}"/> and <see cref="Result.Failure{TValue}"/>
/// so that the two states cannot be assembled independently: no result can
/// report success while carrying an error, or failure while carrying a value.
/// </summary>
/// <remarks>
/// That guarantee is about the state, not the payload, and the asymmetry with
/// <see cref="Result.Failure{TValue}"/> is deliberate. An <see cref="Error"/>
/// is what <em>makes</em> a result a failure — <see cref="Result.IsSuccess"/>
/// is defined by its absence — so a null one would silently produce a success
/// and is refused at run time. A value is only payload: a null one leaves a
/// success a success. The nullable annotation on the factory already rejects
/// <c>Success&lt;string&gt;(null)</c> at compile time, and a caller who asks
/// for <c>Result&lt;string?&gt;</c> has said what they meant.
/// </remarks>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, Error? error) : base(error)
    {
        _value = value;
    }

    public TValue Value =>
        IsSuccess ? _value! : throw new InvalidOperationException("A failed result carries no value.");
}
