using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DB.Schema
{
    /// <summary>
    /// 启动期数据库 Schema 迁移服务。
    /// 用 EF Core 迁移（Migrate）替代旧的 EnsureCreated，使建库建表与后续结构升级都可自动完成：
    /// - 库不存在          → 自动建库 + 应用全部迁移（建表）。
    /// - 库存在且已有迁移历史 → 应用待执行的迁移（未来加新迁移即自动升级表结构）。
    /// - 库存在但无迁移历史   → 旧版 EnsureCreated 建的库：
    ///      * 库里没有任何表 → 直接应用迁移（自动建历史表 + 建表）。
    ///      * 库里有表（历史遗留）→ 把初始迁移标记为已应用以对齐基线，再由 SchemaDoctor 校验并修复实际结构。
    /// </summary>
    public static class SchemaMigrator
    {
        public static async Task EnsureMigratedAsync(DefaultDbContext dbContext)
        {
            var creator = (RelationalDatabaseCreator)dbContext.GetService<IRelationalDatabaseCreator>();
            var historyRepo = dbContext.GetService<IHistoryRepository>();
            var migrator = dbContext.GetService<IMigrator>();
            var migrationsAssembly = dbContext.GetService<IMigrationsAssembly>();

            // 1) 数据库不存在 → 全新建库 + 建表
            if (!await creator.ExistsAsync())
            {
                Shared.Log.Info("数据库不存在，将自动创建数据库并应用所有迁移...");
                await migrator.MigrateAsync();
                return;
            }

            // 2) 数据库存在且已有迁移历史 → 应用待执行迁移
            if (await historyRepo.ExistsAsync())
            {
                Shared.Log.Info("数据库存在且已有迁移历史，应用待执行迁移...");
                await migrator.MigrateAsync();
                return;
            }

            // 3) 数据库存在、无迁移历史、但没有任何表（空库）→ 直接应用迁移建表
            if (!await creator.HasTablesAsync())
            {
                Shared.Log.Info("数据库存在但没有任何表，直接应用迁移建表...");
                await migrator.MigrateAsync();
                return;
            }

            // 4) 数据库存在、无迁移历史、有表 → 旧版 EnsureCreated 遗留库：基线对齐
            var initial = migrationsAssembly.Migrations
                .OrderBy(m => m.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (initial.Value == null)
            {
                Shared.Log.Warning("迁移程序集中没有任何迁移，跳过基线标记。");
                return;
            }

            Shared.Log.Warning(
                "检测到旧版(EnsureCreated)遗留数据库：缺少迁移历史表。将初始迁移标记为已应用以对齐基线，" +
                "随后由 SchemaDoctor 校验并自动修复实际表结构...");
            await historyRepo.CreateIfNotExistsAsync();
            var row = new HistoryRow(initial.Key, typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "9.0.0");
            await dbContext.Database.ExecuteSqlRawAsync(historyRepo.GetInsertScript(row));
            await migrator.MigrateAsync();
        }
    }
}
