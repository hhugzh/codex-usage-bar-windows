using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class Session
{
    public string Path;
    public string Id;
    public string Project;
    public string Title;
    public DateTime Updated;
    public string Name { get { return Title ?? "未命名会话"; } }
    public string Details { get { return Project + " · " + Updated.ToString("MM-dd HH:mm"); } }

    public static string ParseId(string input)
    {
        Guid id;
        if (Guid.TryParse(input, out id)) return id.ToString();
        Uri link;
        if (Uri.TryCreate(input, UriKind.Absolute, out link)
            && link.Scheme.Equals("codex", StringComparison.OrdinalIgnoreCase)
            && link.Host.Equals("threads", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(link.AbsolutePath.Trim('/'), out id)) return id.ToString();
        return null;
    }
}

internal sealed class Snapshot
{
    public string Model;
    public long? Total, Input, Read, Write, Output, Window, Context;
    public long? Uncached
    {
        get { return Input.HasValue && Read.HasValue && Write.HasValue ? Math.Max(0, Input.Value - Read.Value - Write.Value) : (long?)null; }
    }
}

internal sealed class UsageLogs
{
    private readonly string home;
    private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    private readonly Dictionary<string, Session> sessionCache = new Dictionary<string, Session>();
    private readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private DateTime titleStamp;

    public UsageLogs()
    {
        string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        home = String.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured;
    }

    public List<Session> Sessions(bool includeOlder)
    {
        LoadTitles();
        var found = new List<Session>();
        try
        {
            var files = FilesIn("sessions");
            if (includeOlder) files = files.Concat(FilesIn("archived_sessions"));
            IEnumerable<FileInfo> ordered = files.OrderByDescending(file => file.LastWriteTimeUtc);
            if (!includeOlder) ordered = ordered.Take(40);
            foreach (var file in ordered)
            {
                var info = Describe(file.FullName);
                if (info == null) continue;
                string title;
                titles.TryGetValue(info.Id, out title);
                found.Add(new Session {
                    Path = info.Path, Id = info.Id, Project = info.Project, Title = title, Updated = file.LastWriteTime
                });
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return found;
    }

    private IEnumerable<FileInfo> FilesIn(string name)
    {
        var directory = new DirectoryInfo(Path.Combine(home, name));
        return directory.Exists
            ? directory.EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            : Enumerable.Empty<FileInfo>();
    }

    private void LoadTitles()
    {
        var index = new FileInfo(Path.Combine(home, "session_index.jsonl"));
        if (!index.Exists || index.LastWriteTimeUtc == titleStamp) return;
        try
        {
            using (var reader = new StreamReader(Open(index.FullName)))
            {
                titles.Clear();
                for (string line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    var entry = Parse(line);
                    string id = Get(entry, "id") as string;
                    string title = Get(entry, "thread_name") as string;
                    if (!String.IsNullOrWhiteSpace(id) && !String.IsNullOrWhiteSpace(title))
                        titles[id] = title.Trim();
                }
                titleStamp = index.LastWriteTimeUtc;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private Session Describe(string path)
    {
        Session session;
        if (sessionCache.TryGetValue(path, out session)) return session;
        try
        {
            using (var reader = new StreamReader(Open(path)))
            {
                var meta = Map(Parse(reader.ReadLine()), "payload");
                var source = Get(meta, "source");
                var id = Get(meta, "id") as string;
                if (String.IsNullOrEmpty(id)) return null;
                bool child = (source as string) == "subagent" || Get(source as Dictionary<string, object>, "subagent") != null;
                if (!child)
                {
                    var cwd = Get(meta, "cwd") as string;
                    string project = cwd == null ? "本地会话" : Path.GetFileName(cwd.TrimEnd('\u005c', '/'));
                    session = new Session { Path = path, Id = id, Project = project };
                }
                sessionCache[path] = session;
                return session;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public Snapshot ReadLatest(string path)
    {
        Snapshot snapshot = null;
        string newerModel = null;
        foreach (string line in RecentLines(path))
        {
            if (snapshot == null && line.IndexOf("\"token_count\"", StringComparison.Ordinal) >= 0)
            {
                var entry = Parse(line);
                if ((Get(entry, "type") as string) != "event_msg") continue;
                var payload = Map(entry, "payload");
                if ((Get(payload, "type") as string) != "token_count") continue;
                var info = Map(payload, "info");
                var total = Map(info, "total_token_usage");
                if (total == null) continue;
                snapshot = new Snapshot {
                    Model = newerModel,
                    Total = Num(total, "total_tokens"), Input = Num(total, "input_tokens"),
                    Read = Num(total, "cached_input_tokens"), Write = Num(total, "cache_write_input_tokens"),
                    Output = Num(total, "output_tokens"), Window = Num(info, "model_context_window"),
                    Context = Num(Map(info, "last_token_usage"), "total_tokens")
                };
            }
            else if (line.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0)
            {
                var entry = Parse(line);
                if ((Get(entry, "type") as string) != "turn_context") continue;
                var slug = Get(Map(entry, "payload"), "model") as string;
                if (snapshot == null) { if (newerModel == null) newerModel = slug; }
                else if (slug != null)
                {
                    // This turn context predates the selected usage event.
                    snapshot.Model = slug;
                    break;
                }
            }
        }
        return snapshot;
    }

    private static IEnumerable<string> RecentLines(string path)
    {
        using (var stream = Open(path))
        {
            byte[] block = new byte[64 * 1024];
            var reverseLine = new List<byte>();
            bool oversized = false;
            long position = stream.Length;
            while (position > 0)
            {
                int size = (int)Math.Min(position, block.Length);
                position -= size;
                stream.Position = position;
                int length = 0;
                while (length < size)
                {
                    int received = stream.Read(block, length, size - length);
                    if (received == 0) break;
                    length += received;
                }
                for (int i = length - 1; i >= 0; i--)
                {
                    if (block[i] != 10)
                    {
                        if (reverseLine.Count < 512 * 1024) reverseLine.Add(block[i]);
                        else oversized = true;
                        if (position != 0 || i != 0) continue;
                    }
                    if (!oversized && reverseLine.Count > 0)
                    {
                        reverseLine.Reverse();
                        var line = Encoding.UTF8.GetString(reverseLine.ToArray()).TrimEnd('\r');
                        if (line.IndexOf("\"token_count\"", StringComparison.Ordinal) >= 0 ||
                            line.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0)
                            yield return line;
                    }
                    reverseLine.Clear();
                    oversized = false;
                }
            }
        }
    }

    private static FileStream Open(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private Dictionary<string, object> Parse(string line)
    {
        if (String.IsNullOrEmpty(line)) return null;
        try { return json.DeserializeObject(line) as Dictionary<string, object>; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static object Get(Dictionary<string, object> map, string key)
    {
        object value;
        return map != null && map.TryGetValue(key, out value) ? value : null;
    }

    private static Dictionary<string, object> Map(Dictionary<string, object> map, string key)
    {
        return Get(map, key) as Dictionary<string, object>;
    }

    private static long? Num(Dictionary<string, object> map, string key)
    {
        var value = Get(map, key);
        return value is int || value is long ? Convert.ToInt64(value) : (long?)null;
    }
}
