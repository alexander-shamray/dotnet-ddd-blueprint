using Microsoft.EntityFrameworkCore.Migrations;

namespace Catalog.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.5's inbox table, generated from <see cref="InboxMessageConfiguration"/>
/// on <c>AddOutbox</c>'s terms — the configuration is the source of truth and
/// only this file's dress is hand-authored (file-scoped namespace, this
/// comment). The <c>.Designer.cs</c> and the snapshot beside it are
/// machine-owned and untouched.
/// </summary>
/// <remarks>
/// <b>The table ships to every service, including the ones that consume
/// nothing.</b> Catalog binds no receive endpoint yet (§3.2 gives it one
/// Consumes cell, owned by a service that does not exist), so nothing writes a
/// row here — but <c>RetentionPurgeService</c> runs from first boot and purges
/// both tables, and a purge against a table that is not there logs a failure
/// every pass. That is the same argument that keeps <c>AddOutbox</c> in the
/// scaffold's output, inverted: the dispatcher would fail a claim, this would
/// fail a delete.
/// </remarks>
public partial class AddInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InboxMessages",
            schema: "catalog",
            columns: table => new
            {
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Endpoint = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                HandledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InboxMessages", x => new { x.MessageId, x.Endpoint });
            });

        migrationBuilder.CreateIndex(
            name: "IX_Inbox_HandledAt",
            schema: "catalog",
            table: "InboxMessages",
            column: "HandledAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "InboxMessages",
            schema: "catalog");
    }
}
