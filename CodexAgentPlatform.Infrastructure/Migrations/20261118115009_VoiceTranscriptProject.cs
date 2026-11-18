using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexAgentPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VoiceTranscriptProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VoiceProjectId",
                table: "Messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VoiceProjectId",
                table: "Messages");
        }
    }
}
