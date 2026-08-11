namespace Common.Application;

/// <summary>
/// Thrown by an <see cref="ICommandMessageMapper{TMessage, TCommand}"/> on a
/// value it cannot map — an unknown reason code, a malformed reference.
/// </summary>
/// <remarks>
/// <b>It exists to be excluded from retry.</b> §9.8's rule is that retry is for
/// faults time might fix: a broker blip, a deadlock, an expired token. A
/// malformed contract does not parse itself on the fourth attempt, so a receive
/// endpoint declares <c>r.Ignore&lt;ContractMappingException&gt;()</c> and the
/// message reaches the error queue on the first attempt instead of after a
/// minute of backoff spent holding every message behind it.
/// <para>
/// It is deliberately <em>not</em> how a domain rejection travels. A command the
/// domain refused is an answer rather than a fault — <c>CommandConsumer</c> acks
/// it, counts <c>command.domain_rejected</c> and logs it (§9.8), so the error
/// queue holds only faults and its depth alert can stay at zero.
/// </para>
/// <para>
/// The three constructors are CA1032's, and the parameterless one is the reason
/// this is a class rather than a record: an exception the framework may
/// construct needs them all.
/// </para>
/// </remarks>
public sealed class ContractMappingException : Exception
{
    public ContractMappingException()
    {
    }

    public ContractMappingException(string message)
        : base(message)
    {
    }

    public ContractMappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
