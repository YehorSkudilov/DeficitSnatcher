using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MultiRunner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Worker",
                table: "Scans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Worker",
                table: "Purchases",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RunnerKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Machine = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Browser = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RunnerLogins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunnerId = table.Column<int>(type: "integer", nullable: false),
                    Domain = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunnerLogins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunnerLogins_RunnerKeys_RunnerId",
                        column: x => x.RunnerId,
                        principalTable: "RunnerKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Scans_Worker_At",
                table: "Scans",
                columns: new[] { "Worker", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_RunnerKeys_KeyHash",
                table: "RunnerKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunnerLogins_RunnerId_Domain",
                table: "RunnerLogins",
                columns: new[] { "RunnerId", "Domain" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunnerLogins");

            migrationBuilder.DropTable(
                name: "RunnerKeys");

            migrationBuilder.DropIndex(
                name: "IX_Scans_Worker_At",
                table: "Scans");

            migrationBuilder.DropColumn(
                name: "Worker",
                table: "Scans");

            migrationBuilder.DropColumn(
                name: "Worker",
                table: "Purchases");
        }
    }
}
