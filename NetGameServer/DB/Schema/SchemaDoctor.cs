using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DB.Schema
{
    /// <summary>
    /// 启动期数据库表结构医生：在迁移应用后，校验实际表/列/唯一索引是否与 EF 模型一致，并自动修复可安全修复的漂移：
    /// - 表缺失（被删除）  → 从模型生成 CREATE TABLE 自动重建（含主键与索引）。
    /// - 列缺失           → ALTER TABLE ADD COLUMN 自动补列（按模型类型；非空标量列给安全默认值，TEXT 列按 NULL 补并告警）。
    /// - 列类型不一致      → 输出明确 WARNING 诊断，不自动改动（避免数据风险）。
    /// - 唯一索引缺失      → 自动补齐（保障唯一性约束，如 Users.Account / Users.UniqueId）。
    /// </summary>
    public static class SchemaDoctor
    {
        private sealed class ColumnRow
        {
            public string? TableName { get; set; }
            public string? ColumnName { get; set; }
            public string? ColumnType { get; set; }
        }

        private sealed class IndexRow
        {
            public string? TableName { get; set; }
            public string? IndexName { get; set; }
            public int NonUnique { get; set; }
            public string? ColumnName { get; set; }
            public int SeqInIndex { get; set; }
        }

        public static async Task VerifyAndRepairAsync(DefaultDbContext dbContext)
        {
            // 期望的表（来自模型，去重）
            var expectedTables = dbContext.Model.GetEntityTypes()
                .Select(e => e.GetTableName())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existingTables = await QueryTablesAsync(dbContext);
            var (existingColumns, existingColumnTypes) = await QueryColumnsAsync(dbContext);
            var existingUniqueIndexes = await QueryUniqueIndexesAsync(dbContext);

            int createdTables = 0, addedColumns = 0, addedIndexes = 0;

            // 1) 缺失表 → 从模型重建（重建后刷新结构快照，避免对新建表重复补列）
            var missingTables = expectedTables
                .Where(t => !existingTables.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (missingTables.Count > 0)
            {
                Shared.Log.Warning($"SchemaDoctor: 检测到 {missingTables.Count} 张表被删除/缺失，自动重建: {string.Join(", ", missingTables)}");
                createdTables = await RecreateMissingTablesAsync(dbContext, missingTables);
                (existingColumns, existingColumnTypes) = await QueryColumnsAsync(dbContext);
                existingUniqueIndexes = await QueryUniqueIndexesAsync(dbContext);
            }

            // 2) 逐表校验列与唯一索引
            foreach (var tableName in expectedTables)
            {
                var entityTypes = dbContext.Model.GetEntityTypes()
                    .Where(e => string.Equals(e.GetTableName(), tableName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (!existingColumns.TryGetValue(tableName, out var actualCols))
                {
                    actualCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                if (!existingColumnTypes.TryGetValue(tableName, out var actualColTypes))
                {
                    actualColTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (var property in entityTypes.SelectMany(e => e.GetProperties()))
                {
                    var columnName = property.GetColumnName();
                    if (string.IsNullOrWhiteSpace(columnName)) continue;

                    if (!actualCols.Contains(columnName))
                    {
                        // 列缺失 → 自动补列
                        var ddl = BuildAddColumnSql(tableName, columnName, property);
                        try
                        {
                            await dbContext.Database.ExecuteSqlRawAsync(ddl);
                            addedColumns++;
                            Shared.Log.Warning($"SchemaDoctor: 表 {tableName} 缺少列 {columnName}，已自动补列: {ddl}");
                        }
                        catch (Exception ex)
                        {
                            Shared.Log.Error($"SchemaDoctor: 表 {tableName} 补列 {columnName} 失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        // 列类型不一致 → 只诊断，不自动改
                        var expectedType = property.GetRelationalTypeMapping()?.StoreType;
                        if (!string.IsNullOrWhiteSpace(expectedType)
                            && actualColTypes.TryGetValue(columnName, out var actualType)
                            && !string.IsNullOrWhiteSpace(actualType)
                            && !string.Equals(BaseType(expectedType), BaseType(actualType), StringComparison.OrdinalIgnoreCase))
                        {
                            Shared.Log.Warning(
                                $"SchemaDoctor: 表 {tableName}.{columnName} 列类型不一致：模型期望 {expectedType}，实际 {actualType}。" +
                                "存在数据风险，未自动修改，请人工核对后处理。");
                        }
                    }
                }

                // 唯一索引校验（按索引名去重，避免共享表模型重复处理）
                var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var index in entityTypes.SelectMany(e => e.GetIndexes()).Where(i => i.IsUnique))
                {
                    var indexName = index.GetDatabaseName() ?? string.Empty;
                    if (!processed.Add(indexName)) continue;

                    var cols = index.Properties
                        .Select(p => p.GetColumnName())
                        .Where(c => !string.IsNullOrWhiteSpace(c))
                        .ToArray();
                    if (cols.Length == 0) continue;

                    var modelColSet = cols.Select(c => c.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                    bool exists = existingUniqueIndexes.TryGetValue(tableName, out var sets)
                                  && sets.Any(set => set.SetEquals(modelColSet));
                    if (exists) continue;

                    var addName = string.IsNullOrWhiteSpace(indexName)
                        ? "IX_" + string.Join("_", cols)
                        : indexName;
                    var ddl = $"ALTER TABLE {Quote(tableName)} ADD UNIQUE INDEX {Quote(addName)} ({string.Join(", ", cols.Select(Quote))})";
                    try
                    {
                        await dbContext.Database.ExecuteSqlRawAsync(ddl);
                        addedIndexes++;
                        Shared.Log.Warning($"SchemaDoctor: 表 {tableName} 缺少唯一索引 {addName}，已自动补齐: {ddl}");
                    }
                    catch (Exception ex)
                    {
                        Shared.Log.Error(
                            $"SchemaDoctor: 表 {tableName} 补唯一索引 {addName} 失败: {ex.Message}（可能已存在同名索引或表内有重复数据，需人工处理）");
                    }
                }
            }

            Shared.Log.Info($"SchemaDoctor 校验完成：重建表 {createdTables} 张，补列 {addedColumns} 个，补唯一索引 {addedIndexes} 个。");
        }

        // ---------- 结构查询 ----------

        private static async Task<HashSet<string>> QueryTablesAsync(DefaultDbContext dbContext)
        {
            var names = await dbContext.Database.SqlQueryRaw<string>(
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE()").ToListAsync();
            return new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        }

        private static async Task<(Dictionary<string, HashSet<string>> Names, Dictionary<string, Dictionary<string, string>> Types)> QueryColumnsAsync(DefaultDbContext dbContext)
        {
            var rows = await dbContext.Database.SqlQueryRaw<ColumnRow>(
                "SELECT TABLE_NAME AS TableName, COLUMN_NAME AS ColumnName, COLUMN_TYPE AS ColumnType " +
                "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE()").ToListAsync();

            var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var types = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.TableName) || string.IsNullOrWhiteSpace(r.ColumnName)) continue;
                if (!names.TryGetValue(r.TableName, out var set))
                {
                    names[r.TableName] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
                set.Add(r.ColumnName);

                if (!types.TryGetValue(r.TableName, out var typeMap))
                {
                    types[r.TableName] = typeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                typeMap[r.ColumnName] = r.ColumnType ?? string.Empty;
            }
            return (names, types);
        }

        private static async Task<Dictionary<string, List<HashSet<string>>>> QueryUniqueIndexesAsync(DefaultDbContext dbContext)
        {
            var rows = await dbContext.Database.SqlQueryRaw<IndexRow>(
                "SELECT TABLE_NAME AS TableName, INDEX_NAME AS IndexName, NON_UNIQUE AS NonUnique, " +
                "COLUMN_NAME AS ColumnName, SEQ_IN_INDEX AS SeqInIndex " +
                "FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE()").ToListAsync();

            var result = new Dictionary<string, List<HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in rows
                         .Where(r => r.NonUnique == 0 && !string.IsNullOrWhiteSpace(r.TableName))
                         .GroupBy(r => (Table: r.TableName!, Index: r.IndexName ?? string.Empty)))
            {
                var cols = g.OrderBy(r => r.SeqInIndex)
                    .Select(r => r.ColumnName)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => c!.ToLowerInvariant())
                    .ToHashSet(StringComparer.Ordinal);
                if (cols.Count == 0) continue;

                if (!result.TryGetValue(g.Key.Table, out var list))
                {
                    result[g.Key.Table] = list = new List<HashSet<string>>();
                }
                list.Add(cols);
            }
            return result;
        }

        // ---------- 修复 ----------

        private static async Task<int> RecreateMissingTablesAsync(DefaultDbContext dbContext, IReadOnlyList<string> missingTables)
        {
            // 用模型差异器从空库 → 当前模型的差异生成建表操作（等价于 EnsureCreated 的建表逻辑），
            // 再按缺失表过滤，避免影响已存在的表。
            var modelDiffer = dbContext.GetService<IMigrationsModelDiffer>();
            // 注意：必须使用设计期模型（IDesignTimeModel），运行时只读模型缺少表级配置，
            // 会导致 MySqlMigrationsModelDiffer 抛 "configuration is not stored in the read-optimized model"。
            var designTimeRelationalModel = dbContext.GetService<IDesignTimeModel>().Model.GetRelationalModel();
            // 差异器会把索引生成为独立的 CreateIndexOperation（建表操作里不含非主键索引），
            // 因此重建表时需一并带上该表的建表 + 建主键 + 建索引操作。
            var ops = modelDiffer.GetDifferences(null, designTimeRelationalModel)
                .Where(o => (o is CreateTableOperation ct && missingTables.Contains(ct.Name, StringComparer.OrdinalIgnoreCase))
                         || (o is CreateIndexOperation ci && missingTables.Contains(ci.Table, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            if (ops.Count == 0) return 0;

            var sqlGenerator = dbContext.GetService<IMigrationsSqlGenerator>();
            var commands = sqlGenerator.Generate(ops, dbContext.Model);

            // 统计口径：按被重建的表计数（而非执行的 SQL 命令数）
            int createdTables = ops.OfType<CreateTableOperation>()
                .Select(o => o.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            foreach (var cmd in commands)
            {
                if (string.IsNullOrWhiteSpace(cmd.CommandText)) continue;
                try
                {
                    await dbContext.Database.ExecuteSqlRawAsync(cmd.CommandText);
                    Shared.Log.Info($"SchemaDoctor: 已重建表（{cmd.CommandText.Split('\n')[0].Trim()}）");
                }
                catch (Exception ex)
                {
                    Shared.Log.Error($"SchemaDoctor: 重建表失败: {ex.Message}");
                }
            }
            return createdTables;
        }

        private static string BuildAddColumnSql(string tableName, string columnName, IProperty property)
        {
            var storeType = property.GetRelationalTypeMapping()?.StoreType ?? "longtext";
            var baseType = BaseType(storeType).ToLowerInvariant();
            var isTextBlob = baseType is "longtext" or "text" or "mediumtext" or "tinytext"
                or "blob" or "mediumblob" or "longblob" or "tinyblob";

            string nullability;
            if (property.IsNullable || isTextBlob)
            {
                // TEXT/BLOB 列不允许 DEFAULT；非空 TEXT 列只能先按 NULL 补，再提示人工修正
                nullability = "NULL";
                if (!property.IsNullable)
                {
                    Shared.Log.Warning(
                        $"SchemaDoctor: 列 {tableName}.{columnName} 模型为 NOT NULL 但属 TEXT 类型（不支持默认值），" +
                        "已按 NULL 补列；如需严格 NOT NULL 请清空该表后手动 ALTER。");
                }
            }
            else
            {
                var defaultValue = baseType switch
                {
                    "tinyint" => "0",
                    "bit" => "b'0'",
                    "int" => "0",
                    "bigint" => "0",
                    "datetime" => "'2000-01-01 00:00:00'",
                    "timestamp" => "'2000-01-01 00:00:00'",
                    "date" => "'2000-01-01'",
                    _ => "''"
                };
                nullability = $"NOT NULL DEFAULT {defaultValue}";
            }

            return $"ALTER TABLE {Quote(tableName)} ADD COLUMN {Quote(columnName)} {storeType} {nullability}";
        }

        // ---------- 工具 ----------

        private static string BaseType(string storeType)
        {
            var s = storeType.Trim();
            int p = s.IndexOf('(');
            return p >= 0 ? s[..p] : s;
        }

        private static string Quote(string name) => "`" + name.Replace("`", "``") + "`";
    }
}
