using System.Text.RegularExpressions;

namespace Common.Infrastructure;

/// <summary>
/// The one place a schema is checked and delimited. Two registered values need
/// it — <see cref="Outbox.OutboxTable"/> and <see cref="Inbox.InboxTable"/> —
/// and a second copy of the pattern is a second answer to "what is a legal
/// schema here", which is not a question that gets to have two.
/// </summary>
/// <remarks>
/// <b>This is the only identifier in the codebase interpolated into SQL rather
/// than parameterised</b>, because a schema cannot be a parameter — and a value
/// that cannot be a parameter has to be a value the type refuses to hold
/// wrongly. Both callers validate at construction, so a bad schema fails the
/// host at registration rather than the first statement composed from it.
/// </remarks>
internal static partial class SqlSchema
{
    /// <summary>
    /// The schema-qualified, delimited name of a fixed table in
    /// <paramref name="schema"/>, or an <see cref="ArgumentException"/> naming
    /// the parameter the caller took it from.
    /// </summary>
    public static string Qualify(string schema, string table, string paramName)
    {
        if (!Identifier().IsMatch(schema))
            throw new ArgumentException(
                $"'{schema}' is not a SQL identifier, and the schema is interpolated " +
                "into this service's messaging statements rather than parameterised.",
                paramName);

        // Delimited, because the pattern above admits reserved words and the
        // scaffold admits a service called `User`: `FROM user.OutboxMessages`
        // is not a schema reference SQL Server can parse. Brackets rather than
        // a keyword blacklist — the reserved list grows with each release, and
        // a delimiter is right for every name at once.
        //
        // Nothing has to be escaped inside them. `]` is the only character that
        // would need doubling, and the pattern has already refused everything
        // but letters, digits and underscore. The table name is a literal
        // supplied by the two types in this assembly and never by a caller.
        return $"[{schema}].{table}";
    }

    // Bounded at 128, which is what `sysname` holds: a longer schema
    // constructs happily here and then fails every statement composed from it
    // at runtime. One leading character plus 127 more. The scaffold already
    // refuses a service name past this limit, and a value it lets through must
    // not fail deeper in.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex Identifier();
}
