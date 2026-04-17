using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Reflection;

const string ServiceName = "softcenter.service";
const string ServicePath = $"/etc/systemd/system/{ServiceName}";
string? currentExe = Process.GetCurrentProcess().MainModule?.FileName;

if (!string.IsNullOrEmpty(currentExe) && !File.Exists(ServicePath))
{
    string serviceContent = $@"[Unit]
Description=UniFi SoftCenter & Boot Manager
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart={currentExe}
WorkingDirectory=/data/softcenter
Restart=always
RestartSec=5
KillMode=process

[Install]
WantedBy=multi-user.target";
    File.WriteAllText(ServicePath, serviceContent.Trim());
    Process.Start("systemctl", "daemon-reload")?.WaitForExit();
    Process.Start("systemctl", $"enable {ServiceName}")?.WaitForExit();
    Process.Start("systemctl", $"start {ServiceName}");
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

const string BaseDir = "/data/softcenter";
const string DbPath = $"Data Source={BaseDir}/manager.db";
const string ConfigPath = $"{BaseDir}/config.json";

if (!Directory.Exists(BaseDir)) Directory.CreateDirectory(BaseDir);
if (!Directory.Exists($"{BaseDir}/web")) Directory.CreateDirectory($"{BaseDir}/web");

AppConfig sysConfig;
if (File.Exists(ConfigPath))
{
    var json = File.ReadAllText(ConfigPath);
    sysConfig = JsonSerializer.Deserialize<AppConfig>(json, AppJsonContext.Default.AppConfig) ?? new AppConfig();
}
else
{
    sysConfig = new AppConfig();
    File.WriteAllText(ConfigPath, JsonSerializer.Serialize(sysConfig, AppJsonContext.Default.AppConfig));
}

using (var conn = new SqliteConnection(DbPath))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS apps_registry (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Type TEXT NOT NULL, Icon TEXT DEFAULT 'box', 
            StartCommand TEXT NOT NULL, StopCommand TEXT NOT NULL, StatusCommand TEXT NOT NULL, 
            IsAutoStart INTEGER DEFAULT 0, ConfigPath TEXT, ConfigKeys TEXT, LogPath TEXT, SortOrder INTEGER DEFAULT 0,
            Version TEXT DEFAULT '1.0.0', Description TEXT DEFAULT '', CustomCommands TEXT DEFAULT '[]'
        );
        CREATE TABLE IF NOT EXISTS cron_registry (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Schedule TEXT NOT NULL, Command TEXT NOT NULL
        );";
    cmd.ExecuteNonQuery();
    try { using var c1 = conn.CreateCommand(); c1.CommandText = "ALTER TABLE apps_registry ADD COLUMN SortOrder INTEGER DEFAULT 0;"; c1.ExecuteNonQuery(); } catch { }
    try { using var c2 = conn.CreateCommand(); c2.CommandText = "ALTER TABLE apps_registry ADD COLUMN Version TEXT DEFAULT '0.0.1';"; c2.ExecuteNonQuery(); } catch { }
    try { using var c3 = conn.CreateCommand(); c3.CommandText = "ALTER TABLE apps_registry ADD COLUMN Description TEXT DEFAULT '';"; c3.ExecuteNonQuery(); } catch { }
    try { using var c4 = conn.CreateCommand(); c4.CommandText = "ALTER TABLE apps_registry ADD COLUMN CustomCommands TEXT DEFAULT '[]';"; c4.ExecuteNonQuery(); } catch { }
}

string bootLock = "/tmp/softcenter_booted.lock";
if (!File.Exists(bootLock))
{
    try
    {
        var cronLines = new List<string>();
        using (var conn = new SqliteConnection(DbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Schedule, Command FROM cron_registry";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) cronLines.Add($"{reader.GetString(0)} {reader.GetString(1)}");
        }
        if (cronLines.Any())
        {
            File.WriteAllText("/tmp/sc_cron_restore", string.Join("\n", cronLines) + "\n");
            Process.Start("/bin/bash", "-c \"crontab /tmp/sc_cron_restore\"")?.WaitForExit();
        }
    }
    catch { }

    try
    {
        string onBootDir = "/data/softcenter/on_boot.d";
        if (!Directory.Exists(onBootDir)) Directory.CreateDirectory(onBootDir);
        var scripts = Directory.GetFiles(onBootDir, "*.sh").OrderBy(x => x);
        foreach (var script in scripts)
        {
            Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"chmod +x {script}; {script}\"", UseShellExecute = false, CreateNoWindow = true });
        }
    }
    catch { }

    try
    {
        using (var conn = new SqliteConnection(DbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT StartCommand FROM apps_registry WHERE IsAutoStart=1";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var startCmd = reader.GetString(0);
                Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{startCmd}\"", UseShellExecute = false, CreateNoWindow = true });
            }
        }
    }
    catch { }

    File.WriteAllText(bootLock, DateTime.Now.ToString());
}

