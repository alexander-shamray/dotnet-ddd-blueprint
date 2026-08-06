namespace Common.Application;

/// <summary>
/// A failure a handler chose to return, as opposed to one it threw. Code is a
/// stable identifier, Description is for a person, and Type selects the status
/// (§10.5).
/// </summary>
/// <remarks>
/// <see cref="Code"/> is a metric dimension — §9.8 tags
/// <c>command.domain_rejected</c> with it — so its value set has to be closed.
/// Each service constructs every <see cref="Error"/> it can return in one
/// static catalogue, which is what keeps the set enumerable by reflection and
/// reviewable by reading one file. An id interpolated into a code is a
/// cardinality incident; it belongs in <see cref="Description"/>.
/// </remarks>
public sealed record Error(string Code, string Description, ErrorType Type)
{
    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound);

    public static Error Rule(string code, string description) =>
        new(code, description, ErrorType.Rule);

    public static Error Unavailable(string code, string description) =>
        new(code, description, ErrorType.Unavailable);
}

/// <summary>
/// Three cases, not four. There is deliberately no Validation member: a
/// malformed request never reaches a handler, so no handler can return one
/// (§10.5). 401, 403, 409 and 412 are absent for the same reason — each is
/// decided by a mechanism that runs before or beside the handler, and a member
/// here would put two producers on one status.
/// </summary>
public enum ErrorType { NotFound, Rule, Unavailable }
