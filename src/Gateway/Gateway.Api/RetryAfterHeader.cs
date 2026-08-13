namespace Gateway.Api;

/// <summary>
/// The <c>Retry-After</c> value §10.3's rejection handler sends, in the whole
/// seconds RFC 9110 defines the delay form as.
/// </summary>
/// <remarks>
/// <para>
/// <b>A type for one expression, and the expression is why.</b> The obvious
/// spelling is <c>(int)remaining.TotalSeconds</c>, which truncates: a lease
/// with 0.8 s left advertises <c>Retry-After: 0</c>, and zero is not a lost
/// fraction but an instruction — it sends a well-behaved client straight back
/// into a limiter that is still refusing. Rounding up is the only direction
/// that cannot name a time at which the request still fails.
/// </para>
/// <para>
/// That sentence named the *corrected* form as the buggy one for one commit,
/// and the cause is worth more than the typo: the red-first check that proved
/// this type has teeth reverted it with a blind text replace, which rewrote
/// the prose describing the defect along with the code implementing it. A
/// mechanical revert edits comments too.
/// </para>
/// <para>
/// <b>Inline, that rule is close to untestable, which is the whole argument
/// for lifting it out.</b> The window this gateway rejects against is a
/// minute long, so a request refused anywhere but its final fraction of a
/// second carries tens of seconds and rounds identically either way — the
/// suite's own 429 assertions passed with the truncating form, and said so in
/// a comment claiming the opposite until this was measured. Reaching the
/// truncating case through HTTP means holding a window open for
/// fifty-nine seconds to land inside the last one. Here it is three rows of a
/// theory.
/// </para>
/// </remarks>
public static class RetryAfterHeader
{
    /// <summary>
    /// <paramref name="remaining"/> as whole seconds, rounded up, never below
    /// zero — a negative lease would otherwise emit a header no client can
    /// parse.
    /// </summary>
    public static int Seconds(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalSeconds);
}