app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var auth) || auth != sysConfig.AdminToken)
        {
            context.Response.StatusCode = 401; return;
        }
    }
    await next();
});

var fileProvider = new PhysicalFileProvider($"{BaseDir}/web");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });

bool IsAppRunning(string cmdStr)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{cmdStr}\"", UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(); return p?.ExitCode == 0;
    }
    catch { return false; }
}

string GetBashOutput(string cmd)
{
    try
    {
        using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{cmd}\"", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(2000); return p?.StandardOutput.ReadToEnd().Trim() ?? "";
    }
    catch { return ""; }
}

app.MapGet("/api/apps", () => {
    var apps = new List<AppEntity>();
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Name, Type, Icon, StartCommand, StopCommand, StatusCommand, IsAutoStart, ConfigPath, ConfigKeys, LogPath, SortOrder, Version, Description, CustomCommands FROM apps_registry ORDER BY SortOrder ASC, Id ASC";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var sCmd = reader.GetString(6);
        apps.Add(new AppEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), sCmd, reader.GetInt32(7), IsAppRunning(sCmd), reader.IsDBNull(8) ? "" : reader.GetString(8), reader.IsDBNull(9) ? "" : reader.GetString(9), reader.IsDBNull(10) ? "" : reader.GetString(10), reader.IsDBNull(11) ? 0 : reader.GetInt32(11), reader.IsDBNull(12) ? "1.0.0" : reader.GetString(12), reader.IsDBNull(13) ? "" : reader.GetString(13), reader.IsDBNull(14) ? "[]" : reader.GetString(14)));
    }
    return apps;
});

app.MapPost("/api/apps", (AppEntity a) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT OR REPLACE INTO apps_registry VALUES (@Id,@Name,@Type,@Icon,@Start,@Stop,@Status,@Auto,@CPath,@CKeys,@LPath,@SortOrder,@Ver,@Desc,@Custom)";
    cmd.Parameters.AddWithValue("@Id", a.Id); cmd.Parameters.AddWithValue("@Name", a.Name); cmd.Parameters.AddWithValue("@Type", a.Type);
    cmd.Parameters.AddWithValue("@Icon", a.Icon ?? "box"); cmd.Parameters.AddWithValue("@Start", a.StartCommand); cmd.Parameters.AddWithValue("@Stop", a.StopCommand);
    cmd.Parameters.AddWithValue("@Status", a.StatusCommand); cmd.Parameters.AddWithValue("@Auto", a.IsAutoStart);
    cmd.Parameters.AddWithValue("@CPath", a.ConfigPath ?? ""); cmd.Parameters.AddWithValue("@CKeys", a.ConfigKeys ?? ""); cmd.Parameters.AddWithValue("@LPath", a.LogPath ?? "");
    cmd.Parameters.AddWithValue("@SortOrder", a.SortOrder); cmd.Parameters.AddWithValue("@Ver", a.Version ?? "0.0.1"); cmd.Parameters.AddWithValue("@Desc", a.Description ?? "");
    cmd.Parameters.AddWithValue("@Custom", a.CustomCommands ?? "[]");
    cmd.ExecuteNonQuery(); return Results.Ok(new { success = true });
});

app.MapDelete("/api/apps/{id}", (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM apps_registry WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery(); return Results.Ok(new { success = true });
});

app.MapPost("/api/apps/reorder", (List<AppOrderReq> req) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var tx = conn.BeginTransaction();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE apps_registry SET SortOrder = @so WHERE Id = @id";
    var pId = cmd.Parameters.Add("@id", SqliteType.Text);
    var pSo = cmd.Parameters.Add("@so", SqliteType.Integer);
    foreach (var r in req) { pId.Value = r.Id; pSo.Value = r.SortOrder; cmd.ExecuteNonQuery(); }
    tx.Commit(); return Results.Ok(new { success = true });
});

app.MapPost("/api/apps/{id}/control", (string id, string action) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT StartCommand, StopCommand FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var exec = action == "start" ? reader.GetString(0) : (action == "stop" ? reader.GetString(1) : $"{reader.GetString(1)};sleep 1;{reader.GetString(0)}");
    Process.Start("/bin/bash", $"-c \"{exec}\""); return Results.Ok(new { success = true });
});

