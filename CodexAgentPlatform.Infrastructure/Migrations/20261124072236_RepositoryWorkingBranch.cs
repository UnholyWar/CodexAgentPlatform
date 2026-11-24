using Microsoft.EntityFrameworkCore.Migrations;

namespace CodexAgentPlatform.Infrastructure.Migrations;

public partial class RepositoryWorkingBranch : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "WorkingBranch", table: "Resources", type: "TEXT", nullable: true);
        migrationBuilder.Sql("""
            UPDATE Resources SET WorkingBranch = (SELECT DefaultBranch FROM Projects WHERE Id = Resources.ProjectId)
            WHERE Kind = 0 AND IsGlobal = 0;
            """);
        migrationBuilder.Sql("ALTER TABLE Projects DROP COLUMN DefaultBranch");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "DefaultBranch", table: "Projects", type: "TEXT", nullable: false, defaultValue: "main");
        migrationBuilder.Sql("""
            UPDATE Projects SET DefaultBranch = COALESCE((SELECT WorkingBranch FROM Resources
                WHERE ProjectId = Projects.Id AND Kind = 0 AND IsGlobal = 0 AND WorkingBranch IS NOT NULL
                ORDER BY Id LIMIT 1), 'main');
            """);
        migrationBuilder.Sql("ALTER TABLE Resources DROP COLUMN WorkingBranch");
    }
}
