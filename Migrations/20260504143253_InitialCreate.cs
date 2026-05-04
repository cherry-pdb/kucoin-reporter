using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KuCoinFuturesReporter.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClosedPositions",
                columns: table => new
                {
                    CloseId = table.Column<string>(type: "text", nullable: false),
                    Symbol = table.Column<string>(type: "text", nullable: false),
                    SettleCurrency = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    MarginMode = table.Column<string>(type: "text", nullable: false),
                    PositionSide = table.Column<string>(type: "text", nullable: true),
                    Leverage = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    Pnl = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    RealisedGrossCost = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    TradeFee = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    FundingFee = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    OpenPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    ClosePrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                    Roe = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    OpenTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CloseTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TelegramSent = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TelegramSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClosedPositions", x => x.CloseId);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    LastCloseTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClosedPositions_CloseTime",
                table: "ClosedPositions",
                column: "CloseTime");

            migrationBuilder.CreateIndex(
                name: "IX_ClosedPositions_TelegramSent",
                table: "ClosedPositions",
                column: "TelegramSent");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClosedPositions");

            migrationBuilder.DropTable(
                name: "SyncStates");
        }
    }
}