app.MapPost("/api/apps/{id}/custom_command", (string id, CustomCommandReq req) => {
    using var p = Process.Start(new ProcessStartInfo {
        FileName = "/bin/bash",
        Arguments = $"-c \"export TERM=xterm; {req.Command}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    });
    p?.WaitForExit();
    var output = p?.StandardOutput.ReadToEnd() + p?.StandardError.ReadToEnd();
    return Results.Ok(new LogResponse(output ?? ""));
});
app.MapPut("/api/apps/{id}/autostart/{state:int}", (string id, int state) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();

    using var cmdSel = conn.CreateCommand();
    cmdSel.CommandText = "SELECT StartCommand FROM apps_registry WHERE Id=@id";
    cmdSel.Parameters.AddWithValue("@id", id);
    var startCmd = cmdSel.ExecuteScalar()?.ToString();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE apps_registry SET IsAutoStart=@state WHERE Id=@id";
    cmd.Parameters.AddWithValue("@state", state); cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery();

    if (!string.IsNullOrEmpty(startCmd) && startCmd.Contains("systemctl start"))
    {
        var serviceName = startCmd.Replace("systemctl start", "").Trim();
        var action = state == 1 ? "enable" : "disable";
        Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"systemctl {action} {serviceName}\"", UseShellExecute = false, CreateNoWindow = true });
    }

    return Results.Ok(new { success = true });
});

// 🌟 修复：获取配置项时，向上扫描寻找注释，打包进 ConfigItem
app.MapGet("/api/apps/{id}/config", (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath, ConfigKeys FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1)) return Results.NotFound();
    var path = reader.GetString(0);
    var keys = reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries);
    var dict = new Dictionary<string, ConfigItem>();

    if (File.Exists(path))
    {
        var lines = File.ReadAllLines(path);
        foreach (var k in keys.Select(x => x.Trim()))
        {
            string val = "";
            string comment = "";
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i], $@"{k}=([^\n\r]*)");
                if (m.Success)
                {
                    val = m.Groups[1].Value;
                    // 向上寻找最近的非空行，判断是否为 # 注释
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (string.IsNullOrWhiteSpace(lines[j])) continue;
                        if (lines[j].TrimStart().StartsWith("#"))
                        {
                            comment = lines[j].TrimStart().TrimStart('#').Trim();
                        }
                        break; // 找到一行非空行后立即停止扫描
                    }
                    break;
                }
            }
            dict[k] = new ConfigItem(val, comment);
        }
    }
    return Results.Ok(dict);
});

app.MapPost("/api/apps/{id}/config", async (string id, Dictionary<string, string> payload) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    var path = cmd.ExecuteScalar()?.ToString();
    if (!string.IsNullOrEmpty(path) && File.Exists(path))
    {
        var content = await File.ReadAllTextAsync(path);
        foreach (var kv in payload) content = Regex.Replace(content, $@"{kv.Key}=[^\n\r]*", _ => $"{kv.Key}={kv.Value}");
        await File.WriteAllTextAsync(path, content);
        return Results.Ok(new { success = true });
    }
    return Results.NotFound();
});

app.MapGet("/api/apps/{id}/logs", (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT LogPath FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    var logPath = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return Results.Ok(new LogResponse("无日志或未配置。"));
    using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"tail -n 200 {logPath}\"", RedirectStandardOutput = true, UseShellExecute = false });
    return Results.Ok(new LogResponse(p?.StandardOutput.ReadToEnd() ?? ""));
});

app.MapGet("/api/system/logs", () => {
    using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"journalctl -u {ServiceName} -n 200 --no-pager\"", RedirectStandardOutput = true, UseShellExecute = false });
    return Results.Ok(new LogResponse(p?.StandardOutput.ReadToEnd() ?? ""));
});

