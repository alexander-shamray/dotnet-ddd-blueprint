using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// The schedule token for §9.6's <c>AwaitingConfirmation</c> wait (#126).
/// Emitted by <c>dotnet ef migrations add</c> from
/// <see cref="OrderFulfilmentStateConfiguration"/>; only the file's dress is
/// hand-authored (file-scoped namespace, this comment), and the
/// <c>.Designer.cs</c> and snapshot beside it are machine-owned.
/// </summary>
/// <remarks>
/// <b>One nullable column, which is what makes it safe under §15.5.</b>
/// Migrations run ahead of the deploy, so the previous release serves traffic
/// against this schema — and a nullable add is invisible to it: the old
/// machine never writes the column, and EF's generated <c>INSERT</c> for the
/// old model does not name it. The reverse direction is the one that costs,
/// which is why <c>Down</c> is a drop rather than anything cleverer.
/// <para>
/// <b>No backfill, and the absence is a decision.</b> An instance already
/// parked in the old <c>Confirmed</c> state when this deploys has no
/// confirmation wait to have a token for, and inventing one would arm a
/// timeout for an order that already passed the point it guards. Those
/// instances keep their <c>DespatchTimeout</c> and finalise on the branch they
/// were written by; the column stays NULL for them for good, exactly as
/// <c>CancelReason</c> does for a saga that never compensates.
/// </para>
/// </remarks>
public partial class AddSagaConfirmationTimeout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ConfirmationTimeoutTokenId",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "uniqueidentifier",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ConfirmationTimeoutTokenId",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
