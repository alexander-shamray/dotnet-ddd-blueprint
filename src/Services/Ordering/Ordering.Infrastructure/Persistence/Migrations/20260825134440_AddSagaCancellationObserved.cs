using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// The fact §9.6's four early-release doors record: a cancellation is in
/// flight that this instance has not been told about directly
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/143">#143</see>).
/// Emitted by <c>dotnet ef migrations add</c> from
/// <see cref="OrderFulfilmentStateConfiguration"/>; only the file's dress is
/// hand-authored (file-scoped namespace, this comment), and the
/// <c>.Designer.cs</c> and snapshot beside it are machine-owned.
/// </summary>
/// <remarks>
/// <b>NOT NULL with a default, on the argument the verdict join beside it
/// makes.</b> Migrations run ahead of the deploy (ADR-007), the previous
/// release's generated <c>INSERT</c> does not name this column, and SQL Server
/// supplies <c>0</c> — so that release keeps serving against this schema
/// without knowing the column exists.
/// <para>
/// <b><c>0</c> is the conservative value here in the same sense and by a
/// different route.</b> It means "no cancellation seen", which is what every
/// instance written by the previous release actually knew: that release
/// absorbed an early <c>StockReleased</c> with <c>Ignore</c> and kept nothing.
/// So the default is not merely legal, it is true of every row it lands on —
/// and the guards it feeds withhold a forward step, so a spurious <c>1</c>
/// would strand an instance rather than merely mislead it.
/// </para>
/// <para>
/// <b>No backfill, and the reason is the same one that made the column
/// necessary.</b> Whether a parked instance ever saw an early release is not
/// recoverable from the row: the arrival was discarded, and nothing else on
/// the instance records it. Those instances keep the unguarded forward
/// transitions they were written by, which is exactly what <c>0</c>
/// reproduces.
/// </para>
/// </remarks>
public partial class AddSagaCancellationObserved : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "CancellationObserved",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CancellationObserved",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
