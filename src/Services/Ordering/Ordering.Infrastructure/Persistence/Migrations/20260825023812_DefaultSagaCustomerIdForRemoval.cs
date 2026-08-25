using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// The expand half of removing the saga's <c>CustomerId</c>
/// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/63">#63</see>,
/// ADR-028). The instance stopped declaring the property in this release; the
/// column stays, defaulted, and a later release drops it. Emitted by
/// <c>dotnet ef migrations add</c> from
/// <see cref="OrderFulfilmentStateConfiguration"/>; only the file's dress is
/// hand-authored (file-scoped namespace, this comment), and the
/// <c>.Designer.cs</c> and snapshot beside it are machine-owned.
/// </summary>
/// <remarks>
/// <b>The obvious migration here is <c>DROP COLUMN</c>, and §15.5 forbids
/// it.</b> Migrations run ahead of the deploy and the previous release keeps
/// serving beside the new one, so every migration must be backward compatible
/// with the build it stands next to (§7.4). That build's saga writes this
/// column on every <c>OrderPlaced</c>; dropping it would fail those inserts
/// for the length of the ladder, and a rollback would find no column at all.
/// So this release makes the column safe to stop writing, and the drop is the
/// contract half, owed to a release where nothing writes it.
/// <para>
/// <b>The default is what makes it safe in <em>both</em> directions, and only
/// one of them is the ordinary rollout.</b> Rolling forward, the new build's
/// generated <c>INSERT</c> does not name the column and SQL Server supplies
/// the default. Rolling back is the direction that decides the column's
/// nullability: the old build materialises a non-nullable <c>Guid</c> from
/// rows the new build wrote, so a nullable column would throw on
/// materialisation rather than merely read empty. <c>NOT NULL</c> with a
/// default is the one shape that survives both.
/// </para>
/// <para>
/// <b><c>Guid.Empty</c> is the conservative value rather than merely a legal
/// one</b>, on <c>AddSagaPaymentVerdictJoin</c>'s terms one release back. It
/// is nobody. A rolled-back build reading it would send an
/// <c>AuthorisePayment</c> naming no customer, which is the failure the old
/// release's receiver can see; any other default would name a real subject
/// that was never this order's, which is the failure nothing can see and the
/// one ADR-028 exists to remove.
/// </para>
/// <para>
/// <b>No backfill, because there is nothing to derive it from and nothing to
/// derive it for.</b> Existing rows keep the customer they were written with;
/// no code reads them. The column is inert from this release on, and the row
/// it sits in is deleted when the saga finalises.
/// </para>
/// </remarks>
public partial class DefaultSagaCustomerIdForRemoval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "CustomerId",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "uniqueidentifier",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uniqueidentifier");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "CustomerId",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "uniqueidentifier",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uniqueidentifier",
            oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
    }
}
