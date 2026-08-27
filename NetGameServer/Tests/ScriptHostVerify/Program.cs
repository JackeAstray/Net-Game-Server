using Framework.Scripting;
using EntityObj = Framework.Entity.Entity;

// 准备脚本目录（csx 通过 Link 复制到输出目录的 scripts 子目录）
string scriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
Directory.CreateDirectory(scriptsDir);

// 实体定义（与脚本中 Avatar 一致）
var avatarDef = new Framework.Entity.EntityDef { Name = "Avatar" }
    .Add("Nickname", Framework.Entity.EntityPropertyType.String)
    .Add("Position", Framework.Entity.EntityPropertyType.Float3)
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
    .Add("MaxHp", Framework.Entity.EntityPropertyType.Int32)
    .Add("Score", Framework.Entity.EntityPropertyType.Int32);

var manager = new Framework.Entity.EntityManager();
var avatar = avatarDef.CreateEntity(1001);
manager.AddOrUpdateEntity(1001, avatar);

// 1. 启动脚本宿主（加载 GameLogic/scripts/*.csx）
string scriptsDir0 = scriptsDir;
var host = new ScriptHost(scriptsDir);
host.Start();

var script = host.GetScript("Avatar");
Console.WriteLine($"脚本加载: Avatar={script != null} (期望 True)");
if (script == null) return 1;

// 2. 实体创建事件
host.NotifyCreate(avatar);
if (avatar.Get<int>("Hp") != 100) { Console.WriteLine("!! OnCreate 未设置 Hp"); return 1; }

// 3. 消息分发（TakeDamage）
bool handled = host.DispatchMessage(avatar, "TakeDamage", new object?[] { 30 });
Console.WriteLine($"脚本消息: handled={handled} Hp={avatar.Get<int>("Hp")} (期望 True/70)");
if (!handled || avatar.Get<int>("Hp") != 70) return 1;

// 3.5 全局共享数据（对标 KBE KBEngine.globalData）：框架设置伤害倍率 → 脚本读取生效
host.SetGlobal("DamageMultiplier", 2);
bool handled2 = host.DispatchMessage(avatar, "TakeDamage", new object?[] { 10 });
Console.WriteLine($"全局数据: Hp={avatar.Get<int>("Hp")} (期望 50 = 70 - 10x2)");
if (avatar.Get<int>("Hp") != 50) return 1;
host.SetGlobal("DamageMultiplier", null); // 清除，恢复默认倍率

// 4. tick 驱动（模拟 TickEngine 驱动脚本 OnTick，20 tick 回血 1；当前 Hp=50）
for (long f = 1; f <= 40; f++)
{
    host.TickAll(manager, f);
}
int hpAfterTicks = avatar.Get<int>("Hp");
Console.WriteLine($"脚本 tick: 40 tick 后 Hp={hpAfterTicks} (期望 52，从 50 起每 20 tick +1)");
if (hpAfterTicks != 52) return 1;

// 5. 热更新验证：修改脚本文件，等待重新加载
string scriptFile = Path.Combine(scriptsDir, "Avatar.csx");
string original = File.ReadAllText(scriptFile);
string modified = original.Replace("entity.Set(\"Hp\", Math.Max(0, hp));", "entity.Set(\"Hp\", Math.Max(0, hp) + 1000);");
File.WriteAllText(scriptFile, modified);
await Task.Delay(1500); // 等待文件监听 + 防抖 + 重编译

// 重新获取脚本实例（热更新后应为新实例）并验证新逻辑
var newScript = host.GetScript("Avatar");
Console.WriteLine($"热更新: 实例已替换={!ReferenceEquals(script, newScript)} (期望 True)");
if (ReferenceEquals(script, newScript)) return 1;

// 新实例执行 TakeDamage 应带 +1000 修正
var freshAvatar = avatarDef.CreateEntity(2002);
manager.AddOrUpdateEntity(2002, freshAvatar);
host.NotifyCreate(freshAvatar);
host.DispatchMessage(freshAvatar, "TakeDamage", new object?[] { 10 });
int newHp = freshAvatar.Get<int>("Hp");
Console.WriteLine($"热更新后新逻辑: Hp={newHp} (期望 1090 = 100-10+1000)");
if (newHp != 1090) return 1;

// 恢复原脚本
File.WriteAllText(scriptFile, original);
await Task.Delay(1500);

// 6. 错误隔离验证：写入编译错误脚本，应保留旧实例
var lastGoodScript = host.GetScript("Avatar");
string broken = original + "\nthis is not valid csharp {{{";
File.WriteAllText(scriptFile, broken);
await Task.Delay(300); // 等待 watcher 防抖窗口（期间可能触发一次重载）
host.ReloadAll();       // 确定性重新加载：broken 编译失败 → 保留旧实例
var afterBroken = host.GetScript("Avatar");
bool hasError = host.LastLoadErrors.ContainsKey("Avatar");
Console.WriteLine($"错误隔离: 保留旧实例={ReferenceEquals(lastGoodScript, afterBroken)} 记录错误={hasError} (期望 True/True)");
if (!ReferenceEquals(lastGoodScript, afterBroken) || !hasError) return 1;

