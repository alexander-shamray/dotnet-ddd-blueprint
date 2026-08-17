using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// §6.4's local price projection — a read model rather than a write one, and
/// the only table here that no aggregate owns. Emitted by
/// <c>dotnet ef migrations add</c> from <see cref="ProductPriceConfiguration"/>;
/// only the file's dress is hand-authored (file-scoped namespace, this
/// comment), and the <c>.Designer.cs</c> and snapshot are machine-owned.
/// </summary>
/// <remarks>
/// <b>The table ships empty and stays empty until PR-20</b>, which fills it
/// from Catalog's events and depends on this PR rather than the other way
/// round. Shipping the schema with its one reader
/// (<see cref="ProjectedPriceReader"/>) is what lets the slice be tested.
/// <para>
/// <b>Every column matches §6.6's printed DDL, and two of them only after
/// being asked to.</b> §7.4 files read models under hand-written DDL because
/// they are shaped for queries rather than for objects — this one is an EF
/// configuration so the schema stays the migration's business, which means the
/// configuration has to emit the chapter's types rather than EF's defaults for
/// the CLR ones. <c>Currency</c> is <c>char(3)</c> via <c>IsFixedLength</c> +
/// <c>IsUnicode(false)</c>, not <c>nvarchar(3)</c>; <c>IsAvailable</c> carries
/// §6.6's <c>DEFAULT 1</c>. <c>UpdatedAt</c> is the chapter's name too, and it
/// shipped as <c>LastSeenAt</c> until Grok round 4 — PR-20 copies §6.6's
/// <c>MERGE</c> verbatim, so a table this PR creates and that one writes is
/// the last place to hold two schemas.
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
                Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                IsAvailable = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
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
