using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Shared;

namespace Center;

internal static class CenterHttpServer
{
    /// <summary>当前运行的 WebApplication 实例（优雅关闭 StopAsync 用，迭代 21）。</summary>
    private static WebApplication? currentApp;

    /// <summary>
    /// 优雅停止管理台 HTTP 服务（NodeLifecycle 关闭钩子调用）。
    /// 停止 Kestrel 后，StartAsync 中的 app.RunAsync() 返回。
    /// </summary>
    public static async Task StopAsync()
    {
        var app = currentApp;
        if (app == null)
        {
            return;
        }
        Shared.Log.Info("Center HTTP 管理台正在停止...");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Shared.Log.Error(ex, "Center HTTP 管理台停止异常");
        }
        currentApp = null;
    }

    /// <summary>
    /// 启动并运行中心服务器的 ASP.NET Core Web 应用；配置 Kestrel 在配置的 HTTP 端口（默认 31316）监听，启用 Serilog，注册并映射控制器。
    /// </summary>
    /// <remarks>若配置项 CenterHttpPort 为 0 或未配置，则使用默认端口 31316。启动完成后记录运行信息并异步监听连接。</remarks>
    /// <param name="args">传递给 WebApplication 创建器的命令行参数。</param>
    /// <returns>表示应用启动并异步运行直到停止的可等待任务。</returns>
    public static async Task StartAsync(string[] args)
    {
        int httpPort = ConfigHelper.GetConfig<int>("CenterHttpPort") == 0 ? 31316 : ConfigHelper.GetConfig<int>("CenterHttpPort");
        // 安全默认：管理面仅监听回环地址，避免默认公网暴露。
        string bindAddress = ConfigHelper.GetConfig<string>("CenterHttpListenAddress") ?? "127.0.0.1";
        // B6 TLS：配置 pfx 证书 + 密码后启用 HTTPS（默认端口 = HTTP 端口 + 1）
        string tlsPfx = ConfigHelper.GetConfig<string>("CenterHttpTlsPfx") ?? string.Empty;
        string tlsPassword = ConfigHelper.GetConfig<string>("CenterHttpTlsPassword") ?? string.Empty;
        int httpsPort = ConfigHelper.GetConfig<int>("CenterHttpTlsPort") == 0 ? httpPort + 1 : ConfigHelper.GetConfig<int>("CenterHttpTlsPort");
        bool tlsEnabled = !string.IsNullOrWhiteSpace(tlsPfx) && System.IO.File.Exists(tlsPfx);

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            // P3 加固：管理面绑定地址可配置（CenterHttpListenAddress，默认 127.0.0.1 回环保持安全）。
            // 需要从外部访问时设 0.0.0.0，但建议经 TLS/防火墙保护；明文 HTTP 上承载 X-Api-Key 有被嗅探风险。
            System.Net.IPAddress bindIp;
            if (string.Equals(bindAddress, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bindAddress, "*", StringComparison.OrdinalIgnoreCase)
                || string.Equals(bindAddress, "::", StringComparison.OrdinalIgnoreCase))
            {
                bindIp = System.Net.IPAddress.Any;
                options.ListenAnyIP(httpPort);
            }
            else
            {
                bindIp = System.Net.IPAddress.Parse(bindAddress);
                options.Listen(bindIp, httpPort);
            }

            // B6：启用 HTTPS（证书文件存在才生效，缺失时保持明文并告警）
            if (tlsEnabled)
            {
                try
                {
                    var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(tlsPfx, tlsPassword);
                    options.Listen(bindIp, httpsPort, o => o.UseHttps(cert));
                    Shared.Log.Info($"Center 管理面 HTTPS 已启用: {bindAddress}:{httpsPort}（证书 {tlsPfx}）");
                }
                catch (Exception ex)
                {
                    Shared.Log.Warning($"Center 管理面 HTTPS 启用失败（回退明文）: {ex.Message}");
                }
            }
        });

        if (!string.Equals(bindAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bindAddress, "::1", StringComparison.OrdinalIgnoreCase))
        {
            Shared.Log.Warning($"Center HTTP 管理面以明文监听在 {bindAddress}:{httpPort}（X-Api-Key 经明文 HTTP 传输有被嗅探风险）。生产建议绑定 127.0.0.1 或启用 TLS/防火墙。");
        }

        builder.Host.UseSerilog();
        builder.Services.AddControllers();

        var app = builder.Build();
        currentApp = app;

        // Center 管理接口全部需要 API Key 鉴权（节点/房间/集群视图都是敏感信息）。
        // 配置项 CenterHttpApiKeys: 每行一个 Key（或逗号分隔）。
        var apiKeys = (ConfigHelper.GetConfig<string>("CenterHttpApiKeys")
            ?? ConfigHelper.GetConfig<string>("HttpApiKeys")
            ?? string.Empty)
            .Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        if (apiKeys.Length == 0)
        {
            Shared.Log.Warning(
                "Center HTTP 管理接口未配置 ApiKey（CenterHttpApiKeys/HttpApiKeys），所有 /api/center/* 将返回 401。" +
                "生产环境必须配置。");
        }
        // 管理台首页匿名放行（静态 HTML 无敏感数据，数据接口仍需 Key）；/api/center/* 全部强鉴权
        app.UseMiddleware<CenterApiKeyAuthMiddleware>(apiKeys, new[] { "/" });

        // 管理台首页（对标 KBE guiconsole 的 Web 简化版）：轮询 health/nodes/summary/rooms
        // 鉴权由 CenterApiKeyAuthMiddleware 统一完成（恒定时间比较 + 限流），此处不再重复校验，
        // 避免冗余的普通字符串比较引入计时侧信道。客户端需在请求头带 X-Api-Key。
        app.MapGet("/", (HttpContext ctx) => Results.Content(DashboardHtml, "text/html; charset=utf-8"));

        app.MapControllers();

        Shared.Log.Info($"中心服务器启动完成，等待其他服务节点接入。监控 HTTP 端口: {httpPort}");
        await app.RunAsync();
    }

    /// <summary>管理台单页（无外部依赖，5 秒自动刷新）。</summary>
    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="zh">
