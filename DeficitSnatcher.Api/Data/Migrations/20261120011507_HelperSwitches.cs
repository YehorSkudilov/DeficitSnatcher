using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class HelperSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ChecksEnabled",
                table: "RunnerKeys",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledDomains",
                table: "RunnerKeys",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChecksEnabled",
                table: "RunnerKeys");

            migrationBuilder.DropColumn(
                name: "DisabledDomains",
                table: "RunnerKeys");
        }
    }
}
