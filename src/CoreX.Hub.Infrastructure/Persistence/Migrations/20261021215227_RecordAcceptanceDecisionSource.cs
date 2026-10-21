using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreX.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecordAcceptanceDecisionSource : Migration
    {
        /// <inheritdoc />
        // ADR-0028: a decision now says whether a person took it or a repository's autopilot
        // did. The default exists only because SQLite needs one to add a NOT NULL column, and
        // it is the true answer for every row that can already exist: until this migration the
        // autopilot did not, so every recorded decision was a person's.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "AcceptanceDecisions",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Person");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "AcceptanceDecisions");
        }
    }
}
