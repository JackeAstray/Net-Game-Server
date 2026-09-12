using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static Machine.Program;
using ManagedInstance = Machine.Program.ManagedInstance;
using Topology = Machine.Program.Topology;

namespace Machine;

/// <summary>
/// Machine 本机 Web 控制台（对标 KBE guiconsole 的 machine 页简化版）。
/// 零依赖 HttpListener，默认仅监听 localhost，提供：
///   GET  /            —— 控制页（纯 JS，5s 轮询 /status）
///   GET  /status      —— 托管实例状态 JSON
///   POST /control     —— 控制指令 {action: start|stop|restart, instance: "DB-1"}
/// </summary>
internal static class HttpConsole
{
    private sealed class ControlRequest
    {
        public string? Action { get; set; }
        public string? Instance { get; set; }
    }

    public static async Task RunAsync(
        List<ManagedInstance> managed,
        Topology topology,
        int port,
        string? token,
        CancellationToken stoppingToken)
    {
        var listener = new HttpListener();
        // localhost 与 127.0.0.1 双前缀：HttpListener 的 localhost 前缀不接受 127.0.0.1 的 Host 头
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Machine] HTTP 控制台启动失败（端口 {port}）: {ex.Message}");
            return;
        }
        Console.WriteLine($"[Machine] HTTP 控制台: http://localhost:{port}/");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (HttpListenerException) { break; }

                _ = Task.Run(() => HandleAsync(ctx, managed, topology, token));
            }
        }
        finally
        {
            listener.Close();
        }
    }

    private static async Task HandleAsync(
        HttpListenerContext ctx,
        List<ManagedInstance> managed,
        Topology topology,
        string? token)
    {
        try
        {
            if (!string.IsNullOrEmpty(token) && !IsAuthorized(ctx, token))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await WriteAsync(ctx, "{\"ok\":false,\"error\":\"token 无效\"}");
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.HttpMethod == "GET" && path == "/")
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await WriteAsync(ctx, DashboardHtml);
            }
            else if (ctx.Request.HttpMethod == "GET" && path == "/status")
            {
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await WriteAsync(ctx, BuildStatusJson(managed, topology));
            }
            else if (ctx.Request.HttpMethod == "POST" && path == "/control")
            {
                await HandleControlAsync(ctx, managed, topology);
            }
            else
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteAsync(ctx, "{\"ok\":false,\"error\":\"not found\"}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Machine] HTTP 请求处理异常: {ex.Message}");
            try { ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static bool IsAuthorized(HttpListenerContext ctx, string token)
    {
        string? provided = ctx.Request.Headers["X-Token"];
        if (string.IsNullOrEmpty(provided))
        {
            provided = ctx.Request.QueryString["token"];
        }
        if (string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(token));
    }

    private static string BuildStatusJson(List<ManagedInstance> managed, Topology topology)
    {
        var instances = managed.Select(m =>
        {
            bool running = false;
            int? pid = null;
            try
            {
                var p = m.Process;
                if (p != null && !p.HasExited) { running = true; pid = p.Id; }
            }
            catch { /* 进程对象已释放 */ }
            return new
            {
                instanceId = m.Spec.InstanceId,
                nodeId = m.Spec.GeneratedNodeId,
                type = m.Spec.Template.Type,
                host = m.Spec.EffectiveHost,
                port = m.Spec.EffectivePort,
                running,
                ready = m.Ready,
                probeOk = m.ProbeOk,
                startCount = m.StartCount,
                restartCount = m.RestartCount,
                stopping = m.Stopping,
                pid,
                lastStartedAtUtc = m.LastStartedAtUtc?.ToString("O"),
                lastExitedAtUtc = m.LastExitedAtUtc?.ToString("O"),
                lastExitCode = m.LastExitCode,
                logFile = string.IsNullOrEmpty(m.LogFile) ? null : m.LogFile
            };
        });
        return JsonSerializer.Serialize(new
        {
            machineId = topology.MachineId,
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            instances
        });
    }

    private static async Task HandleControlAsync(
        HttpListenerContext ctx, List<ManagedInstance> managed, Topology topology)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        string body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
        ControlRequest? req = null;
        try { req = JsonSerializer.Deserialize<ControlRequest>(body); } catch { }

        if (req == null || string.IsNullOrEmpty(req.Action) || string.IsNullOrEmpty(req.Instance))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            await WriteAsync(ctx, "{\"ok\":false,\"error\":\"参数缺失（需要 action 与 instance）\"}");
            return;
        }

        var target = managed.FirstOrDefault(m => m.Spec.InstanceId.Equals(req.Instance, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            await WriteAsync(ctx, "{\"ok\":false,\"error\":\"实例不存在\"}");
            return;
        }

        string message;
        int statusCode = (int)HttpStatusCode.OK;
        // 控制指令串行化，避免与崩溃自动重启/退出回调并发竞态
        lock (managed)
        {
            switch (req.Action.ToLowerInvariant())
            {
                case "start":
                    target.Stopping = false;
                    if (IsRunning(target))
                    {
                        message = "已在运行";
                    }
                    else
                    {
                        StartProcess(target, topology);
                        message = "启动指令已发出";
                    }
                    break;
                case "stop":
                    target.Stopping = true;
                    KillQuiet(target);
                    message = "停止指令已发出";
                    break;
                case "restart":
                    target.Stopping = false;
                    if (IsRunning(target))
                    {
                        KillQuiet(target); // 退出回调触发看护自动重启
                        message = "重启指令已发出";
                    }
                    else
                    {
                        StartProcess(target, topology);
                        message = "启动指令已发出";
                    }
                    break;
                default:
                    statusCode = (int)HttpStatusCode.BadRequest;
                    message = "未知动作";
                    break;
            }
        }

        ctx.Response.StatusCode = statusCode;
        await WriteAsync(ctx, statusCode == (int)HttpStatusCode.OK
            ? $"{{\"ok\":true,\"message\":\"{message}\"}}"
            : $"{{\"ok\":false,\"error\":\"{message}\"}}");
    }

    private static bool IsRunning(ManagedInstance m)
    {
        try
        {
            var p = m.Process;
            return p != null && !p.HasExited;
        }
        catch { return false; }
    }

    private static void KillQuiet(ManagedInstance m)
    {
        try
        {
            var p = m.Process;
            if (p != null && !p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>控制页：纯 JS 轮询 /status，展示托管实例状态并提供控制按钮（textContent 防 XSS）。</summary>
    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="zh">
<head><meta charset="utf-8"><title>Net-Game-Server Machine 控制台</title>
<style>
body{font-family:system-ui;background:#111;color:#ddd;margin:20px}
h1{font-size:20px}h2{font-size:15px;margin:8px 0}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{border:1px solid #333;padding:5px 8px;text-align:left}th{background:#222}
.ok{color:#4caf50}.bad{color:#f44336}.warn{color:#ff9800}
.card{background:#181818;padding:12px;border-radius:8px;margin-bottom:14px}
button{margin-left:4px;cursor:pointer}
#ts{font-size:12px;color:#888;font-weight:normal}
</style></head>
<body>
<h1>Net-Game-Server Machine 控制台 <span id="ts"></span></h1>
<div class="card"><h2>托管进程 <button onclick="refresh()">刷新</button></h2>
<table><thead><tr><th>实例</th><th>类型</th><th>地址</th><th>PID</th><th>状态</th><th>就绪</th><th>启动/重启</th><th>上次退出码</th><th>日志</th><th>操作</th></tr></thead><tbody id="rows"></tbody></table></div>
<script>
async function fetchJson(url, opts){
  const res = await fetch(url, opts);
  if (!res.ok) throw new Error(url + ' HTTP ' + res.status);
  return res.json();
}
async function control(instance, action){
  try{
    const r = await fetchJson('/control', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({instance, action})});
    document.getElementById('ts').textContent = r.message || '已执行';
    setTimeout(refresh, 500);
  }catch(e){ document.getElementById('ts').textContent = '操作失败: ' + e; }
}
async function refresh(){
  try{
    const st = await fetchJson('/status');
    document.getElementById('ts').textContent = '更新于 ' + new Date().toLocaleTimeString() + '（Machine: ' + st.machineId + '）';
    const tb = document.getElementById('rows'); tb.textContent = '';
    for(const m of st.instances){
      const tr = document.createElement('tr');
      const stateSpan = document.createElement('span');
      if (m.stopping){ stateSpan.textContent = '停机中'; stateSpan.className = 'bad'; }
      else if (m.running){ stateSpan.textContent = '运行'; stateSpan.className = 'ok'; }
      else { stateSpan.textContent = '停止'; stateSpan.className = 'warn'; }
      const cells = [m.instanceId, m.type, m.host + ':' + m.port, m.pid ?? '-', m.ready ? '是' : '否', m.startCount + ' / ' + m.restartCount, m.lastExitCode ?? '-', m.logFile ?? '-'];
      for(const v of cells){
        const td = document.createElement('td');
        td.textContent = String(v);
        tr.appendChild(td);
      }
      const st = document.createElement('td'); st.appendChild(stateSpan); tr.appendChild(st);
      const op = document.createElement('td');
      const b1 = document.createElement('button'); b1.textContent = '重启'; b1.onclick = () => control(m.instanceId, 'restart');
      const b2 = document.createElement('button'); b2.textContent = '停止'; b2.onclick = () => control(m.instanceId, 'stop');
      const b3 = document.createElement('button'); b3.textContent = '启动'; b3.onclick = () => control(m.instanceId, 'start');
      op.append(b1, b2, b3);
      tr.appendChild(op);
      tb.appendChild(tr);
    }
  }catch(e){ document.getElementById('ts').textContent = '加载失败: ' + e; }
}
refresh(); setInterval(refresh, 5000);
</script>
</body></html>
""";
}
