using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexAgentPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatProjectHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoordinatorAgentId",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuationRunId",
                table: "Approvals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Voice",
                table: "Agents",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoordinatorAgentId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ContinuationRunId",
                table: "Approvals");

            migrationBuilder.DropColumn(
                name: "Voice",
                table: "Agents");
        }
    }
}
