using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §9.5's table, mapped here rather than beside the entity, for the reason
/// <see cref="OutboxMessageConfiguration"/> gives one file over: the entity is a
/// building block and knows no schema, the schema is this service's, and this
/// assembly is where the <c>ApplyConfigurationsFromAssembly</c> scan looks.
/// </summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages", "catalog");

        // The composite key of §9.5, and the second column is the whole point:
        // one service may bind the same type on two endpoints — a normal queue
        // and a bulk/replay one — and each must process the message
        // independently. MessageId alone would let whichever finished first
        // suppress the other.
        builder.HasKey(m => new { m.MessageId, m.Endpoint });

        // varchar, not nvarchar, and this is the opposite call to the outbox's
        // MessageType column one file over — deliberately. That column holds a
        // type's FullName, which C# permits to be Unicode, so narrowing it
        // would let the domain's language decide whether a message could be
        // delivered. This one holds a queue address: MassTransit composes it
        // from the endpoint name, and a RabbitMQ queue name is ASCII by the
        // transport's own rules rather than by assumption.
        //
        // 300 is §9.5's width, and it is generous on purpose — the value is a
        // path, so a virtual host prefixes the queue name on any broker
        // configured with one.
        builder
            .Property(m => m.Endpoint)
            .HasMaxLength(300)
            .IsUnicode(false)

            // Binary collation, because this column is half a key rather than
            // text. SQL Server's default is case-insensitive, and a broker's
            // queue names are not: `orders` and `Orders` are two queues, and
            // under the default collation the second one's row collides with
            // the first's. The message is then dropped as a duplicate on an
            // endpoint that never saw it — the exact once-per-endpoint
            // guarantee the composite key exists to provide, defeated by the
            // column's comparison semantics rather than by its contents.
            //
            // BIN2 rather than a CS_AS collation: an endpoint address is an
            // identifier to be matched exactly, and linguistic comparison has
            // no meaning over it. Accents and width would be the same argument
            // one rule further on.
            .UseCollation("Latin1_General_BIN2");

        // The purge's predicate (§9.5). Non-covering and non-filtered, unlike
        // the outbox's: there is no unprocessed subset to narrow to here — every
        // row is handled by construction — and the delete needs the key, which
        // it already has from the clustered primary key above.
        builder
            .HasIndex(m => m.HandledAt)
            .HasDatabaseName("IX_Inbox_HandledAt");
    }
}
