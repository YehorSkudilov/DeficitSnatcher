using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PcHelper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RunOnPc",
                table: "Sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunOnPc",
                table: "Sites");
        }
    }
}
