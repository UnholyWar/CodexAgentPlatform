using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexAgentPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ResumeAfterOperationApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResumesRunId",
                table: "Runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Approvals_ContinuationRunId",
                table: "Approvals",
                column: "ContinuationRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Approvals_ContinuationRunId",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "ResumesRunId",
                table: "Runs");
        }
    }
}
