using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Logger;

/// <summary>
/// Logger 本机日志查看器：HTTP 查询聚合日志并按条件过滤（对标 KBE logviewer 简化版）。
/// 零依赖 HttpListener，仅监听 localhost，提供：
///   GET  /      —— 查看页（纯 JS）
///   GET  /logs  —— 查询 JSON：?node=&amp;level=&amp;q=&amp;date=yyyyMMdd&amp;max=
/// </summary>
internal static class HttpLogViewer
{
    public static async Task RunAsync(string logDir, int port, string? token, CancellationToken stoppingToken)
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
            Console.Error.WriteLine($"[Logger] HTTP 日志查看启动失败（端口 {port}）: {ex.Message}");
            return;
        }
        Console.WriteLine($"[Logger] HTTP 日志查看: http://localhost:{port}/ （logDir={logDir}）");

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

                _ = Task.Run(() => HandleAsync(ctx, logDir, token));
            }
        }
        finally
        {
            listener.Close();
        }
    }

    private static async Task HandleAsync(HttpListenerContext ctx, string logDir, string? token)
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
            else if (ctx.Request.HttpMethod == "GET" && path == "/logs")
            {
                var qs = ctx.Request.QueryString;
                string? node = qs["node"];
                string? level = qs["level"];
                string? keyword = qs["q"];
                string? date = qs["date"];
                string dateStr = string.IsNullOrWhiteSpace(date) ? DateTime.UtcNow.ToString("yyyyMMdd") : date;
                if (!int.TryParse(qs["max"], out int max) || max <= 0) max = 500;
                max = Math.Min(max, 2000);

                ctx.Response.ContentType = "application/json; charset=utf-8";
                await WriteAsync(ctx, QueryLogs(logDir, node, level, keyword, dateStr, max));
            }
            else
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteAsync(ctx, "{\"ok\":false,\"error\":\"not found\"}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logger] HTTP 请求处理异常: {ex.Message}");
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

    /// <summary>扫描指定日期的日志文件（含滚动 .1/.2/.3），按节点/级别/关键字过滤，最新在前。</summary>
    private static string QueryLogs(string logDir, string? node, string? level, string? keyword, string dateStr, int max)
    {
        var logs = new List<object>();
        try
        {
            string pattern = $"*.{dateStr}.log*";
            var files = Directory.GetFiles(logDir, pattern)
                .Where(f =>
                {
                    if (string.IsNullOrWhiteSpace(node)) return true;
                    // 文件名为 {Sanitize(node)}.{date}.log 或滚动 {Sanitize(node)}.{date}.log.N
                    return Path.GetFileName(f).StartsWith(Sanitize(node) + ".", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f)
                .ToList();

            foreach (var file in files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    var (lineLevel, lineText) = ParseLine(line);
                    if (lineText == null) continue;
                    if (!string.IsNullOrWhiteSpace(level) &&
                        !lineLevel.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(keyword) &&
                        !lineText.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                    logs.Add(new { time = line[..Math.Min(12, line.Length)], level = lineLevel, text = lineText });
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // 日志目录尚未创建（无节点上报过）
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Logger] 日志查询失败: {ex.Message}");
        }

        var tail = logs.AsEnumerable().Reverse().Take(max);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            node = node ?? "",
            level = level ?? "",
            keyword = keyword ?? "",
            date = dateStr,
            count = logs.Count,
            returned = tail.Count(),
            logs = tail
        });
    }

    /// <summary>行格式：HH:mm:ss.fff LEVEL\t timestamp\t message。返回 (level, 完整文本)。</summary>
    private static (string Level, string? Text) ParseLine(string line)
    {
        if (line.Length <= 12) return ("", null);
        string rest = line[12..];
        int tab = rest.IndexOf('\t');
        if (tab < 0) return ("", rest);
        return (rest[..tab].Trim(), rest);
    }

    private static string Sanitize(string nodeId)
    {
        var sb = new StringBuilder(nodeId.Length);
        foreach (var c in nodeId)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.ToString();
    }

    private static async Task WriteAsync(HttpListenerContext ctx, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private const string DashboardHtml = """
<!DOCTYPE html>
<html lang="zh">
<head><meta charset="utf-8"><title>Net-Game-Server 日志查看器</title>
<style>
body{font-family:system-ui;background:#111;color:#ddd;margin:20px}
h1{font-size:20px}
table{border-collapse:collapse;width:100%;font-size:12px;font-family:Consolas,monospace}
th,td{border:1px solid #333;padding:3px 6px;text-align:left;vertical-align:top;white-space:pre-wrap;word-break:break-all}
th{background:#222;position:sticky;top:0}
input,select{padding:4px;margin-right:8px}
.card{background:#181818;padding:12px;border-radius:8px;margin-bottom:12px}
#ts{font-size:12px;color:#888;font-weight:normal}
</style></head>
<body>
<h1>Net-Game-Server 日志查看器 <span id="ts"></span></h1>
<div class="card">
  节点 <input id="node" placeholder="如 Login-127.0.0.1:31302">
  级别 <select id="level"><option value="">全部</option><option>ERROR</option><option>WARNING</option><option>INFO</option><option>DEBUG</option></select>
  关键字 <input id="q" placeholder="子串过滤">
  条数 <input id="max" value="500" style="width:70px">
  <button onclick="refresh()">查询</button>
  <button onclick="clearLogs()">清空</button>
</div>
<div id="logs"></div>
<script>
async function fetchJson(url){
  const res = await fetch(url);
  if (!res.ok) throw new Error(url + ' HTTP ' + res.status);
  return res.json();
}
async function refresh(){
  try{
    const p = new URLSearchParams();
    const node = document.getElementById('node').value.trim();
    const level = document.getElementById('level').value;
    const q = document.getElementById('q').value.trim();
    const max = document.getElementById('max').value.trim();
    if (node) p.set('node', node);
    if (level) p.set('level', level);
    if (q) p.set('q', q);
    if (max) p.set('max', max);
    const r = await fetchJson('/logs?' + p.toString());
    document.getElementById('ts').textContent = '命中 ' + r.count + ' 条，返回 ' + r.returned + ' 条（' + r.date + '）';
    const box = document.getElementById('logs'); box.textContent = '';
    const tbl = document.createElement('table');
    const head = document.createElement('thead');
    const hr = document.createElement('tr');
    for (const h of ['时间','级别','内容']){
      const th = document.createElement('th'); th.textContent = h; hr.appendChild(th);
    }
    head.appendChild(hr); tbl.appendChild(head);
    const body = document.createElement('tbody');
    for (const lg of r.logs){
      const tr = document.createElement('tr');
      const td1 = document.createElement('td'); td1.textContent = lg.time;
      const td2 = document.createElement('td'); td2.textContent = lg.level;
      const td3 = document.createElement('td'); td3.textContent = lg.text;
      tr.append(td1, td2, td3);
      body.appendChild(tr);
    }
    tbl.appendChild(body);
    box.appendChild(tbl);
  }catch(e){ document.getElementById('ts').textContent = '加载失败: ' + e; }
}
function clearLogs(){
  document.getElementById('logs').textContent = '';
  document.getElementById('ts').textContent = '已清空';
}
refresh();
</script>
</body></html>
""";
}
