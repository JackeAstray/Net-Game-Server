using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Shared;

namespace Center;

internal static class CenterHttpServer
{
    /// <summary>
    /// 启动并运行中心服务器的 ASP.NET Core Web 应用；配置 Kestrel 在配置的 HTTP 端口（默认 31316）监听，启用 Serilog，注册并映射控制器。
    /// </summary>
    /// <remarks>若配置项 CenterHttpPort 为 0 或未配置，则使用默认端口 31316。启动完成后记录运行信息并异步监听连接。</remarks>
    /// <param name="args">传递给 WebApplication 创建器的命令行参数。</param>
    /// <returns>表示应用启动并异步运行直到停止的可等待任务。</returns>
    public static async Task StartAsync(string[] args)
    {
        int httpPort = ConfigHelper.GetConfig<int>("CenterHttpPort") == 0 ? 31316 : ConfigHelper.GetConfig<int>("CenterHttpPort");

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(httpPort);
        });

        builder.Host.UseSerilog();
        builder.Services.AddControllers();

        var app = builder.Build();

        // 管理台首页（对标 KBE guiconsole 的 Web 简化版）：轮询 health/nodes/summary/rooms
        app.MapGet("/", () => Results.Content(DashboardHtml, "text/html; charset=utf-8"));

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
<div class="card" id="health"></div>
<div class="card"><h2>节点</h2><table><thead><tr><th>节点ID</th><th>类型</th><th>地址</th><th>负载</th><th>心跳</th><th>连接</th></tr></thead><tbody id="nodes"></tbody></table></div>
<div class="card"><h2>房间</h2><table><thead><tr><th>房间ID</th><th>名称</th><th>类型</th><th>Battle节点</th><th>人数</th><th>状态</th><th>房主</th></tr></thead><tbody id="rooms"></tbody></table></div>
<script>
async function refresh(){
  try{
    const [h,n,s,r]=await Promise.all([
      fetch('/api/center/health').then(x=>x.json()),
      fetch('/api/center/nodes').then(x=>x.json()),
      fetch('/api/center/summary').then(x=>x.json()),
      fetch('/api/center/rooms').then(x=>x.json())
    ]);
    document.getElementById('ts').textContent='更新于 '+new Date().toLocaleTimeString()+'（每5秒自动刷新）';
    document.getElementById('health').innerHTML='状态: <b class="'+(h.status==='ok'?'ok':'bad')+'">'+h.status+'</b> | Leader: <b>'+h.isLeader+'</b> | 节点数: <b>'+h.nodeCount+'</b> | '+s.battle+' Battle / '+s.game+' Game / '+s.gateway+' Gateway / '+s.login+' Login';
    const nt=document.querySelector('#nodes'); nt.innerHTML='';
    for(const node of n){
      const tr=document.createElement('tr');
      tr.innerHTML='<td>'+node.nodeId+'</td><td>'+node.nodeType+'</td><td>'+node.host+':'+node.port+'</td><td>'+node.currentLoad+'</td><td>'+new Date(node.lastHeartbeat).toLocaleTimeString()+'</td><td class="'+(node.isConnected?'ok':'bad')+'">'+(node.isConnected?'在线':'离线')+'</td>';
      nt.appendChild(tr);
    }
    const rt=document.querySelector('#rooms'); rt.innerHTML='';
    for(const room of r){
      const tr=document.createElement('tr');
      tr.innerHTML='<td>'+room.roomId+'</td><td>'+room.roomName+'</td><td>'+room.sceneType+'</td><td>'+room.battleNodeId+'</td><td>'+room.currentPlayers+'/'+room.maxPlayers+'</td><td>'+room.roomStatus+'</td><td>'+room.ownerUserId+'</td>';
      rt.appendChild(tr);
    }
  }catch(e){ document.getElementById('ts').textContent='加载失败: '+e; }
}
refresh(); setInterval(refresh, 5000);
</script>
</body></html>
""";
}
