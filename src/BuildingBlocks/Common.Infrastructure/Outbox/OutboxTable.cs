using System.Text.RegularExpressions;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// Where this service's outbox lives. §9.4 writes its three statements against
/// <c>ordering.OutboxMessages</c>, which is right for a chapter about Ordering
/// and wrong for the common code every service shares — so the schema is a
/// registered value and the dispatcher composes its SQL from it.
/// </summary>
/// <remarks>
/// <b>The alternative is a dispatcher per service, which is §9.3's prohibition
/// on a second outbox table set arriving by the back door</b> — two
/// dispatchers, two retention policies, two sets of ordering guarantees, and
/// one of them being the one nobody monitors.
/// <para>
/// The table name is fixed and the schema is shape-checked, so no
/// caller-supplied text reaches SQL unvalidated. The check is not defensive
/// decoration: this is the one place in the codebase where an identifier is
/// interpolated into a statement rather than parameterised, because a schema
/// cannot be a parameter, and a value that cannot be a parameter has to be a
/// value the type refuses to hold wrongly.
/// </para>
/// </remarks>
public sealed partial class OutboxTable
{
    public OutboxTable(string schema)
    {
        if (!Identifier().IsMatch(schema))
            throw new ArgumentException(
                $"'{schema}' is not a SQL identifier, and the schema is interpolated " +
                "into the dispatcher's statements rather than parameterised.",
                nameof(schema));

        // Delimited, because the pattern above admits reserved words and the
        // scaffold admits a service called `User`: `FROM user.OutboxMessages`
        // is not a schema reference SQL Server can parse. Brackets rather than
        // a keyword blacklist — the reserved list grows with each release, and
        // a delimiter is right for every name at once.
        //
        // Nothing has to be escaped inside them. `]` is the only character
        // that would need doubling, and the pattern has already refused
        // everything but letters, digits and underscore.
        QualifiedName = $"[{schema}].OutboxMessages";
        Schema = schema;
    }

    public string Schema { get; }

    /// <summary>Schema-qualified and delimited, ready to interpolate.</summary>
    public string QualifiedName { get; }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
}
