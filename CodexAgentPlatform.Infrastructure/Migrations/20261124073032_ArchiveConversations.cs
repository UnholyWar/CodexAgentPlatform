using Microsoft.EntityFrameworkCore.Migrations;

namespace CodexAgentPlatform.Infrastructure.Migrations;

public partial class ArchiveConversations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<bool>(name: "Archived", table: "Conversations", type: "INTEGER",
            nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("ALTER TABLE Conversations DROP COLUMN Archived");
}
