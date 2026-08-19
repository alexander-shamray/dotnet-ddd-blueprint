using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>
/// §9.6's two tables: the fulfilment saga's instance store and the operations
/// queue its escalations land in. Generated from
/// <see cref="OrderFulfilmentStateConfiguration"/> and
/// <see cref="OrderReviewConfiguration"/> — the configurations are the source
/// of truth and only this file's dress is hand-authored (file-scoped
/// namespace, this comment, the field CA1861 asks for). The
/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and
/// untouched.
/// </summary>
/// <remarks>
/// <b>Two tables in one migration because they arrive together and neither has
/// a reader without the other.</b> A saga that finalises on a despatch timeout
/// writes an <c>OrderReviews</c> row on the way out, so shipping the instance
/// store alone would give the escalation path a table it cannot write to — and
/// §9.6's own rule is that a wait with no compensation escalates rather than
/// having no bound.
/// <para>
/// Both indexes are the chapter's and both back an alert rather than a query
/// this service runs: §13.6 asks for unfinalised sagas by age and outstanding
/// reviews by age, and without these two each of those is a table scan on
/// whatever schedule the alert runs at.
/// </para>
/// </remarks>
public partial class AddFulfilmentSaga : Migration
{
    // A field rather than the generated `new[] { … }` argument, which is what
    // CA1861 asks for and the only shape available: Annotation takes an
    // object, so a collection expression has no target type to convert to.
    // The same line AddOutbox carries, for the same reason.
    private static readonly string[] IncludedColumns = ["CurrentState"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderFulfilmentStates",
            schema: "ordering",
            columns: table => new
            {
                CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CurrentState = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Total = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                CancelReason = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: true),
                StockTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                PaymentTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DespatchTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                ReleaseTimeoutTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderFulfilmentStates", x => x.CorrelationId);
            });

        migrationBuilder.CreateTable(
            name: "OrderReviews",
            schema: "ordering",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Reason = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderReviews", x => new { x.OrderId, x.Reason });
            });

        migrationBuilder.CreateIndex(
            name: "IX_OrderFulfilmentStates_StartedAt",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            column: "StartedAt")
            .Annotation("SqlServer:Include", IncludedColumns);

        migrationBuilder.CreateIndex(
            name: "IX_OrderReviews_RaisedAt",
            schema: "ordering",
            table: "OrderReviews",
            column: "RaisedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OrderFulfilmentStates",
            schema: "ordering");

        migrationBuilder.DropTable(
            name: "OrderReviews",
            schema: "ordering");
    }
}
