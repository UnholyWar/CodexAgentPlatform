using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CodexAgentPlatform.Infrastructure.Migrations;

public partial class ChatAutoApproval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("AutoApprove", "Conversations", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>("AutoEligible", "Approvals", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>("IsGlobal", "Resources", type: "INTEGER", nullable: false, defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AutoApprove", "Conversations");
        migrationBuilder.DropColumn("AutoEligible", "Approvals");
        migrationBuilder.DropColumn("IsGlobal", "Resources");
    }
}
