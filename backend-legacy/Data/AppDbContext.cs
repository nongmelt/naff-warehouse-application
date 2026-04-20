using Microsoft.EntityFrameworkCore;

namespace backend.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<PackingListEntity> PackingLists => Set<PackingListEntity>();
}
