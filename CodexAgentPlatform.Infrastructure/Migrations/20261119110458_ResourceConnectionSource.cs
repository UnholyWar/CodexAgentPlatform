using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexAgentPlatform.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ResourceConnectionSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedByAgentId",
                table: "Resources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionSourceId",
                table: "Resources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_ConnectionSourceId",
                table: "Resources",
                column: "ConnectionSourceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Resources_Resources_ConnectionSourceId",
                table: "Resources",
                column: "ConnectionSourceId",
                principalTable: "Resources",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CreatedByAgentId", table: "Resources");

            migrationBuilder.DropForeignKey(
                name: "FK_Resources_Resources_ConnectionSourceId",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Resources_ConnectionSourceId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "ConnectionSourceId",
                table: "Resources");
        }
    }
}
