using Microsoft.EntityFrameworkCore;
using Shared.Data;

namespace DB
{
    public class DefaultDbContext : DbContext
    {
        public DefaultDbContext(DbContextOptions<DefaultDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
    }
}