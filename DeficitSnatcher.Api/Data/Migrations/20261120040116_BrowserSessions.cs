using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BrowserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrowserSessions",
                columns: table => new
                {
                    SiteId = table.Column<int>(type: "integer", nullable: false),
                    Engine = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StateEncrypted = table.Column<string>(type: "text", nullable: false),
                    SavedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrowserSessions", x => x.SiteId);
                    table.ForeignKey(
                        name: "FK_BrowserSessions_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrowserSessions");
        }
    }
}
