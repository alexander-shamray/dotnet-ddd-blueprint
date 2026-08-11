using System.Reflection;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// The assemblies whose events may be staged. Mutable and resolved before the
/// map, so a test host can add its own without replacing the registration —
/// the production assemblies are always in the list (§4.2).
/// </summary>
/// <remarks>
/// This is what lets §12.4's outbox suite stage a poison row, a healthy row
/// and a row for an event with no handler, all through the real
/// <see cref="MessageTypeMap"/>. A test double for the map would let a test
/// stage a type the running host cannot resolve, which is the one thing those
/// builders exist to prove does not happen.
/// </remarks>
public sealed class MessageTypeSource(params Assembly[] assemblies)
{
    private readonly List<Assembly> _assemblies = [.. assemblies];
    private readonly Dictionary<string, Type> _aliases = [];
    private readonly Dictionary<Type, string> _written = [];

    public IEnumerable<Assembly> Assemblies => _assemblies;

    public MessageTypeSource Add(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }

    /// <summary>
    /// A name a type answered to before it was renamed, so both resolve to it
    /// for one release.
    /// </summary>
    /// <remarks>
    /// <b>§9.4's rename procedure needs this and nothing else provided it.</b>
    /// "Deploy the rename in one release with both names resolving to the same
    /// type, drain, then remove the old name in the next" — and the map derives
    /// only the current <c>FullName</c>, so without an alias that first release
    /// is not expressible. During a rolling deploy the old instances go on
    /// staging the old name while the new dispatcher resolves nothing, and the
    /// rows abandon after ten attempts: the procedure the chapter documents as
    /// the safe one is the one that loses messages.
    /// <para>
    /// Aliases resolve <em>inward</em> only. <c>NameOf</c> keeps writing the
    /// current name, so the old one drains and never returns — which is what
    /// makes the second release a deletion rather than a migration of its own.
    /// </para>
    /// </remarks>
    public MessageTypeSource Alias(string persistedName, Type type)
    {
        _aliases.Add(persistedName, type);
        return this;
    }

    /// <summary>
    /// Keeps writing a type's previous persisted name, so rows this instance
    /// stages stay readable by instances that have not been replaced yet.
    /// </summary>
    /// <remarks>
    /// <b><see cref="Alias"/> alone does not make a rolling rename safe, and
    /// saying it did was wrong.</b> An alias is inward: it lets a new instance
    /// resolve the old name. It does nothing about the other direction, which
    /// is live for exactly as long — a new instance writes the new name
    /// immediately, and the old instances sharing the table cannot resolve it,
    /// so their dispatchers burn the attempt cap on rows that are perfectly
    /// good. Half the deployment window, in the opposite direction, silently.
    /// <para>
    /// The pair makes it expand and contract, over three releases:
    /// </para>
    /// <list type="number">
    /// <item>Alias the new name to the type and <see cref="WriteAs"/> the old
    /// one. Every instance resolves both and writes what every instance can
    /// read.</item>
    /// <item>Drop the <see cref="WriteAs"/>. The new name is written; release
    /// one's instances still resolve it through their alias.</item>
    /// <item>Drop the <see cref="Alias"/>, once no unprocessed row names the
    /// old one.</item>
    /// </list>
    /// </remarks>
    public MessageTypeSource WriteAs(Type type, string persistedName)
    {
        _written.Add(type, persistedName);
        return this;
    }

    public IReadOnlyDictionary<string, Type> Aliases => _aliases;

    public IReadOnlyDictionary<Type, string> WrittenNames => _written;
}
