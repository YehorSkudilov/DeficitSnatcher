using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePcHelper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Store sign-ins (emails and passwords for the PC helpers) aren't used any more: not kept.
            migrationBuilder.Sql("DELETE FROM \"StoredValues\" WHERE \"Key\" = 'store-logins';");
            migrationBuilder.DropTable(
                name: "RunnerLogins");

            migrationBuilder.DropTable(
                name: "RunnerKeys");

            migrationBuilder.DropColumn(
                name: "RunOnPc",
                table: "Sites");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RunOnPc",
                table: "Sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RunnerKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Browser = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ChecksEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DisabledDomains = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Machine = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
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
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Domain = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RunnerId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
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
    }
}
