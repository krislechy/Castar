using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Castar.DAL;

public sealed class CastarDbContextFactory : IDesignTimeDbContextFactory<CastarDbContext>
{
    public CastarDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<CastarDbContext>()
            .UseSqlite("Data Source=castar.design.sqlite;Foreign Keys=True")
            .Options);
}
