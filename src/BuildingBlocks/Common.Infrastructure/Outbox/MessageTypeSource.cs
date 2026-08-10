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

    public IEnumerable<Assembly> Assemblies => _assemblies;

    public MessageTypeSource Add(Assembly assembly)
    {
        _assemblies.Add(assembly);
        return this;
    }
}
