using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class UsageBridge
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly UsageLogs Logs = new UsageLogs();
    private static readonly string BarPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageBar", "CodexUsageBar.exe");

    private static void Main(string[] args)
    {
        bool reportMode = args.Contains("--report");
        using (var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
        using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
        {
            string line;
            while ((line = input.ReadLine()) != null)
            {
                Dictionary<string, object> request;
                try { request = Json.DeserializeObject(line) as Dictionary<string, object>; }
                catch (ArgumentException) { Reply(output, null, null, -32700, "Invalid JSON"); continue; }
                catch (InvalidOperationException) { Reply(output, null, null, -32700, "Invalid JSON"); continue; }
                object id;
                if (request == null || !request.TryGetValue("id", out id)) continue;
                var method = Get(request, "method") as string;
                var parameters = Get(request, "params") as Dictionary<string, object>;
                switch (method)
                {
                    case "initialize":
                        var offered = Get(parameters, "protocolVersion") as string;
                        var versions = new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
                        Reply(output, id, new {
                            protocolVersion = versions.Contains(offered) ? offered : versions[0],
                            capabilities = new { tools = new { } },
                            serverInfo = new {
                                name = reportMode ? "codex-usage-report" : "codex-usage",
                                version = reportMode ? "1.0.0" : "1.0.3"
                            },
                            instructions = reportMode
                                ? "本插件仅用于用户明确请求查询用量数字时。用户只发送 @Codex 用量 时由打开浮窗插件处理。读取默认取最近活动的本机会话；可指定本机会话链接或 UUID。"
                                : "用户只发送 @Codex 用量、仅发送 codex-usage 插件链接，或要求打开用量条时，直接调用 open_usage_bar 打开或重开桌面浮窗。"
                        });
                        break;
                    case "ping":
                        Reply(output, id, new { });
                        break;
                    case "tools/list":
                        Reply(output, id, new { tools = ToolList(reportMode) });
                        break;
                    case "tools/call":
                        try
                        {
                            var tool = Get(parameters, "name") as string;
                            if (!reportMode && tool == "open_usage_bar") Reply(output, id, OpenBar());
                            else if (reportMode && tool == "read_usage") Reply(output, id, ReadUsage(Get(parameters, "arguments") as Dictionary<string, object>));
                            else Reply(output, id, null, -32602, "Unknown tool");
                        }
                        catch (IOException) { Reply(output, id, Result("本机会话或浮窗暂时不可读。", true)); }
                        catch (UnauthorizedAccessException) { Reply(output, id, Result("无法访问本机用量浮窗或会话日志。", true)); }
                        catch (Win32Exception) { Reply(output, id, Result("Windows 未能打开用量浮窗。可从开始菜单重试。", true)); }
                        break;
                    default:
                        Reply(output, id, null, -32601, "Method not found");
                        break;
                }
            }
        }
    }

    private static object[] ToolList(bool reportMode)
    {
        if (!reportMode) return new object[] {
            new {
                name = "open_usage_bar",
                title = "打开 Codex 用量浮窗",
                description = "打开或重开 Windows 用量浮窗。用户只发送 @Codex 用量或单独发送该插件链接时，默认直接调用本工具。关闭后浮窗进程退出。",
                inputSchema = new { type = "object", properties = new { }, additionalProperties = false },
                annotations = new { readOnlyHint = false, destructiveHint = false, idempotentHint = true, openWorldHint = false }
            }
        };
        return new object[] {
            new {
                name = "read_usage",
                title = "读取 Codex 会话用量",
                description = "只在用户明确要求查询或汇报数字时读取本机 token、缓存和上下文用量。默认读取最近活动的本机会话。",
                inputSchema = new {
                    type = "object",
                    properties = new { session = new { type = "string", description = "可选。Codex 本机会话链接或 UUID。" } },
                    additionalProperties = false
                },
                annotations = new { readOnlyHint = true, openWorldHint = false }
            }
        };
    }

    private static object OpenBar()
    {
        if (!File.Exists(BarPath)) return Result("找不到已安装的用量浮窗。", true);
        using (var launch = Process.Start(new ProcessStartInfo(BarPath) { UseShellExecute = true }))
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                foreach (var process in Process.GetProcessesByName("CodexUsageBar"))
                {
                    using (process)
                    {
                        if (process.MainWindowHandle != IntPtr.Zero)
                            return Result("「Codex 用量」浮窗已在桌面打开。", false);
                    }
                }
                Thread.Sleep(150);
            }
        }
        return Result("已请求打开浮窗，Windows 尚未报告可见窗口。可以在开始菜单搜索 Codex Usage Bar。", true);
    }

    private static object ReadUsage(Dictionary<string, object> arguments)
    {
        var requested = (Get(arguments, "session") as string ?? "").Trim();
        Session selected;
        if (requested.Length == 0)
            selected = Logs.Sessions(false).FirstOrDefault();
        else
        {
            string threadId = Session.ParseId(requested);
            if (threadId == null) return Result("请提供本地 Codex 会话链接或 UUID。", true);
            selected = Logs.Sessions(true).FirstOrDefault(session => String.Equals(session.Id, threadId, StringComparison.OrdinalIgnoreCase));
        }
        if (selected == null) return Result("没有找到对应的本机 Codex 会话。", true);
        var usage = Logs.ReadLatest(selected.Path);
        if (usage == null) return Result("这条会话还没有写入用量记录。", true);
        var counts = new {
            selection = requested.Length == 0 ? "latest_local_activity" : "exact_local_session",
            model = usage.Model,
            total_tokens = usage.Total,
            input_tokens = usage.Input,
            uncached_input_tokens = usage.Uncached,
            cached_input_tokens = usage.Read,
            reported_cache_write_tokens = usage.Write,
            output_tokens = usage.Output,
            cache_hit_percent = Percentage(usage.Read, usage.Input),
            last_context_tokens = usage.Context,
            effective_context_window = usage.Window,
            context_used_percent = Percentage(usage.Context, usage.Window)
        };
        return new { content = new[] { new { type = "text", text = Json.Serialize(counts) } }, structuredContent = counts };
    }

    private static double? Percentage(long? count, long? capacity)
    {
        return count.HasValue && capacity > 0 ? (double?)Math.Round(count.Value * 100.0 / capacity.Value, 1) : null;
    }

    private static object Result(string message, bool error)
    {
        return new { content = new[] { new { type = "text", text = message } }, isError = error };
    }

    private static object Get(Dictionary<string, object> source, string key)
    {
        object value;
        return source != null && source.TryGetValue(key, out value) ? value : null;
    }

    private static void Reply(StreamWriter output, object id, object result, int? code = null, string message = null)
    {
        var response = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id } };
        if (code.HasValue) response["error"] = new { code = code.Value, message = message };
        else response["result"] = result;
        output.WriteLine(Json.Serialize(response));
    }
}