// 修复脚本并确认恢复
File.WriteAllText(scriptFile, original);
await Task.Delay(300);
host.ReloadAll();
bool errorCleared = !host.LastLoadErrors.ContainsKey("Avatar");
Console.WriteLine($"错误恢复: 错误已清除={errorCleared} (期望 True)");
if (!errorCleared) return 1;

// 7. 多脚本共存验证（Avatar + Npc 同时加载，不同实体类型互不干扰）
var npcDef = new Framework.Entity.EntityDef { Name = "Npc" }
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
    .Add("MaxHp", Framework.Entity.EntityPropertyType.Int32)
    .Add("Score", Framework.Entity.EntityPropertyType.Int32)
    .Add("Position", Framework.Entity.EntityPropertyType.Float3);
var npc = npcDef.CreateEntity(3001);
manager.AddOrUpdateEntity(3001, npc);

var npcScript = host.GetScript("Npc");
Console.WriteLine($"多脚本加载: Npc={npcScript != null} (期望 True)");
if (npcScript == null) return 1;

host.NotifyCreate(npc);
if (npc.Get<int>("Hp") != 50) { Console.WriteLine("!! Npc OnCreate 未设置 Hp"); return 1; }

// tick 驱动 Npc 巡逻（位置应变化）
var posBefore = npc.Get<Framework.Entity.Float3>("Position");
for (long f = 1; f <= 60; f++)
{
    host.TickAll(manager, f);
}
var posAfter = npc.Get<Framework.Entity.Float3>("Position");
Console.WriteLine($"Npc 巡逻: PosX {posBefore.X:F1} -> {posAfter.X:F1} (期望变化)");
if (Math.Abs(posAfter.X - posBefore.X) < 0.1f) return 1;

// Npc 受击死亡 → 掉落经验写入全局数据
host.DispatchMessage(npc, "TakeDamage", new object?[] { 100 });
var expDropped = host.GetGlobal("TotalExpDropped");
Console.WriteLine($"Npc 死亡掉落: TotalExpDropped={expDropped} (期望 20)");
if (expDropped is not int exp || exp != 20) return 1;

// 全局数据在脚本间共享：Avatar 脚本也可读取（此处验证框架侧读取一致）
Console.WriteLine("多脚本共存验证 OK");

// 8. Quest 任务脚本验证（三脚本共存 + 跨脚本协作：Npc 击杀 → 全局数据 → Quest 完成）
var questDef = new Framework.Entity.EntityDef { Name = "Quest" }
    .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
    .Add("MaxHp", Framework.Entity.EntityPropertyType.Int32)
    .Add("Score", Framework.Entity.EntityPropertyType.Int32);
var quest = questDef.CreateEntity(4001);
manager.AddOrUpdateEntity(4001, quest);

var questScript = host.GetScript("Quest");
Console.WriteLine($"Quest 脚本加载: {questScript != null} (期望 True)");
if (questScript == null) return 1;

host.NotifyCreate(quest);
if (quest.Get<int>("MaxHp") != 20) { Console.WriteLine("!! Quest OnCreate 未设置目标"); return 1; }

// 模拟第二只 Npc 死亡：经验达到阈值 → Quest tick 检测到完成
host.DispatchMessage(npc, "TakeDamage", new object?[] { 100 });
// 注意：第二只 Npc 已死亡，直接再次击杀同实体无效果——改为通过全局数据直接设置（简化）
// 重新创建一只 Npc 击杀以累计经验
var npc2 = npcDef.CreateEntity(3002);
manager.AddOrUpdateEntity(3002, npc2);
host.NotifyCreate(npc2);
host.DispatchMessage(npc2, "TakeDamage", new object?[] { 100 });

// 驱动 tick 让 Quest 脚本检测到全局数据变化（5 tick 轮询 + 判定）
for (long f = 1; f <= 20; f++)
{
    host.TickAll(manager, f);
}
bool questCompleted = host.GetGlobal("QuestCompleted") is bool qc && qc;
Console.WriteLine($"Quest 完成: {questCompleted} (期望 True，经验 40>=20)");
if (!questCompleted) return 1;

// 查询进度消息
host.DispatchMessage(quest, "QueryProgress", Array.Empty<object?>());
Console.WriteLine("三脚本协作（Npc→全局数据→Quest）验证 OK");

host.Dispose();
Console.WriteLine("\n===== ScriptHost 验证通过 =====");
return 0;
