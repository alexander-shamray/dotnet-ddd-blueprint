namespace Common.Application;

/// <summary>
/// The caller behind the current operation, as a port — a handler must not see
/// <c>HttpContext</c> (§11.4). Resource-level checks ("is this the customer's
/// own order?") ask this rather than the request, which is §11.4's subject
/// rule: a subject identifier is bound from the principal, never from a field
/// any authenticated caller can set.
/// </summary>
/// <remarks>
/// Common rather than per-service, and the chapter was amended to match.
/// §11.4 writes <c>Ordering.Application</c> for the same reason §9.4 used to
/// write <c>ordering.OutboxMessages</c> — it is Ordering's viewpoint — and
/// nothing in these three members names a service. This is §9.5's
/// <c>InboxFilter</c> argument one layer up.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>
    /// Whether an authenticated caller is behind this operation. Presence of a
    /// principal is not the question and never was: an anonymous HTTP request
    /// carries one, holding no authenticated identity, and this answers false
    /// for it exactly as it does for a message-borne command (§9.4) that has no
    /// principal at all. Both are "no caller", which is what a guard here needs
    /// to mean if it is to be a security boundary rather than a null check.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The subject identifier, and the only source of "whose record is this".
    /// Throws when there is no authenticated caller, or when one carries no
    /// subject claim, rather than answering <see cref="Guid.Empty"/>: an empty
    /// subject silently compares unequal to every real one, which turns a
    /// missing guard into a refusal nobody can explain. Guard with
    /// <see cref="IsAuthenticated"/>, or state the origin on the command.
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Whether the caller carries a permission. The endpoint policies read the
    /// same claim (§11.4), so a policy and a resource check can never disagree
    /// about what a permission is.
    /// </summary>
    bool HasPermission(string permission);
}
