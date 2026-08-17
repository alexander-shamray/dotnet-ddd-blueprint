using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// §6.4's local price projection — a read model rather than a write one, and
/// the only table here that no aggregate owns. The DDL is exactly what
/// <c>dotnet ef migrations add</c> produced from
/// <see cref="ProductPriceConfiguration"/>; only the file's dress is
/// hand-authored (file-scoped namespace, this comment), on
/// <c>InitialCreate</c>'s terms, and the <c>.Designer.cs</c> and the snapshot
/// are machine-owned and untouched.
/// </summary>
/// <remarks>
/// <b>The table ships empty and stays empty until PR-20.</b> This PR delivers
/// its one reader (<see cref="ProjectedPriceReader"/>, which
/// <c>PlaceOrderHandler</c> depends on); the projection that fills it from
/// Catalog's events is PR-20's, and PR-20 depends on this PR rather than the
/// other way round. Shipping the schema with the consumer is what lets the
/// slice be tested — §12.4's handler tests seed rows directly.
/// <para>
/// <b><c>UpdatedAt</c> is §6.6's name and the reason this migration was
/// regenerated.</b> It emitted as <c>LastSeenAt</c> first, which reads better
/// and would have cost PR-20 a debugging session: that PR is specified to copy
/// §6.6's <c>MERGE</c> verbatim, and the statement names <c>UpdatedAt</c>. A
/// table one PR writes and the next one reads is the last place to exercise a
/// private preference over the chapter's spelling.
/// </para>
/// <para>
/// The composite key is <c>(ProductId, Currency)</c> because that is exactly
/// what the reader's <c>WHERE</c> seeks on, and <c>UpdatedAt</c> exists before
/// its first reader: it is the out-of-order guard §6.6 requires, and a
/// projection cannot add the column in the same change that starts relying on
/// it.
/// </para>
/// </remarks>
public partial class AddProductPrices : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProductPrices",
            schema: "ordering",
            columns: table => new
            {
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                IsAvailable = table.Column<bool>(type: "bit", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProductPrices", x => new { x.ProductId, x.Currency });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ProductPrices",
            schema: "ordering");
    }
}
