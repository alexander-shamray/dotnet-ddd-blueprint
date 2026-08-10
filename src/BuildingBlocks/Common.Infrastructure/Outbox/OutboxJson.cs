using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Infrastructure.Outbox;

/// <summary>
/// The payload column is a persisted format, written by one deployment and
/// read by another — and on the <c>Local</c> lane it holds a domain event, a
/// type §5.5 describes as free to change with the code. Both statements are
/// true and together they are a trap: a member renamed between the stage and
/// the deliver silently deserialises to its default, because that is what
/// <c>System.Text.Json</c> does with a property it cannot match.
/// </summary>
/// <remarks>
/// <b>One instance, registered, and both sides resolve it.</b> Staging and
/// delivering must agree, and the way they stop agreeing is one of them
/// picking up a host-wide default that was changed for an API's benefit. A
/// static field would say the same thing about the settings and nothing about
/// the converters, which are now half of what "agree" means.
/// <para>
/// <b>The converters are how a value object reaches the payload, and this is
/// the layer that owes them.</b> §5.3's <c>Money</c> has a private constructor
/// and two get-only properties; a struct always has a parameterless
/// constructor, so <c>System.Text.Json</c> does not throw on that shape — it
/// builds the default, finds nothing to set, and returns <c>Amount = 0</c>
/// with a null <c>Currency</c>. The domain must not grow a
/// <c>[JsonConstructor]</c> to fix it: §4.2's allow-list gate names
/// <c>System.Text.Json</c> as forbidden in a domain assembly, and it is right
/// to. So the service's Infrastructure supplies a
/// <c>JsonConverter&lt;Money&gt;</c>, exactly as it already supplies the
/// <c>ComplexProperty</c> mapping that turns the same type into two columns —
/// same layer, same reason, and neither one visible from the domain.
/// </para>
/// <para>
/// §12.4's round-trip assertion is what makes the obligation checkable: a
/// value object with no converter fails it on the day it joins a domain event,
/// rather than on the day a deploy lands mid-batch.
/// </para>
/// </remarks>
public sealed class OutboxJson
{
    public OutboxJson(IEnumerable<JsonConverter> converters)
    {
        Options = new JsonSerializerOptions
        {
            // Explicitly the defaults that matter, rather than inherited ones:
            // property names as declared, numbers as numbers, no
            // case-insensitive rescue on the way back in — a payload that only
            // round-trips because matching is lenient is a payload that will
            // not survive a rename.
            PropertyNamingPolicy = null,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict
        };

        foreach (JsonConverter converter in converters)
            Options.Converters.Add(converter);

        // Frozen at construction. The instance is a singleton reached from a
        // background service and from every command scope at once, and
        // JsonSerializerOptions is only thread-safe once it is read-only —
        // otherwise the first serialisation freezes it anyway and a converter
        // added later throws from whichever thread got there second.
        //
        // populateMissingResolver, because the parameterless overload refuses
        // options that name no TypeInfoResolver: freezing one is a promise
        // that nothing more will be discovered, so the reflection-based
        // default has to be attached here rather than on first use. Nothing in
        // this solution is trimmed or published AOT, which is the only setting
        // where that default is the wrong one.
        Options.MakeReadOnly(populateMissingResolver: true);
    }

    public JsonSerializerOptions Options { get; }
}
