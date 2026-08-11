using System.Collections.Frozen;
using System.Reflection;
using Common.Contracts;
using Common.Domain;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// Two-way map between a stageable type and its persisted name. Built from
/// <see cref="MessageTypeSource"/>, so it cannot list a name for a type that
/// no longer exists — and, being a singleton built at startup, a duplicate
/// name fails the host rather than the first message.
/// </summary>
/// <remarks>
/// <b>The type name is a persisted contract</b>, which makes the obvious
/// implementation — <c>AssemblyQualifiedName</c> out, <c>Type.GetType</c> back
/// — wrong in a way that only shows in production. Every row would carry the
/// assembly version that staged it; bump it, which a release pipeline does
/// automatically, and <c>Type.GetType</c> returns null for every row written
/// before the deploy. The dispatcher then exhausts its attempts on a batch of
/// perfectly good messages and abandons them. Nothing is lost, nothing is
/// delivered, and the only symptom is outbox depth climbing after a release
/// that looked clean. Trimming, single-file publish and moving a type between
/// assemblies break it the same way.
/// </remarks>
public sealed class MessageTypeMap
{
    /// <summary>
    /// The widest name the <c>MessageType</c> column holds, and the reason
    /// this constant lives here rather than beside the EF configuration that
    /// spells it: the map is what decides a type is stageable, so the map is
    /// what has to refuse a name the column cannot keep. Defined once and
    /// read by both.
    /// </summary>
    public const int MaxNameLength = 300;

    private readonly FrozenDictionary<string, Type> _byName;
    private readonly FrozenDictionary<Type, string> _byType;

    public MessageTypeMap(IEnumerable<Assembly> assemblies)
        : this(assemblies, new Dictionary<string, Type>())
    {
    }

    public MessageTypeMap(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, Type> aliases)
        : this(assemblies, aliases, new Dictionary<Type, string>())
    {
    }

    public MessageTypeMap(
        IEnumerable<Assembly> assemblies,
        IReadOnlyDictionary<string, Type> aliases,
        IReadOnlyDictionary<Type, string> writtenNames)
    {
        // FullName, not AssemblyQualifiedName: namespace and type name, no
        // version and no assembly. For contracts the namespace is already
        // versioned (§9.2), so this IS the contract. For domain events it is
        // internal, and a rename is then a migration the team chose rather
        // than one a build number made for it.
        (string Name, Type Type)[] pairs =
        [
            .. assemblies
                .SelectMany(a => a.GetTypes())
                // Not `IsClass`: neither interface carries a class
                // constraint, so a `readonly record struct` domain event
                // compiles, raises and dispatches like any other — and an
                // `IsClass` filter dropped it here in silence, leaving NameOf
                // to throw inside the transaction that staged it. A map that
                // excludes a type the rest of the API accepts is a trap, so
                // this admits every concrete implementation and lets the
                // duplicate-name check below judge them all alike.
                .Where(t => t is { IsAbstract: false, IsInterface: false } &&
                    (t.IsAssignableTo(typeof(IIntegrationEvent)) ||
                        t.IsAssignableTo(typeof(IDomainEvent))))
                .Select(t => (Name: t.FullName!, Type: t))
        ];

        // Checked at startup, where MessageTypeMapValidator resolves the map,
        // rather than at SaveChanges. A deep namespace with nested generic
        // arguments passes every other guard and then fails the insert on a
        // truncation error — the command lost, the row never written, and the
        // cause named nowhere. `StageableDomainEvents` must not report a type
        // that cannot actually be persisted.
        //
        // A loop, not FirstOrDefault: the sequence is of value tuples, so
        // "no match" comes back as (null, null) rather than as null, and a
        // nullable wrapper around it is never null. The guard then fired on
        // every map and dereferenced the null name — caught immediately,
        // because the fixture builds a real host.
        foreach ((string Name, Type Type) pair in pairs)
        {
            if (pair.Name.Length > MaxNameLength)
                throw new InvalidOperationException(
                    $"{pair.Type.Name}'s persisted name is {pair.Name.Length} characters and the " +
                    $"outbox column holds {MaxNameLength}. Shorten the namespace, or move the type.");
        }

        IGrouping<string, (string Name, Type Type)>? clash =
            pairs.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new InvalidOperationException(
                $"Two staged types share the name '{clash.Key}'. The outbox " +
                "column cannot distinguish them.");

        // Aliases resolve inward only: _byName carries them so a row written
        // before a rename still resolves, and _byType does not, so NameOf goes
        // on writing the current name and the old one drains away.
        foreach ((string Name, Type Type) alias in aliases.Select(a => (a.Key, a.Value)))
        {
            if (pairs.Any(p => p.Name == alias.Name))
                throw new InvalidOperationException(
                    $"'{alias.Name}' is an alias and also a live type name. One of them resolves " +
                    "and which is not decidable — rename the alias or drop it.");

            // The target has to be a type this map already carries. An alias
            // onto anything else is a name that resolves to a type Stage
            // would refuse, and the dispatcher trusts the row's Lane rather
            // than re-deriving it — so an old Broker name aliased onto a
            // domain event would publish that domain event, which is the leak
            // Stage's guards exist to close, reopened through the alias door.
            if (!pairs.Any(p => p.Type == alias.Type))
                throw new InvalidOperationException(
                    $"'{alias.Name}' aliases {alias.Type.Name}, which this map does not carry. An " +
                    "alias names a type that is still stageable — one that is not is a row nobody " +
                    "can deliver and a guard nobody applies.");
        }

        _byName = pairs
            .Select(p => (p.Name, p.Type))
            .Concat(aliases.Select(a => (Name: a.Key, Type: a.Value)))
            .ToFrozenDictionary(p => p.Name, p => p.Type);

        // An overridden name must be one this map can read back. Writing a
        // name nothing resolves is the failure the override exists to prevent,
        // pointed the other way.
        foreach ((Type Type, string Name) written in writtenNames.Select(w => (w.Key, w.Value)))
        {
            if (!_byName.ContainsKey(written.Name))
                throw new InvalidOperationException(
                    $"{written.Type.Name} is written as '{written.Name}', which this map cannot " +
                    "resolve. Alias that name to the type in the same release, or the rows this " +
                    "instance stages are rows it cannot itself deliver.");
        }

        _byType = pairs.ToFrozenDictionary(
            p => p.Type,
            p => writtenNames.TryGetValue(p.Type, out string? written) ? written : p.Name);
    }

    /// <summary>
    /// Fails when something unstageable is staged — in the transaction, so
    /// the command fails rather than the outbox filling with rows nobody can
    /// deliver.
    /// </summary>
    public string NameOf(Type type) =>
        _byType.TryGetValue(type, out string? name) ? name
            : throw new InvalidOperationException(
                $"{type.Name} is not a stageable message type. Staging it would " +
                "write a row the dispatcher cannot resolve.");

    /// <summary>The Local lane's payload types — §12.4 round-trips each.</summary>
    public IEnumerable<Type> StageableDomainEvents =>
        _byType.Keys.Where(t => t.IsAssignableTo(typeof(IDomainEvent)));

    /// <summary>
    /// Fails on the dispatcher, where the message that names a departed type
    /// is the one that lands in the retry log with its own name in it.
    /// </summary>
    public Type Resolve(string name) =>
        _byName.TryGetValue(name, out Type? type) ? type
            : throw new InvalidOperationException(
                $"Unknown message type '{name}'. A type was renamed or removed " +
                "while rows naming it were still unprocessed — drain the outbox " +
                "before deleting a message type (§9.4).");
}
