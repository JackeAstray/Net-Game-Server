using Framework.Scripting;
using EntityObj = Framework.Entity.Entity;

// 启用控制台日志（脚本编译错误详情输出）
Framework.Core.Log.Configure(true, Path.Combine(AppContext.BaseDirectory, "logs", "ScriptHostVerify.log"));

// 准备脚本目录（csx 通过 Link 复制到输出目录的 scripts 子目录）
string scriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
Directory.CreateDirectory(scriptsDir);

// 实体定义（与脚本中 Player 一致：Avatar.csx 现绑定 Player 实体类型，玩家加入场景即生效）
var avatarDef = new Framework.Entity.EntityDef { Name = "Player" }
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
host.RegisterEntityManager(manager); // 注册实体管理器（全局数据事件遍历用）

var script = host.GetScript("Player");
Console.WriteLine($"脚本加载: Player={script != null} (期望 True)");
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
var newScript = host.GetScript("Player");
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

// 5.5 热更新状态迁移：同一实体对象在重载前后状态保持，且新逻辑作用于保留状态
//    avatar 经 40 tick 后 Hp=52（未受新实体影响）；重载后新实例对旧实体 TakeDamage(10)
//    → Hp = 52-10=42，再 +1000 = 1042（证明状态跨热更新迁移 + 新逻辑生效）
host.DispatchMessage(avatar, "TakeDamage", new object?[] { 10 });
int migratedHp = avatar.Get<int>("Hp");
Console.WriteLine($"热更新状态迁移: 旧实体 Hp={migratedHp} (期望 1042 = 52-10+1000)");
if (migratedHp != 1042) return 1;

// 恢复原脚本
File.WriteAllText(scriptFile, original);
await Task.Delay(1500);

// 6. 错误隔离验证：写入编译错误脚本，应保留旧实例
var lastGoodScript = host.GetScript("Player");
string broken = original + "\nthis is not valid csharp {{{";
File.WriteAllText(scriptFile, broken);
await Task.Delay(300); // 等待 watcher 防抖窗口（期间可能触发一次重载）
host.ReloadAll();       // 确定性重新加载：broken 编译失败 → 保留旧实例
var afterBroken = host.GetScript("Player");
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

// 模拟第二只 Npc 死亡：经验达到阈值 → Quest 通过全局数据事件立即完成（事件驱动，无需 tick 轮询）
host.DispatchMessage(npc, "TakeDamage", new object?[] { 100 });
// 重新创建一只 Npc 击杀以累计经验
var npc2 = npcDef.CreateEntity(3002);
manager.AddOrUpdateEntity(3002, npc2);
host.NotifyCreate(npc2);
host.DispatchMessage(npc2, "TakeDamage", new object?[] { 100 });

// 事件驱动验证：Quest.OnGlobalChanged 在 SetGlobal 时立即触发，无需驱动 tick
bool questCompleted = host.GetGlobal("QuestCompleted") is bool qc && qc;
Console.WriteLine($"Quest 完成: {questCompleted} (期望 True，经验 40>=20，事件驱动)");
if (!questCompleted) return 1;

// 查询进度消息
host.DispatchMessage(quest, "QueryProgress", Array.Empty<object?>());
Console.WriteLine("三脚本协作（Npc→全局数据→Quest 事件驱动）验证 OK");

// 9. Skill 技能脚本验证（冷却管理 + 升级成长 + 全局伤害累计）
var skillDef = new Framework.Entity.EntityDef { Name = "Skill" }
    .Add("Level", Framework.Entity.EntityPropertyType.Int32)
    .Add("CooldownRemaining", Framework.Entity.EntityPropertyType.Int32)
    .Add("Casts", Framework.Entity.EntityPropertyType.Int32);
var skill = skillDef.CreateEntity(5001);
manager.AddOrUpdateEntity(5001, skill);

var skillScript = host.GetScript("Skill");
Console.WriteLine($"Skill 脚本加载: {skillScript != null} (期望 True)");
if (skillScript == null) return 1;

host.NotifyCreate(skill);
if (skill.Get<int>("Level") != 1 || skill.Get<int>("CooldownRemaining") != 0) { Console.WriteLine("!! Skill OnCreate 初始化错误"); return 1; }

// 首次释放：Lv.1 伤害 10
host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
var skillTotal1 = host.GetGlobal("SkillTotalDamage");
Console.WriteLine($"Skill 首次释放: TotalDamage={skillTotal1} CD={skill.Get<int>("CooldownRemaining")} (期望 10/10)");
if (skillTotal1 is not int st1 || st1 != 10 || skill.Get<int>("CooldownRemaining") != 10) return 1;

// 冷却中释放被拒绝
host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
var skillTotal2 = host.GetGlobal("SkillTotalDamage");
Console.WriteLine($"Skill 冷却拒绝: TotalDamage={skillTotal2} (期望 10 不变)");
if (skillTotal2 is not int st2 || st2 != 10) return 1;

