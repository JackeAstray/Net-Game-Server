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

        /// <summary>
        /// 配置实体模型，为 User 实体添加唯一索引并保留基类的配置。
        /// </summary>
        /// <remarks>为 User.UniqueId 和 User.Account 添加唯一索引；调用 base.OnModelCreating 以保留基类的模型配置。</remarks>
        /// <param name="modelBuilder">用于构建和配置实体模型的 ModelBuilder 实例。</param>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>()
                .HasIndex(u => u.UniqueId)
                .IsUnique();

            modelBuilder.Entity<User>()
                .HasIndex(u => u.Account)
                .IsUnique();

            modelBuilder.Entity<Friend>()
                .HasIndex(f => new { f.UserId, f.FriendUserId })
                .IsUnique();

            modelBuilder.Entity<Blacklist>()
                .HasIndex(b => new { b.UserId, b.BlockedUserId })
                .IsUnique();

            modelBuilder.Entity<FriendRequest>()
                .HasIndex(r => new { r.RequesterUserId, r.ReceiverUserId, r.Status });
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Friend> Friends { get; set; }
        public DbSet<Blacklist> Blacklists { get; set; }
        public DbSet<FriendRequest> FriendRequests { get; set; }
    }
}
