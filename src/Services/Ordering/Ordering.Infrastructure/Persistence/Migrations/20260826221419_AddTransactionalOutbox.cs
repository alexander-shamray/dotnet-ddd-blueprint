using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-032's transactional outbox: the three tables MassTransit's
/// <c>AddEntityFrameworkOutbox</c> needs, so §9.6's saga can commit its
/// outgoing messages with its own instance. Generated from
/// <c>modelBuilder.AddTransactionalOutboxEntities()</c> in
/// <see cref="OrderingDbContext"/> — that call is the source of truth and only
/// this file's dress is hand-authored (file-scoped namespace, this comment,
/// the fields CA1861 asks for). The <c>.Designer.cs</c> and the snapshot beside
/// it are machine-owned and untouched.
/// </summary>
/// <remarks>
/// <b>These are the second outbox table set in this schema, and the names do
/// not collide by luck.</b> MassTransit's are singular —
/// <c>ordering.OutboxMessage</c>, <c>ordering.InboxState</c>,
/// <c>ordering.OutboxState</c> — where §9.4's and §9.5's are plural
/// (<c>ordering.OutboxMessages</c>, <c>ordering.InboxMessages</c>). Both sets
/// live under the schema <c>HasDefaultSchema</c> sets, which is why a reader of
/// the database sees five tables where the chapters describe two. ADR-032
/// argues why the second set is admitted and what it costs.
/// <para>
/// <c>OutboxMessage</c> carries foreign keys to both state tables, so the
/// <c>Down</c> below drops it first. That order is the generator's and is
/// correct; it is worth noticing rather than reformatting, because the reverse
/// fails at the constraint rather than at the table.
/// </para>
/// <para>
/// No retention index is added here and none is owed. §9.4's
/// <c>RetentionPurgeService</c> does not read these tables at all, and the
/// generated set above already carries the indexes the library's own removers
/// read: <c>IX_InboxState_Delivered</c> for the hosted
/// <c>InboxCleanupService</c> that <c>AddEntityFrameworkOutbox</c> registers,
/// which removes <c>InboxState</c> rows once the duplicate-detection window has
/// elapsed and reads no other table; and
/// <c>IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber</c> for
/// the outbox middleware, which delivers a consume transaction's staged
/// messages and removes them once they reach the transport.
/// </para>
/// <para>
/// <b><c>IX_OutboxState_Created</c> serves neither, and the table under it is
/// unused here.</b> It is read by <c>BusOutboxDeliveryService</c> — the sweeper
/// behind <c>UseBusOutbox()</c>, which this platform deliberately does not
/// call (see <c>Messaging/DependencyInjection.cs</c>). Nothing writes, reads or
/// prunes <c>ordering.OutboxState</c> in this configuration; it is created
/// because <c>OutboxMessage.OutboxId</c> carries a foreign key to it and the
/// model would not build otherwise. A permanently empty
/// <c>ordering.OutboxState</c> is the design rather than a symptom.
/// </para>
/// </remarks>
public partial class AddTransactionalOutbox : Migration
{
    // Fields rather than the generated `new[] { … }` arguments, which is what
    // CA1861 asks for. The same line AddOutbox and AddFulfilmentSaga carry, for
    // the same reason.
    private static readonly string[] InboxStateKey = ["MessageId", "ConsumerId"];

    private static readonly string[] InboxDeliveryColumns =
        ["InboxMessageId", "InboxConsumerId", "SequenceNumber"];

    private static readonly string[] OutboxDeliveryColumns = ["OutboxId", "SequenceNumber"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxState",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ConsumerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LockId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Received = table.Column<DateTime>(type: "datetime2", nullable: false),
                ReceiveCount = table.Column<int>(type: "int", nullable: false),
                ExpirationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                Consumed = table.Column<DateTime>(type: "datetime2", nullable: true),
                Delivered = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxState", x => x.Id);
                table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
            });

        migrationBuilder.CreateTable(
            name: "OutboxState",
            schema: "ordering",
            columns: table => new
            {
                OutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                LockId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                Created = table.Column<DateTime>(type: "datetime2", nullable: false),
                Delivered = table.Column<DateTime>(type: "datetime2", nullable: true),
                LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
            });

        migrationBuilder.CreateTable(
            name: "OutboxMessage",
            schema: "ordering",
            columns: table => new
            {
                SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                EnqueueTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                SentTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                Headers = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                InboxMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                InboxConsumerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                OutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ContentType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                MessageType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                InitiatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                DestinationAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ResponseAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                FaultAddress = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ExpirationTime = table.Column<DateTime>(type: "datetime2", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                table.ForeignKey(
                    name: "FK_OutboxMessage_InboxState_InboxMessageId_InboxConsumerId",
                    columns: x => new { x.InboxMessageId, x.InboxConsumerId },
                    principalSchema: "ordering",
                    principalTable: "InboxState",
                    principalColumns: InboxStateKey);
                table.ForeignKey(
                    name: "FK_OutboxMessage_OutboxState_OutboxId",
                    column: x => x.OutboxId,
                    principalSchema: "ordering",
                    principalTable: "OutboxState",
                    principalColumn: "OutboxId");
            });

        migrationBuilder.CreateIndex(
            name: "IX_InboxState_Delivered",
            schema: "ordering",
            table: "InboxState",
            column: "Delivered");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_EnqueueTime",
            schema: "ordering",
            table: "OutboxMessage",
            column: "EnqueueTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_ExpirationTime",
            schema: "ordering",
            table: "OutboxMessage",
            column: "ExpirationTime");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage",
            columns: InboxDeliveryColumns,
            unique: true,
            filter: "[InboxMessageId] IS NOT NULL AND [InboxConsumerId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessage_OutboxId_SequenceNumber",
            schema: "ordering",
            table: "OutboxMessage",
            columns: OutboxDeliveryColumns,
            unique: true,
            filter: "[OutboxId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_OutboxState_Created",
            schema: "ordering",
            table: "OutboxState",
            column: "Created");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OutboxMessage",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "InboxState",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "OutboxState",
            schema: "ordering");
    }
}
