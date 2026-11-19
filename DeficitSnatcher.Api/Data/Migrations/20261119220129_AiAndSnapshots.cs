using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiAndSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAiFixAt",
                table: "Sites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UnreadableCount",
                table: "Sites",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AiUsed",
                table: "Purchases",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AiCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SiteId = table.Column<int>(type: "integer", nullable: true),
                    PurchaseId = table.Column<int>(type: "integer", nullable: true),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Summary = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiCalls_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScanSnapshots",
                columns: table => new
                {
                    ScanId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Screenshot = table.Column<byte[]>(type: "bytea", nullable: true),
                    Source = table.Column<byte[]>(type: "bytea", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanSnapshots", x => x.ScanId);
                    table.ForeignKey(
                        name: "FK_ScanSnapshots_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCalls_At",
                table: "AiCalls",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_AiCalls_SiteId_At",
                table: "AiCalls",
                columns: new[] { "SiteId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiCalls");

            migrationBuilder.DropTable(
                name: "ScanSnapshots");

            migrationBuilder.DropColumn(
                name: "LastAiFixAt",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "UnreadableCount",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "AiUsed",
                table: "Purchases");
        }
    }
}
