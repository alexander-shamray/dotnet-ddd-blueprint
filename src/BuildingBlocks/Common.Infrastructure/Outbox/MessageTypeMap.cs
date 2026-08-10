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
    private readonly FrozenDictionary<string, Type> _byName;
    private readonly FrozenDictionary<Type, string> _byType;

    public MessageTypeMap(IEnumerable<Assembly> assemblies)
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
                .Where(t => t is { IsClass: true, IsAbstract: false } &&
                    (t.IsAssignableTo(typeof(IIntegrationEvent)) ||
                        t.IsAssignableTo(typeof(IDomainEvent))))
                .Select(t => (Name: t.FullName!, Type: t))
        ];

        IGrouping<string, (string Name, Type Type)>? clash =
            pairs.GroupBy(p => p.Name).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new InvalidOperationException(
                $"Two staged types share the name '{clash.Key}'. The outbox " +
                "column cannot distinguish them.");

        _byName = pairs.ToFrozenDictionary(p => p.Name, p => p.Type);
        _byType = pairs.ToFrozenDictionary(p => p.Type, p => p.Name);
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
                "before deleting a message type (§7.4).");
}
