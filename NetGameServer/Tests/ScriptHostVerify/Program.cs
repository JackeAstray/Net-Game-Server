using Framework.Scripting;
using Framework.Tick;
using EntityObj = Framework.Entity.Entity;

namespace ScriptHostVerify;

/// <summary>
/// 脚本宿主 + 玩法脚本验证入口。
/// 覆盖：Avatar 回血/边界/伤害 + 全局数据 DamageMultiplier + Npc 巡逻 + Quest 事件驱动 +
/// Skill 冷却/升级 + Item 拾取/边界/自动掉落 + OnReload 热更新 + ScriptVersion + 错误隔离。
/// </summary>
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        // 启用控制台日志
        Framework.Core.Log.Configure(true, Path.Combine(AppContext.BaseDirectory, "logs", "ScriptHostVerify.log"));

        // 准备脚本目录（csx 通过 csproj 复制到输出目录的 scripts 子目录）
        string scriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
        Directory.CreateDirectory(scriptsDir);

        // 1. 启动 TickEngine（KBE-Gap-Review S2：脚本需要它来用 AddTimer）
        var tickEngine = new TickEngine(20);
        tickEngine.Start();

        // 2. 启动脚本宿主
        var host = new ScriptHost(scriptsDir);
        host.AttachTickEngine(tickEngine);  // 注入引擎，csx 才能用 AddTimer
        host.Start();

        var manager = new Framework.Entity.EntityManager();
        host.RegisterEntityManager(manager);

        int exitCode = await RunAsync(host, manager, tickEngine, scriptsDir);
        host.Dispose();
        tickEngine.Stop();
        return exitCode;
    }

    static async Task<int> RunAsync(ScriptHost host, Framework.Entity.EntityManager manager,
        TickEngine tickEngine, string scriptsDir)
    {
        // === 3. Avatar 验证：定时器回血 + 边界校验 (S1+S2+S3) ===
        var avatarDef = new Framework.Entity.EntityDef { Name = "Player" }
            .Add("Nickname", Framework.Entity.EntityPropertyType.String)
            .Add("Position", Framework.Entity.EntityPropertyType.Float3)
            .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
            .Add("MaxHp", Framework.Entity.EntityPropertyType.Int32)
            .Add("Score", Framework.Entity.EntityPropertyType.Int32);
        var avatar = avatarDef.CreateEntity(1001);
        manager.AddOrUpdateEntity(1001, avatar);

        var script = host.GetScript("Player");
        Console.WriteLine($"[1] 脚本加载: Player={script != null}, ScriptVersion={script?.ScriptVersion} (期望 True/2)");
        if (script == null || script.ScriptVersion != 2) return 1;

        host.NotifyCreate(avatar);
        if (avatar.Get<int>("Hp") != 100) { Console.WriteLine("!! OnCreate 未设置 Hp"); return 1; }

        // 受伤：MathClampAdd 钳制到 [0, MaxHp]
        host.DispatchMessage(avatar, "TakeDamage", new object?[] { 30 });
        Console.WriteLine($"[2] TakeDamage(30) Hp={avatar.Get<int>("Hp")} (期望 70)");
        if (avatar.Get<int>("Hp") != 70) return 1;

        // 边界：扣血超过当前 Hp 应该钳制到 0，不能为负
        host.DispatchMessage(avatar, "TakeDamage", new object?[] { 9999 });
        Console.WriteLine($"[3] TakeDamage(9999) Hp={avatar.Get<int>("Hp")} (期望 0，边界钳制)");
        if (avatar.Get<int>("Hp") != 0) return 1;

        // S2：定时器回血。Avatar 脚本 AddTimer(1000ms) 每秒回 1。
        // TickEngine 跑 ~1.2s（覆盖至少 1 个回血周期）
        await Task.Delay(1200);
        int hpAfterHeal = avatar.Get<int>("Hp");
        Console.WriteLine($"[4] 1.2s 后 Hp={hpAfterHeal} (期望 1，定时器回血)");
        if (hpAfterHeal < 1) return 1;

        // === 4. 全局数据 DamageMultiplier ===
        host.SetGlobal("DamageMultiplier", 2);
        var avatar2 = avatarDef.CreateEntity(1002);
        manager.AddOrUpdateEntity(1002, avatar2);
        host.NotifyCreate(avatar2);
        host.DispatchMessage(avatar2, "TakeDamage", new object?[] { 10 });
        Console.WriteLine($"[5] 全局倍率 2: Hp={avatar2.Get<int>("Hp")} (期望 80 = 100-10x2)");
        if (avatar2.Get<int>("Hp") != 80) return 1;
        host.SetGlobal("DamageMultiplier", null);

        // === 5. Npc 验证：定时器巡逻 (S2) ===
        var npcDef = new Framework.Entity.EntityDef { Name = "Npc" }
            .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
            .Add("MaxHp", Framework.Entity.EntityPropertyType.Int32)
            .Add("Score", Framework.Entity.EntityPropertyType.Int32)
            .Add("Position", Framework.Entity.EntityPropertyType.Float3);
        var npc = npcDef.CreateEntity(3001);
        manager.AddOrUpdateEntity(3001, npc);
        var npcScript = host.GetScript("Npc");
        Console.WriteLine($"[6] Npc 脚本: v{npcScript?.ScriptVersion} (期望 2)");
        if (npcScript == null || npcScript.ScriptVersion != 2) return 1;

        host.NotifyCreate(npc);
        var posBefore = npc.Get<Framework.Entity.Float3>("Position");
        await Task.Delay(800); // 0.5s 巡逻一次，800ms 至少一次
        var posAfter = npc.Get<Framework.Entity.Float3>("Position");
        Console.WriteLine($"[7] Npc 巡逻: X {posBefore.X:F1} -> {posAfter.X:F1} (期望变化)");
        if (Math.Abs(posAfter.X - posBefore.X) < 0.1f) return 1;

        // 击杀：边界校验，扣血到 0 后 Hp 钳制为 0
        host.DispatchMessage(npc, "TakeDamage", new object?[] { 9999 });
        Console.WriteLine($"[8] Npc 死亡: Hp={npc.Get<int>("Hp")} (期望 0)");
        if (npc.Get<int>("Hp") != 0) return 1;
        var expDropped = host.GetGlobal("TotalExpDropped");
        Console.WriteLine($"[9] Npc 经验掉落: {expDropped} (期望 20)");
        if (expDropped is not int exp || exp != 20) return 1;

        // === 6. Quest 验证：跨脚本事件驱动协作（原有功能未变）===
        var questDef = new Framework.Entity.EntityDef { Name = "Quest" }
            .Add("Hp", Framework.Entity.EntityPropertyType.Int32)
            .Add("MaxHp", Framework.Entity.EntityPropertyType.Int32)
            .Add("Score", Framework.Entity.EntityPropertyType.Int32);
        var quest = questDef.CreateEntity(4001);
        manager.AddOrUpdateEntity(4001, quest);
        host.NotifyCreate(quest);
        if (quest.Get<int>("MaxHp") != 20) { Console.WriteLine("!! Quest 目标未设置"); return 1; }

        var questScript = host.GetScript("Quest");
        Console.WriteLine($"[10] Quest 脚本: v{questScript?.ScriptVersion}");
        if (questScript == null || questScript.ScriptVersion != 2) return 1;

        // 累计 40 经验（>20 阈值）→ Quest 完成（事件驱动，立即触发）
        // 注：单只 Npc 死亡只掉落 20 经验（isDead 实例字段共享，避免再生 Npc 时 SetGlobal 跳过）
        //     → 先把 Quest 监听打开，再单独 SetGlobal 模拟累计
        host.SetGlobal("TotalExpDropped", 40);
        bool questCompleted = host.GetGlobal("QuestCompleted") is bool qc && qc;
        Console.WriteLine($"[11] Quest 完成（事件驱动）: {questCompleted} (期望 True)");
        if (!questCompleted) return 1;

        // === 7. Skill 验证：定时器冷却 (S2) + 边界 (S3) ===
        var skillDef = new Framework.Entity.EntityDef { Name = "Skill" }
            .Add("Level", Framework.Entity.EntityPropertyType.Int32)
            .Add("CooldownRemaining", Framework.Entity.EntityPropertyType.Int32)
            .Add("Casts", Framework.Entity.EntityPropertyType.Int32);
        var skill = skillDef.CreateEntity(5001);
        manager.AddOrUpdateEntity(5001, skill);
        var skillScript = host.GetScript("Skill");
        if (skillScript == null || skillScript.ScriptVersion != 2) { Console.WriteLine("!! Skill 脚本加载失败"); return 1; }
        host.NotifyCreate(skill);
        if (skill.Get<int>("Level") != 1) { Console.WriteLine("!! Skill 初始 Level≠1"); return 1; }

        // 首次释放：BaseDamage(10) * Level(1) * 1 = 10
        host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
        Console.WriteLine($"[12] Skill 首次释放: TotalDmg={host.GetGlobal("SkillTotalDamage")} CD={skill.Get<int>("CooldownRemaining")} (期望 10/500ms)");
        if (host.GetGlobal("SkillTotalDamage") is not int dmg1 || dmg1 != 10) return 1;
        if (skill.Get<int>("CooldownRemaining") != 500) return 1;  // CooldownMs=500

        // 冷却中：拒绝
        host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
        Console.WriteLine($"[13] Skill 冷却拒绝: TotalDmg={host.GetGlobal("SkillTotalDamage")} (期望 10 不变)");
        if (host.GetGlobal("SkillTotalDamage") is not int dmg2 || dmg2 != 10) return 1;

        // 等 600ms 冷却结束
        await Task.Delay(600);
        Console.WriteLine($"[14] 冷却结束: CD={skill.Get<int>("CooldownRemaining")} (期望 0)");
        if (skill.Get<int>("CooldownRemaining") != 0) return 1;

        // 再释放 2 次，触发升级
        host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
        await Task.Delay(600);
        host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
        await Task.Delay(50); // 让第 3 次 CastSkill 内部 Casts++ 完成升级判定
        int level = skill.Get<int>("Level");
        int casts = skill.Get<int>("Casts");
        var skillLevelG = host.GetGlobal("SkillLevel");
        Console.WriteLine($"[15] 三次释放: Level={level} Casts={casts} GlobalLevel={skillLevelG} (期望 2/0/2)");
        if (level != 2 || casts != 0 || skillLevelG is not int sl || sl != 2) return 1;

        // 升级后伤害：Lv.1→Lv.2 在第 3 次释放触发（伤害还是按当前 Level=1 算 10，升级在伤害后）
        // 所以累计 = 10 + 10 + 10 = 30（升级后 Level=2）；第 4 次伤害 = 10*2 = 20；总累计 = 50
        await Task.Delay(600);
        host.DispatchMessage(skill, "CastSkill", Array.Empty<object?>());
        var totalAfter4 = host.GetGlobal("SkillTotalDamage");
        Console.WriteLine($"[16] 升级后伤害: TotalDmg={totalAfter4} (期望 50 = 10+10+10 + 20)");
        if (totalAfter4 is not int t4 || t4 != 50) return 1;

        // === 8. Item 验证：定时器掉落 + 边界 (S2+S3) ===
        var itemDef = new Framework.Entity.EntityDef { Name = "Item" }
            .Add("ItemId", Framework.Entity.EntityPropertyType.Int32)
            .Add("Count", Framework.Entity.EntityPropertyType.Int32);
        var item = itemDef.CreateEntity(6001);
        manager.AddOrUpdateEntity(6001, item);
        var itemScript = host.GetScript("Item");
        if (itemScript == null || itemScript.ScriptVersion != 2) { Console.WriteLine("!! Item 脚本加载失败"); return 1; }
        host.NotifyCreate(item);

        // 拾取 5 个
        host.DispatchMessage(item, "Pickup", new object?[] { 101, 5 });
        Console.WriteLine($"[17] Item 拾取 5: Count={item.Get<int>("Count")} (期望 5)");
        if (item.Get<int>("Count") != 5) return 1;

        // 边界：拾取 200 个会钳制到 99
        host.DispatchMessage(item, "Pickup", new object?[] { 101, 200 });
        Console.WriteLine($"[18] Item 拾取 200 边界: Count={item.Get<int>("Count")} (期望 99 上限)");
        if (item.Get<int>("Count") != 99) return 1;

        // 使用 3 个：钳制到 [0, 99]
        for (int i = 0; i < 3; i++) host.DispatchMessage(item, "UseItem", Array.Empty<object?>());
        Console.WriteLine($"[19] Item 使用 3: Count={item.Get<int>("Count")} Healed={host.GetGlobal("ItemHealedTotal")} (期望 96/30)");
        if (item.Get<int>("Count") != 96) return 1;
        if (host.GetGlobal("ItemHealedTotal") is not int ih1 || ih1 != 30) return 1;

        // 定时器掉落：1.5s 间隔，2s 内至少 1 次
        int dropsBefore = host.GetGlobal("ItemAutoDrops") is int d0 ? d0 : 0;
        await Task.Delay(1800);
        int dropsAfter = host.GetGlobal("ItemAutoDrops") is int d1 ? d1 : 0;
        Console.WriteLine($"[20] Item 自动掉落 1.8s: Drops {dropsBefore}->{dropsAfter} Count={item.Get<int>("Count")} (期望 +1)");
        if (dropsAfter - dropsBefore < 1) return 1;

        // === 9. 热更新 S4 验证：OnReload 钩子触发 + ScriptVersion 跟踪 ===
        string scriptFile = Path.Combine(scriptsDir, "Avatar.csx");
        string original = File.ReadAllText(scriptFile);
        string modified = original.Replace("public override int ScriptVersion => 2;",
                                           "public override int ScriptVersion => 3;");
        File.WriteAllText(scriptFile, modified);
        await Task.Delay(800);
        int vNew = host.GetScript("Player")?.ScriptVersion ?? 0;
        Console.WriteLine($"[21] 热更新 ScriptVersion: {vNew} (期望 3)");
        if (vNew != 3) return 1;

        File.WriteAllText(scriptFile, original);
        await Task.Delay(800);
        int vReset = host.GetScript("Player")?.ScriptVersion ?? 0;
        Console.WriteLine($"[22] 热更新回滚 ScriptVersion: {vReset} (期望 2)");
        if (vReset != 2) return 1;

        // === 10. 错误隔离：编译失败保留旧实例 ===
        var lastGood = host.GetScript("Player");
        host.PauseWatcher();  // 暂停 watcher 防止防抖窗口竞争
        File.WriteAllText(scriptFile, original + "\nthis is not valid csharp {{{");
        host.ReloadAll();
        var afterBroken = host.GetScript("Player");
        bool hasError = host.LastLoadErrors.ContainsKey("Avatar");
        Console.WriteLine($"[23] 错误隔离: 保留旧实例={ReferenceEquals(lastGood, afterBroken)} 记录错误={hasError} (期望 True/True)");
        if (!ReferenceEquals(lastGood, afterBroken) || !hasError) return 1;

        File.WriteAllText(scriptFile, original);
        host.ReloadAll();
        host.ResumeWatcher();
        bool cleared = !host.LastLoadErrors.ContainsKey("Avatar");
        Console.WriteLine($"[24] 错误恢复: 错误已清除={cleared} (期望 True)");
        if (!cleared) return 1;

        Console.WriteLine("\n===== ScriptHost 验证通过（含 S1-S4 全部能力）=====");
        return 0;
    }
}
