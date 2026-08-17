using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// The aggregate's two tables — §7.4's write-model kind, so the DDL below is
/// exactly what <c>dotnet ef migrations add</c> produced from
/// <see cref="OrderConfiguration"/> and <see cref="OrderLineConfiguration"/>:
/// the configuration is the source of truth and duplicating its shape by hand
/// would create two definitions that drift. Only the file's dress is
/// hand-authored (file-scoped namespace, this comment), on InitialCreate's
/// terms; the <c>.Designer.cs</c> and the snapshot are machine-owned and
/// untouched.
/// </summary>
/// <remarks>
/// <c>OrderLines.OrderId</c> is <c>nullable: false</c>, and that is the one
/// column worth checking by eye. EF infers an optional relationship unless the
/// configuration says <c>IsRequired</c>, and an optional one emits a nullable
/// foreign key — a schema that accepts a line belonging to no order, which is
/// the aggregate boundary failing in the only place the domain cannot defend
/// it. The first generated run had it nullable.
/// </remarks>
public partial class AddOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Orders",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                PlacedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                ShipToCity = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                ShipToCountry = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                ShipToLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                ShipToLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                ShipToPostalCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Orders", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OrderLines",
            schema: "ordering",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Quantity = table.Column<int>(type: "int", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UnitPriceAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                UnitPriceCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderLines", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderLines_Orders_OrderId",
                    column: x => x.OrderId,
                    principalSchema: "ordering",
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OrderLines_OrderId",
            schema: "ordering",
            table: "OrderLines",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_Orders_CustomerId",
            schema: "ordering",
            table: "Orders",
            column: "CustomerId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OrderLines",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "Orders",
            schema: "ordering");
    }
}
