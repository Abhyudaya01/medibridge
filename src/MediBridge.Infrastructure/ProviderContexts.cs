using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediBridge.Infrastructure;

public sealed class SqliteClaimsDbContext(DbContextOptions<SqliteClaimsDbContext> options) : ClaimsDbContext(options);
public sealed class SqlClaimsDbContext(DbContextOptions<SqlClaimsDbContext> options) : ClaimsDbContext(options);

public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteClaimsDbContext>
{
    public SqliteClaimsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqliteClaimsDbContext>()
        .UseSqlite("Data Source=design-only.db").Options);
}
public sealed class SqlDesignFactory : IDesignTimeDbContextFactory<SqlClaimsDbContext>
{
    public SqlClaimsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqlClaimsDbContext>()
        .UseSqlServer("Server=localhost;Database=DesignOnly;Integrated Security=true;TrustServerCertificate=true").Options);
}
