using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeficitSnatcher.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovedPageKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Unlocker, BrightData and Camoufox page kinds are gone (the app's browser is Settings → App browser): any page
            // set to one is read in the app's browser.
            migrationBuilder.Sql("UPDATE \"Sites\" SET \"Kind\" = 'Browser' WHERE \"Kind\" IN ('Unlocker', 'BrightData', 'Camoufox');");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