<head><meta charset="utf-8"><title>Net-Game-Server 管理台</title>
<style>
body{font-family:system-ui;background:#111;color:#ddd;margin:20px}
h1{font-size:20px}h2{font-size:15px;margin:8px 0}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{border:1px solid #333;padding:5px 8px;text-align:left}th{background:#222}
.ok{color:#4caf50}.bad{color:#f44336}.card{background:#181818;padding:12px;border-radius:8px;margin-bottom:14px}
#ts{font-size:12px;color:#888;font-weight:normal}
</style></head>
<body>
<h1>Net-Game-Server 管理台 <span id="ts"></span></h1>
<div class="card">API Key：<input id="apikey" type="password" style="width:340px" placeholder="输入 X-Api-Key（保存在浏览器本地）"><button onclick="saveKey()">保存</button></div>
<div class="card" id="health"></div>
<div class="card"><h2>节点趋势（最近 30 分钟）</h2><svg id="trend" width="100%" height="150" viewBox="0 0 800 150" preserveAspectRatio="none"></svg><div id="trendlegend" style="font-size:12px;color:#888"></div></div>
<div class="card"><h2>节点</h2><table><thead><tr><th>节点ID</th><th>类型</th><th>地址</th><th>负载</th><th>心跳</th><th>连接</th><th>内存</th><th>运行时长</th><th>线程</th></tr></thead><tbody id="nodes"></tbody></table></div>
<div class="card"><h2>机器/进程总览（KBE machine 化，迭代 20）</h2><table><thead><tr><th>机器ID</th><th>托管方</th><th>总节点</th><th>Battle</th><th>Game</th><th>Gateway</th><th>Login</th><th>DB</th><th>Center</th><th>在线</th></tr></thead><tbody id="machines"></tbody></table></div>
<div class="card"><h2>房间</h2><table><thead><tr><th>房间ID</th><th>名称</th><th>类型</th><th>Battle节点</th><th>人数</th><th>状态</th><th>房主</th></tr></thead><tbody id="rooms"></tbody></table></div>
<div class="card"><h2>配置中心（运行时覆盖）</h2>
<div>Key <input id="cfgkey" style="width:220px"> Value <input id="cfgval" style="width:280px" placeholder="留空保存 = 删除该键"> <button onclick="saveConfig()">保存/更新</button></div>
<table><thead><tr><th>Key</th><th>Value</th><th>操作</th></tr></thead><tbody id="cfgrows"></tbody></table>
<h2 style="margin-top:12px">变更历史与回滚</h2>
<table><thead><tr><th>版本</th><th>操作</th><th>Key</th><th>旧值</th><th>新值</th><th>时间</th><th>操作</th></tr></thead><tbody id="cfghistory"></tbody></table>
</div>
<script>
function saveKey(){
  localStorage.setItem('ngs_api_key', document.getElementById('apikey').value.trim());
  document.getElementById('ts').textContent='API Key 已保存';
}
(function(){
  document.getElementById('apikey').value = localStorage.getItem('ngs_api_key') || '';
})();
async function fetchJson(url){
  const key = localStorage.getItem('ngs_api_key') || '';
  const headers = key ? {'X-Api-Key': key} : {};
  const res = await fetch(url, {headers});
  if (!res.ok) throw new Error(url + ' HTTP ' + res.status);
  return res.json();
}
function fmtBytes(b){
  if (b >= 1024*1024*1024) return (b/1024/1024/1024).toFixed(1)+' GiB';
  if (b >= 1024*1024) return (b/1024/1024).toFixed(1)+' MiB';
  return (b/1024).toFixed(1)+' KiB';
}
async function saveConfig(){
  const key = document.getElementById('cfgkey').value.trim();
  const val = document.getElementById('cfgval').value;
  if (!key){ document.getElementById('ts').textContent='key 不能为空'; return; }
  try{
    const r = await fetchJson('/api/center/config', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({key, value: val})});
    document.getElementById('ts').textContent = r.success ? '配置已保存: '+key : '保存失败: '+(r.message||'');
    document.getElementById('cfgval').value='';
    refresh();
  }catch(e){ document.getElementById('ts').textContent='保存失败: '+e; }
}
async function deleteConfig(key){
  try{
    const r = await fetchJson('/api/center/config/'+encodeURIComponent(key), {method:'DELETE'});
    document.getElementById('ts').textContent = r.success ? '已删除: '+key : '删除失败: '+(r.message||'');
    refresh();
  }catch(e){ document.getElementById('ts').textContent='删除失败: '+e; }
}
async function rollbackTo(version){
  if(!confirm('回滚到版本 '+version+'？将撤销其后全部配置变更')) return;
  try{
    const r = await fetchJson('/api/center/config-rollback', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({version})});
    document.getElementById('ts').textContent = '回滚完成，撤销 '+r.undone+' 条变更';
    refresh();
  }catch(e){ document.getElementById('ts').textContent='回滚失败: '+e; }
}
function renderConfig(cfg){
  const tb=document.querySelector('#cfgrows'); tb.textContent='';
  const map = (cfg && cfg.overrides) || {};
  const keys = Object.keys(map);
  if (keys.length===0){
    const tr=document.createElement('tr');
    const td=document.createElement('td'); td.colSpan=3; td.textContent='（无运行时覆盖，全部使用配置文件默认值）';
    tr.appendChild(td); tb.appendChild(tr);
    return;
  }
  for(const k of keys){
    const tr=document.createElement('tr');
    const td1=document.createElement('td'); td1.textContent=k;
    const td2=document.createElement('td'); td2.textContent=String(map[k]);
    const td3=document.createElement('td');
    const b=document.createElement('button'); b.textContent='删除'; b.onclick=()=>deleteConfig(k);
    td3.appendChild(b);
    tr.append(td1,td2,td3);
    tb.appendChild(tr);
  }
}
function renderHistory(hist){
  const tb=document.querySelector('#cfghistory'); tb.textContent='';
  if(!hist || hist.length===0){
    const tr=document.createElement('tr');
    const td=document.createElement('td'); td.colSpan=7; td.textContent='（暂无变更历史）';
    tr.appendChild(td); tb.appendChild(tr);
    return;
  }
  for(const e of hist){
    const tr=document.createElement('tr');
    const cells=[e.version, e.op, e.key, e.valueBefore==null?'-':e.valueBefore, e.valueAfter==null?'-':e.valueAfter, e.timeUtc?new Date(e.timeUtc).toLocaleTimeString():'-'];
    for(const v of cells){
      const td=document.createElement('td'); td.textContent=String(v); tr.appendChild(td);
    }
    const td3=document.createElement('td');
    if(e.op!=='rollback'){
      const b=document.createElement('button'); b.textContent='回滚到此'; b.onclick=()=>rollbackTo(e.version);
      td3.appendChild(b);
    }
    tr.appendChild(td3);
    tb.appendChild(tr);
  }
}
function drawLine(svg, pts, color, w){
  if (pts.length < 2) return;
  const d = pts.map((p,i)=> (i===0?'M':'L') + p[0].toFixed(1) + ',' + p[1].toFixed(1)).join(' ');
  const el = document.createElementNS('http://www.w3.org/2000/svg','path');
  el.setAttribute('d', d);
  el.setAttribute('fill','none');
  el.setAttribute('stroke', color);
  el.setAttribute('stroke-width', String(w||2));
  svg.appendChild(el);
}
function renderTrend(t){
  const svg = document.getElementById('trend');
  svg.textContent = '';
  const legend = document.getElementById('trendlegend'); legend.textContent='';
  if (!t || t.length < 2){ legend.textContent = '样本不足（启动后每 10 秒采集一点）'; return; }
  const W=800, H=150, PAD=8;
  const series = [
    {key:'nodeCount', label:'节点数', color:'#4caf50'},
    {key:'connectedCount', label:'在线', color:'#2196f3'},
    {key:'totalLoad', label:'总负载', color:'#ff9800'},
    {key:'roomCount', label:'房间数', color:'#e91e63'}
  ];
  let maxV = 4;
  for(const s of series) for(const p of t) maxV = Math.max(maxV, p[s.key]);
  maxV = Math.ceil(maxV * 1.1) || 1;
  const x = (i)=> PAD + (W - 2*PAD) * (i/(t.length-1));
  const y = (v)=> H - PAD - (H - 2*PAD) * (v/maxV);
  for(const s of series){
    drawLine(svg, t.map((p,i)=>[x(i), y(p[s.key])]), s.color, 2);
    const sw = document.createElement('span');
    sw.textContent = s.label + '=' + t[t.length-1][s.key] + '  ';
    legend.appendChild(sw);
  }
}
async function refresh(){
  try{
    const [h,n,s,c,r,t,m,cfg,hist]=await Promise.all([
      fetchJson('/api/center/health'),
      fetchJson('/api/center/nodes'),
      fetchJson('/api/center/summary'),
      fetchJson('/api/center/cluster'),
      fetchJson('/api/center/rooms'),
      fetchJson('/api/center/metrics-trend'),
      fetchJson('/api/center/node-metrics'),
      fetchJson('/api/center/config'),
      fetchJson('/api/center/config-history')
    ]);
    document.getElementById('ts').textContent='更新于 '+new Date().toLocaleTimeString()+'（每5秒自动刷新）';
    renderTrend(t);
    renderConfig(cfg);
    renderHistory(hist);
    // XSS 修复：使用 textContent 而非 innerHTML 拼接用户可控字段
    const health=document.getElementById('health');
    health.textContent='';
    const s1=document.createElement('span');
    s1.textContent='状态: '+(h.status==='ok'?'正常':'异常')+' | Leader: '+(h.isLeader?'是':'否')+' | 节点数: '+h.nodeCount+' | '+s.battle+' Battle / '+s.game+' Game / '+s.gateway+' Gateway / '+s.login+' Login';
    health.appendChild(s1);
    const nt=document.querySelector('#nodes'); nt.textContent='';
    const nm = {};
    for(const mt of m){ nm[mt.nodeId] = mt; }
    for(const node of n){
      const tr=document.createElement('tr');
      const met = nm[node.nodeId];
      const mem = (met && met.reachable) ? fmtBytes(met.memoryBytes) : '-';
      const up = (met && met.reachable) ? Math.round(met.uptimeSeconds) + 's' : '-';
      const thr = (met && met.reachable) ? met.threads : '-';
      const tds=[node.nodeId, node.nodeType, node.host+':'+node.port, node.currentLoad, new Date(node.lastHeartbeat).toLocaleTimeString(), node.isConnected?'在线':'离线', mem, up, thr];
      for(const v of tds){
        const td=document.createElement('td');
        td.textContent=String(v);  // XSS 修复：纯文本插入
        tr.appendChild(td);
      }
      nt.appendChild(tr);
    }
    const mt=document.querySelector('#machines'); mt.textContent='';
    for(const m of c.machines){
      const tr=document.createElement('tr');
      const tds=[m.machineId, m.supervisedBy||'-', m.totalNodes, m.battle, m.game, m.gateway, m.login, m.db, m.center, m.online+'/'+m.totalNodes];
      for(const v of tds){
        const td=document.createElement('td');
        td.textContent=String(v);
        tr.appendChild(td);
      }
      mt.appendChild(tr);
    }
    const rt=document.querySelector('#rooms'); rt.textContent='';
    for(const room of r){
      const tr=document.createElement('tr');
      const tds=[room.roomId, room.roomName, room.sceneType, room.battleNodeId, room.currentPlayers+'/'+room.maxPlayers, room.roomStatus, room.ownerUserId];
      for(const v of tds){
        const td=document.createElement('td');
        td.textContent=String(v);
        tr.appendChild(td);
      }
      rt.appendChild(tr);
    }
  }catch(e){ document.getElementById('ts').textContent='加载失败: '+e; }
}
refresh(); setInterval(refresh, 5000);
</script>
</body></html>
""";
}
