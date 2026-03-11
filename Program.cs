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

// 自动注册 Systemd 服务
if (!string.IsNullOrEmpty(currentExe) && !File.Exists(ServicePath))
{
    string serviceContent = $@"
[Unit]
Description=UniFi SoftCenter Manager
After=network.target

[Service]
Type=simple
ExecStart={currentExe}
WorkingDirectory=/data/softcenter
Restart=always
RestartSec=5

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
            IsAutoStart INTEGER DEFAULT 0, ConfigPath TEXT, ConfigKeys TEXT, LogPath TEXT, SortOrder INTEGER DEFAULT 0
        );";
    cmd.ExecuteNonQuery();

    // 自动为旧版本数据库升级表结构，补充 SortOrder 字段
    try
    {
        using var cmdAlter = conn.CreateCommand();
        cmdAlter.CommandText = "ALTER TABLE apps_registry ADD COLUMN SortOrder INTEGER DEFAULT 0;";
        cmdAlter.ExecuteNonQuery();
    }
    catch { /* 字段已存在则忽略 */ }
}

app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var auth) || auth != sysConfig.AdminToken)
        {
            context.Response.StatusCode = 401;
            return;
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
        p?.WaitForExit();
        return p?.ExitCode == 0;
    }
    catch { return false; }
}

// 获取应用列表 (加入 SortOrder 排序)
app.MapGet("/api/apps", () => {
    var apps = new List<AppEntity>();
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Name, Type, Icon, StartCommand, StopCommand, StatusCommand, IsAutoStart, ConfigPath, ConfigKeys, LogPath, SortOrder FROM apps_registry ORDER BY SortOrder ASC, Id ASC";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var sCmd = reader.GetString(6);
        apps.Add(new AppEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), sCmd, reader.GetInt32(7), IsAppRunning(sCmd), reader.IsDBNull(8) ? "" : reader.GetString(8), reader.IsDBNull(9) ? "" : reader.GetString(9), reader.IsDBNull(10) ? "" : reader.GetString(10), reader.IsDBNull(11) ? 0 : reader.GetInt32(11)));
    }
    return apps;
});

// 新增/覆盖应用
app.MapPost("/api/apps", (AppEntity a) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT OR REPLACE INTO apps_registry VALUES (@Id,@Name,@Type,@Icon,@Start,@Stop,@Status,@Auto,@CPath,@CKeys,@LPath,@SortOrder)";
    cmd.Parameters.AddWithValue("@Id", a.Id); cmd.Parameters.AddWithValue("@Name", a.Name); cmd.Parameters.AddWithValue("@Type", a.Type);
    cmd.Parameters.AddWithValue("@Icon", a.Icon ?? "box"); cmd.Parameters.AddWithValue("@Start", a.StartCommand); cmd.Parameters.AddWithValue("@Stop", a.StopCommand);
    cmd.Parameters.AddWithValue("@Status", a.StatusCommand); cmd.Parameters.AddWithValue("@Auto", a.IsAutoStart);
    cmd.Parameters.AddWithValue("@CPath", a.ConfigPath ?? ""); cmd.Parameters.AddWithValue("@CKeys", a.ConfigKeys ?? ""); cmd.Parameters.AddWithValue("@LPath", a.LogPath ?? "");
    cmd.Parameters.AddWithValue("@SortOrder", a.SortOrder);
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

// 删除应用
app.MapDelete("/api/apps/{id}", (string id) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM apps_registry WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

// 拖拽排序保存接口
app.MapPost("/api/apps/reorder", (List<AppOrderReq> req) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var tx = conn.BeginTransaction();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE apps_registry SET SortOrder = @so WHERE Id = @id";
    var pId = cmd.Parameters.Add("@id", SqliteType.Text);
    var pSo = cmd.Parameters.Add("@so", SqliteType.Integer);
    foreach (var r in req)
    {
        pId.Value = r.Id; pSo.Value = r.SortOrder;
        cmd.ExecuteNonQuery();
    }
    tx.Commit();
    return Results.Ok();
});

// 控制启停
app.MapPost("/api/apps/{id}/control", (string id, string action) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT StartCommand, StopCommand FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var exec = action == "start" ? reader.GetString(0) : (action == "stop" ? reader.GetString(1) : $"{reader.GetString(1)};sleep 1;{reader.GetString(0)}");
    Process.Start("/bin/bash", $"-c \"{exec}\"");
    return Results.Ok();
});

// 开机自启状态切换
app.MapPut("/api/apps/{id}/autostart", (string id, int state) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE apps_registry SET IsAutoStart=@state WHERE Id=@id";
    cmd.Parameters.AddWithValue("@state", state); cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

// 获取配置
app.MapGet("/api/apps/{id}/config", (string id) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath, ConfigKeys FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1)) return Results.NotFound();
    var path = reader.GetString(0);
    var keys = reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries);
    var dict = new Dictionary<string, string>();
    if (File.Exists(path))
    {
        var content = File.ReadAllText(path);
        foreach (var k in keys.Select(x => x.Trim()))
        {
            var m = Regex.Match(content, $@"{k}=['""]?([^'""\n\r]*)['""]?");
            dict[k] = m.Success ? m.Groups[1].Value : "";
        }
    }
    return Results.Ok(dict);
});

