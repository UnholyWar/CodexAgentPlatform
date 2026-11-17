using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodexAgentPlatform.Infrastructure;

public sealed class PlatformDbDesignFactory : IDesignTimeDbContextFactory<PlatformDb>
{
    public PlatformDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PlatformDb>().UseSqlite("Data Source=agents.design.db").Options);
}
