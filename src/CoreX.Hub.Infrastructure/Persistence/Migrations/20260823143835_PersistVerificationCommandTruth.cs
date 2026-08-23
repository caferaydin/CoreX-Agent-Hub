using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoreX.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistVerificationCommandTruth : Migration
    {
        /// <inheritdoc />
        // Restart safe machine truth: a Machine acceptance criterion names a verification
        // step and is answered long after the process that ran it exited, so the step's
        // status and whether it was required AT THE TIME have to live on the row.
        //
        // The defaults exist only because SQLite needs one to add a NOT NULL column, and
        // they are chosen so no historical row can be read as a success: "Unavailable" is
        // the domain's explicit fail-closed legacy status, it is never produced by a run,
        // and it never counts as a pass. Nothing in the table today was written by
        // production - CommandResults was mapped but never populated - so this describes a
        // case that should not exist rather than one that does.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Required",
                table: "CommandResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "CommandResults",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.CreateIndex(
                name: "UX_CommandResults_Step_Name",
                table: "CommandResults",
                columns: new[] { "ExecutionStepId", "StepName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CommandResults_Step_Name",
                table: "CommandResults");

            migrationBuilder.DropColumn(
                name: "Required",
                table: "CommandResults");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "CommandResults");
        }
    }
}
