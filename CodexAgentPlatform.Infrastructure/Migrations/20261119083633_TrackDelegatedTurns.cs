using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexAgentPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackDelegatedTurns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OriginMessageId",
                table: "Runs",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OriginMessageId",
                table: "Runs");
        }
    }
}
