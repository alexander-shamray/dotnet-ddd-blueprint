using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// The two facts §9.6's <c>Compensating</c> state joins on: whether Payments
/// still owes a verdict, and whether the stock half has come to rest
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/124">#124</see>).
/// Emitted by <c>dotnet ef migrations add</c> from
/// <see cref="OrderFulfilmentStateConfiguration"/>; only the file's dress is
/// hand-authored (file-scoped namespace, this comment), and the
/// <c>.Designer.cs</c> and snapshot beside it are machine-owned.
/// </summary>
/// <remarks>
/// <b>Two NOT NULL columns with a default, which is a different §15.5
/// argument from the nullable add beside it.</b> A nullable column is
/// invisible to the previous release because nothing has to supply it; these
/// are not nullable, so what makes them safe is the default rather than the
/// nullability. Migrations run ahead of the deploy, the old machine's
/// generated <c>INSERT</c> does not name either column, and SQL Server
/// supplies <c>0</c> — so the old release keeps serving against this schema
/// without knowing the columns exist.
/// <para>
/// <b>The default is the conservative value, not merely a legal one.</b>
/// <c>PaymentVerdictOutstanding = 0</c> means "nothing is owed", so an
/// instance created by the old release and compensated by the new one
/// finalises on exactly the terms the old machine would have used. The new
/// behaviour applies to sagas that pass through <c>StockReserved</c> after
/// the deploy, which are the only ones whose obligation was ever recorded.
/// A default of <c>1</c> would have been the unsafe direction: every
/// in-flight instance would then wait for a verdict nobody was going to send.
/// </para>
/// <para>
/// <b>No backfill, and it could not be derived if one were wanted.</b>
/// Whether a parked instance is owed a verdict depends on which door it
/// entered <c>Compensating</c> by, and the row records the state it reached
/// rather than the transition that took it there — the fact this migration
/// adds is precisely the one that was not being kept. Those instances keep
/// the unconditional finalise they were written by, which is the behaviour
/// the default reproduces.
/// </para>
/// </remarks>
public partial class AddSagaPaymentVerdictJoin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "PaymentVerdictOutstanding",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "StockReleaseSettled",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "bit",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PaymentVerdictOutstanding",
            schema: "ordering",
            table: "OrderFulfilmentStates");

        migrationBuilder.DropColumn(
            name: "StockReleaseSettled",
            schema: "ordering",
            table: "OrderFulfilmentStates");
    }
}