// 保存配置
app.MapPost("/api/apps/{id}/config", async (string id, Dictionary<string, string> payload) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    var path = cmd.ExecuteScalar()?.ToString();
    if (!string.IsNullOrEmpty(path) && File.Exists(path))
    {
        var content = await File.ReadAllTextAsync(path);
        foreach (var kv in payload) content = Regex.Replace(content, $@"{kv.Key}=['""]?[^'""\n\r]*['""]?", $"{kv.Key}=\"{kv.Value}\"");
        await File.WriteAllTextAsync(path, content);
        return Results.Ok();
    }
    return Results.NotFound();
});

// 日志拉取
app.MapGet("/api/apps/{id}/logs", (string id) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT LogPath FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    var logPath = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return Results.Ok(new LogResponse("无日志或未配置。"));
    using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"tail -n 200 {logPath}\"", RedirectStandardOutput = true, UseShellExecute = false });
    return Results.Ok(new LogResponse(p?.StandardOutput.ReadToEnd() ?? ""));
});

// 系统日志拉取
app.MapGet("/api/system/logs", () => {
    using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"journalctl -u {ServiceName} -n 200 --no-pager\"", RedirectStandardOutput = true, UseShellExecute = false });
    return Results.Ok(new LogResponse(p?.StandardOutput.ReadToEnd() ?? ""));
});

// Cron 管理
app.MapGet("/api/cron", () => {
    var list = new List<CronEntity>();
    try
    {
        using var p = Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = "-c \"crontab -l 2>/dev/null\"", RedirectStandardOutput = true, UseShellExecute = false });
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        foreach (var l in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (l.TrimStart().StartsWith("#")) continue;
            var parts = l.Split(new[] { ' ', '\t' }, 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6) list.Add(new CronEntity(Convert.ToBase64String(Encoding.UTF8.GetBytes(l)), "任务", string.Join(" ", parts.Take(5)), parts[5]));
        }
    }
    catch { }
    return list;
});

app.MapPost("/api/cron", (CronRequest req) => {
    Process.Start("/bin/bash", $"-c \"(crontab -l 2>/dev/null; echo '{req.Schedule} {req.Command}') | crontab -\"");
    return Results.Ok();
});

app.MapDelete("/api/cron/{id}", (string id) => {
    var lineToRemove = Encoding.UTF8.GetString(Convert.FromBase64String(id));
    Process.Start("/bin/bash", $"-c \"crontab -l | grep -vF '{lineToRemove}' | crontab -\"");
    return Results.Ok();
});

// 系统信息与一键更新
app.MapGet("/api/system/info", () => {
    var rawVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0-dev";
    var version = rawVersion.Split('+')[0];
    return Results.Ok(new SystemInfo(version, "NativeAOT-.NET10", "UCG-Fiber"));
});

app.MapPost("/api/system/upgrade", () => {
    var proxy = sysConfig.GithubProxy;
    if (!string.IsNullOrEmpty(proxy) && !proxy.EndsWith("/")) proxy += "/";
    var scriptUrl = $"{proxy}https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/install.sh";

    if (File.Exists("/tmp/sc_update.log")) File.Delete("/tmp/sc_update.log");

    Process.Start("systemd-run", $"--unit=sc_updater --collect bash -c \"sleep 1 && curl -sSL {scriptUrl} | bash -s '{proxy}' > /tmp/sc_update.log 2>&1\"");
    return Results.Ok();
});

app.MapGet("/api/system/upgrade/log", () => {
    if (File.Exists("/tmp/sc_update.log"))
    {
        return Results.Ok(new LogResponse(File.ReadAllText("/tmp/sc_update.log")));
    }
    return Results.Ok(new LogResponse("正在准备更新环境..."));
});

app.Run($"http://0.0.0.0:{sysConfig.Port}");

public record AppConfig { public int Port { get; set; } = 9958; public string AdminToken { get; set; } = "Your_Secret_Token_Here"; public string GithubProxy { get; set; } = "https://cdn.gh-proxy.org/"; }
public record AppEntity(string Id, string Name, string Type, string Icon, string StartCommand, string StopCommand, string StatusCommand, int IsAutoStart, bool IsRunning, string ConfigPath, string ConfigKeys, string LogPath, int SortOrder);
public record AppOrderReq(string Id, int SortOrder);
public record CronEntity(string Id, string Name, string Schedule, string Command);
public record CronRequest(string Schedule, string Command);
public record LogResponse(string Content);
public record SystemInfo(string Version, string Runtime, string Device);

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AppEntity))]
[JsonSerializable(typeof(AppOrderReq))]
[JsonSerializable(typeof(CronEntity))]
[JsonSerializable(typeof(CronRequest))]
[JsonSerializable(typeof(LogResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<AppEntity>))]
[JsonSerializable(typeof(List<AppOrderReq>))]
[JsonSerializable(typeof(List<CronEntity>))]
[JsonSerializable(typeof(IEnumerable<CronEntity>))]
internal partial class AppJsonContext : JsonSerializerContext { }