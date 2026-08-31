using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Shared;

namespace DB
{
    /// <summary>
    /// dotnet ef 设计期工厂：供 `dotnet ef migrations add/script` 等设计期工具创建 DbContext。
    /// 与运行时使用同一连接字符串来源（ConfigHelper + appsettings.json），禁止硬编码连接串。
    /// </summary>
    public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DefaultDbContext>
    {
        public DefaultDbContext CreateDbContext(string[] args)
        {
            var connectionString = ConfigHelper.GetConfig<string>("ConnectionStrings:MySqlConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "未配置 ConnectionStrings:MySqlConnection；无法创建设计期 DbContext。" +
                    "请在 DB/appsettings.json 或环境变量 ConnectionStrings__MySqlConnection 中配置。");
            }

            var options = new DbContextOptionsBuilder<DefaultDbContext>()
                .UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
                .Options;
            return new DefaultDbContext(options);
        }
    }
}
