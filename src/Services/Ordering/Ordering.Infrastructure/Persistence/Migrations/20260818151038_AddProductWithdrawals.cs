using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// The product-level withdrawal watermark §6.6's price projection consults on
/// the one branch a per-row guard cannot cover. Emitted by
/// <c>dotnet ef migrations add</c> from
/// <see cref="ProductWithdrawalConfiguration"/>; only the file's dress is
/// hand-authored (file-scoped namespace, this comment), and the
/// <c>.Designer.cs</c> and snapshot are machine-owned.
/// </summary>
/// <remarks>
/// <b>Two columns, and the second is the whole reason for the table.</b>
/// <c>ordering.ProductPrices</c> is keyed <c>(ProductId, Currency)</c> and
/// <c>ProductDiscontinued</c> carries no currency (§9.1), so a withdrawal can
/// only reach the rows that already exist. §9.4 guarantees no ordering, so two
/// cases escape it — a withdrawal claimed ahead of a publish still retrying,
/// and a stale price for a currency nobody had projected — and both end with
/// the price table's <c>MERGE</c> inserting an orderable row for a product
/// Catalog has withdrawn. Both were reproduced as failing tests before this
/// table existed.
/// <para>
/// <c>WithdrawnAt</c> is a watermark rather than a flag for the same reason
/// <c>ProductPrices.UpdatedAt</c> is: a withdrawal must not make a product
/// permanently unorderable, so Catalog republishing at a later
/// <c>OccurredAt</c> re-lists it — in currencies that have rows and in
/// currencies that do not.
/// </para>
/// </remarks>
public partial class AddProductWithdrawals : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProductWithdrawals",
            schema: "ordering",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                WithdrawnAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductWithdrawals", x => x.ProductId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProductWithdrawals",
            schema: "ordering");
    }
}
