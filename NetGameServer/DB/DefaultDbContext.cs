using Microsoft.EntityFrameworkCore;
using Shared.Data;
using Shared.Data.Social;

namespace DB
{
    public class DefaultDbContext : DbContext
    {
        public DefaultDbContext(DbContextOptions<DefaultDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>()
                .HasIndex(u => u.UniqueId)
                .IsUnique();

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Account)
                .IsUnique();
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Friend> Friends { get; set; }
        public DbSet<Blacklist> Blacklists { get; set; }
    }
}