// 等 10 tick 冷却归零，再释放（累计 2 次）
for (long f = 1; f <= 10; f++) host.TickAll(manager, f);
if (skill.Get<int>("CooldownRemaining") != 0) { Console.WriteLine("!! Skill 冷却未归零"); return 1; }
host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
var skillTotal2b = host.GetGlobal("SkillTotalDamage");
Console.WriteLine($"Skill 第二次释放: TotalDamage={skillTotal2b} (期望 20)");
if (skillTotal2b is not int st2b || st2b != 20) return 1;

// 再等 10 tick 冷却归零，第三次释放（累计 3 次 → CastSkill 内立即升级 Lv.2，Casts 清零）
for (long f = 1; f <= 10; f++) host.TickAll(manager, f);
host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
var skillTotal3 = host.GetGlobal("SkillTotalDamage");
int skillLevel = skill.Get<int>("Level");
int skillCasts = skill.Get<int>("Casts");
var skillLevelGlobal = host.GetGlobal("SkillLevel");
Console.WriteLine($"Skill 三次释放: TotalDamage={skillTotal3} Level={skillLevel} Casts={skillCasts} 全局SkillLevel={skillLevelGlobal} (期望 30/2/0/2，升级事件驱动)");
if (skillTotal3 is not int st3 || st3 != 30 || skillLevel != 2 || skillCasts != 0 || skillLevelGlobal is not int slg || slg != 2) return 1;

// Lv.2 释放伤害 20 → 累计 50（先等冷却归零）
for (long f = 1; f <= 10; f++) host.TickAll(manager, f);
host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
var skillTotal4 = host.GetGlobal("SkillTotalDamage");
Console.WriteLine($"Skill 升级后伤害: TotalDamage={skillTotal4} (期望 50 = 30 + 10x2)");
if (skillTotal4 is not int st4 || st4 != 50) return 1;
Console.WriteLine("Skill 技能脚本验证 OK");

// 10. Item 物品脚本验证（拾取堆叠 + 使用消耗 + 自动掉落）
var itemDef = new Framework.Entity.EntityDef { Name = "Item" }
    .Add("ItemId", Framework.Entity.EntityPropertyType.Int32)
    .Add("Count", Framework.Entity.EntityPropertyType.Int32);
var item = itemDef.CreateEntity(6001);
manager.AddOrUpdateEntity(6001, item);

var itemScript = host.GetScript("Item");
if (itemScript == null)
{
    foreach (var loadErr in host.LastLoadErrors)
    {
        Console.WriteLine($"[诊断] 脚本加载错误 {loadErr.Key}: {loadErr.Value.Message}");
        if (loadErr.Value.InnerException != null) Console.WriteLine($"[诊断] 内部: {loadErr.Value.InnerException.Message}");
    }
}
Console.WriteLine($"Item 脚本加载: {itemScript != null} (期望 True)");
if (itemScript == null) return 1;

host.NotifyCreate(item);
if (item.Get<int>("Count") != 0) { Console.WriteLine("!! Item OnCreate 初始化错误"); return 1; }

// 拾取 5 个
host.DispatchMessage(item, "Pickup", new object?[] { 101, 5 });
var picked1 = host.GetGlobal("ItemTotalPicked");
Console.WriteLine($"Item 拾取: Count={item.Get<int>("Count")} 累计拾取={picked1} (期望 5/5)");
if (item.Get<int>("Count") != 5 || picked1 is not int p1 || p1 != 5) return 1;

// 使用 3 个（每个回 10 血）
for (int i = 0; i < 3; i++) host.DispatchMessage(item, "UseItem", Array.Empty<object?>());
var healed1 = host.GetGlobal("ItemHealedTotal");
Console.WriteLine($"Item 使用: Count={item.Get<int>("Count")} 累计治疗={healed1} (期望 2/30)");
if (item.Get<int>("Count") != 2 || healed1 is not int h1 || h1 != 30) return 1;

// 30 tick 自动掉落 1 个
for (long f = 1; f <= 30; f++) host.TickAll(manager, f);
var drops1 = host.GetGlobal("ItemAutoDrops");
Console.WriteLine($"Item 自动掉落: Count={item.Get<int>("Count")} 掉落数={drops1} (期望 3/1)");
if (item.Get<int>("Count") != 3 || drops1 is not int d1 || d1 != 1) return 1;

// 再使用 1 个 → 累计治疗 40
host.DispatchMessage(item, "UseItem", Array.Empty<object?>());
var healed2 = host.GetGlobal("ItemHealedTotal");
Console.WriteLine($"Item 最终: Count={item.Get<int>("Count")} 累计治疗={healed2} (期望 2/40)");
if (item.Get<int>("Count") != 2 || healed2 is not int h2 || h2 != 40) return 1;
Console.WriteLine("Item 物品脚本验证 OK");

host.Dispose();
Console.WriteLine("\n===== ScriptHost 验证通过 =====");
return 0;
