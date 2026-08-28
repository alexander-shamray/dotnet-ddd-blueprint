using Common.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §8.5's durable marker, mapped here rather than beside the entity, for the
/// reason <see cref="InboxMessageConfiguration"/> gives one file over: the
/// entity is a building block and knows no schema, the schema is this
/// service's, and this assembly is where the
/// <c>ApplyConfigurationsFromAssembly</c> scan looks.
/// </summary>
internal sealed class IdempotencyMarkerConfiguration : IEntityTypeConfiguration<IdempotencyMarker>
{
    public void Configure(EntityTypeBuilder<IdempotencyMarker> builder)
    {
        builder.ToTable("IdempotencyMarkers", "catalog");

        // The key alone, because §8.5 already put everything that
        // distinguishes an attempt inside it — {subject}:{operation}:{commandId}.
        // The inbox's composite key exists because one service may bind one
        // message type on two endpoints; there is no second axis here, and a
        // column that distinguishes nothing is what that entity's own comment
        // warns against.
        builder.HasKey(marker => marker.Key);

        builder
            .Property(marker => marker.Key)

            // 450, which is exactly SQL Server's 900-byte limit for a clustered
            // index key at two bytes a character — the widest this column can
            // be while the primary key above stays clustered. The key needs 74
            // characters for its two GUIDs and two separators, so what the
            // width really bounds is the declared operation name, and 376
            // characters is past any name a service would write. A per-service
            // gate asserts the ones it declares fit, because the alternative is
            // SQL Server truncating a key and refusing the insert on the first
            // dispatch of a command nobody tested with a long name.
            .HasMaxLength(450)

            // nvarchar for the inbox's reason one file over: this column is a
            // key, and narrowing a key column lets an encoding decide whether
            // two commands are the same command. The operation segment is a
            // developer-chosen string and the subject segment is a principal's
            // identity; neither is promised to be ASCII by anything.
            .UseCollation("Latin1_General_BIN2");

        // The purge's predicate (§8.5). Non-covering and non-filtered, like the
        // inbox's and for the same reason: every row here records work that
        // committed, so there is no unfinished subset to narrow to, and the
        // delete already has the key from the clustered primary key above.
        builder
            .HasIndex(marker => marker.CommittedAt)
            .HasDatabaseName("IX_Idempotency_CommittedAt");
    }
}