app.MapGet("/api/cron", () => {
    var list = new List<CronEntity>();
    try
    {
        using var conn = new SqliteConnection(DbPath); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, Schedule, Command FROM cron_registry";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(new CronEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }
    catch { }
    return list;
});

app.MapPost("/api/cron", (CronRequest req) => {
    var id = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{req.Schedule} {req.Command}"));
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT OR REPLACE INTO cron_registry VALUES (@id, '任务', @sch, @cmd)";
    cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@sch", req.Schedule); cmd.Parameters.AddWithValue("@cmd", req.Command);
    cmd.ExecuteNonQuery();
    Process.Start("/bin/bash", $"-c \"(crontab -l 2>/dev/null; echo '{req.Schedule} {req.Command}') | crontab -\""); return Results.Ok(new { success = true });
});

app.MapDelete("/api/cron/{id}", (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM cron_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
    var lineToRemove = Encoding.UTF8.GetString(Convert.FromBase64String(id));
    Process.Start("/bin/bash", $"-c \"crontab -l | grep -vF '{lineToRemove}' | crontab -\""); return Results.Ok(new { success = true });
});

app.MapGet("/api/system/info", () => {
    var rawVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0-dev";

    var cpuTempRaw = GetBashOutput("ubnt-systool cputemp 2>/dev/null");
    var cpuTemp = string.IsNullOrEmpty(cpuTempRaw) ? "--" : cpuTempRaw + "°C";

    var sfpTempRaw = GetBashOutput("sensors 2>/dev/null | grep temp1 | awk '{print $2}'");
    var sfpTemp = string.IsNullOrEmpty(sfpTempRaw) ? "--" : sfpTempRaw.Replace("+", "");

    var uptimeRaw = GetBashOutput("uptime 2>/dev/null");
    var uptime = "--";
    if (!string.IsNullOrEmpty(uptimeRaw))
    {
        var match = Regex.Match(uptimeRaw, @"up\s+(.*?),\s+\d+\s+user");
        if (match.Success)
        {
            var u = match.Groups[1].Value;
            u = u.Replace("days,", "天").Replace("days", "天").Replace("day,", "天").Replace("day", "天").Replace("min,", "分钟").Replace("min", "分钟");
            u = Regex.Match(u, @"\d+:\d+").Success ? Regex.Replace(u, @"(\d+):(\d+)", "$1小时$2分钟") : u;
            uptime = u;
        }
        else { uptime = uptimeRaw; }
    }

    return Results.Ok(new SystemInfo(rawVersion.Split('+')[0], "NativeAOT-.NET10", "UCG-Fiber", cpuTemp, sfpTemp, uptime));
});

app.MapGet("/api/system/config", () => Results.Ok(sysConfig));

app.MapPost("/api/system/config", async (AppConfig newConfig) => {
    sysConfig.Port = newConfig.Port; sysConfig.AdminToken = newConfig.AdminToken; sysConfig.LocalProxy = newConfig.LocalProxy;
    await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(sysConfig, AppJsonContext.Default.AppConfig));
    Process.Start("systemd-run", $"--unit=sc_restarter --collect bash -c \"sleep 1 && systemctl restart {ServiceName}\""); return Results.Ok(new { success = true });
});

app.MapPost("/api/system/upgrade", () => {
    var proxy = sysConfig.LocalProxy;
    var scriptUrl = "https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/install.sh";
    if (File.Exists("/tmp/sc_update.log")) File.Delete("/tmp/sc_update.log");
    string curlCmd = !string.IsNullOrEmpty(proxy) ? $"curl -x {proxy} -sSL {scriptUrl} | bash -s '{proxy}' > /tmp/sc_update.log 2>&1" : $"curl -sSL {scriptUrl} | bash > /tmp/sc_update.log 2>&1";
    Process.Start("systemd-run", $"--unit=sc_updater --collect bash -c \"sleep 1 && {curlCmd}\""); return Results.Ok(new { success = true });
});

app.MapGet("/api/system/upgrade/log", () => {
    if (File.Exists("/tmp/sc_update.log")) return Results.Ok(new LogResponse(File.ReadAllText("/tmp/sc_update.log")));
    return Results.Ok(new LogResponse("正在准备更新环境..."));
});

app.Run($"http://0.0.0.0:{sysConfig.Port}");

public record AppConfig { public int Port { get; set; } = 9958; public string AdminToken { get; set; } = "Your_Secret_Token_Here"; public string LocalProxy { get; set; } = ""; }
public record AppEntity(string Id, string Name, string Type, string Icon, string StartCommand, string StopCommand, string StatusCommand, int IsAutoStart, bool IsRunning, string ConfigPath, string ConfigKeys, string LogPath, int SortOrder, string Version, string Description, string CustomCommands);
public record AppOrderReq(string Id, int SortOrder);
public record CronEntity(string Id, string Name, string Schedule, string Command);
public record CronRequest(string Schedule, string Command);
public record CustomCommandReq(string Command);
public record LogResponse(string Content);
public record SystemInfo(string Version, string Runtime, string Device, string CpuTemp, string SfpTemp, string Uptime);

public record ConfigItem(string Value, string Comment);

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AppEntity))]
[JsonSerializable(typeof(AppOrderReq))]
[JsonSerializable(typeof(CronEntity))]
[JsonSerializable(typeof(CronRequest))]
[JsonSerializable(typeof(CustomCommandReq))]
[JsonSerializable(typeof(LogResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(ConfigItem))]
[JsonSerializable(typeof(Dictionary<string, ConfigItem>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<AppEntity>))]
[JsonSerializable(typeof(List<AppOrderReq>))]
[JsonSerializable(typeof(List<CronEntity>))]
[JsonSerializable(typeof(IEnumerable<CronEntity>))]
internal partial class AppJsonContext : JsonSerializerContext { }