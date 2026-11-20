using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestockEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Events",
                table: "RestockInsights",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            // Every store looked up again with the deeper search (one at a time, by the background lookup).
            migrationBuilder.Sql("UPDATE \"RestockInsights\" SET \"FetchedAt\" = '2000-01-01T00:00:00Z';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Events",
                table: "RestockInsights");
        }
    }
}
