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

// ======================================================================
// 🚀 核心守护进程自动安装模块
// ======================================================================
const string ServiceName = "softcenter.service";
const string ServicePath = $"/etc/systemd/system/{ServiceName}";
string? currentExe = Process.GetCurrentProcess().MainModule?.FileName;

if (!string.IsNullOrEmpty(currentExe) && !File.Exists(ServicePath))
{
    Console.WriteLine("首次运行：正在自动注册 Systemd 服务...");
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
    Console.WriteLine("🎉 服务已转入后台守护运行！请访问端口 9958");
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

// --- 1. 路径与配置 ---
const string BaseDir = "/data/softcenter";
const string DbPath = $"Data Source={BaseDir}/manager.db";
const string ConfigPath = $"{BaseDir}/config.json";

if (!Directory.Exists(BaseDir)) Directory.CreateDirectory(BaseDir);
if (!Directory.Exists($"{BaseDir}/cronjobs")) Directory.CreateDirectory($"{BaseDir}/cronjobs");
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

// --- 2. 数据库初始化 ---
using (var conn = new SqliteConnection(DbPath))
{
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS apps_registry (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Type TEXT NOT NULL,
            Icon TEXT DEFAULT 'box',
            StartCommand TEXT NOT NULL,
            StopCommand TEXT NOT NULL,
            StatusCommand TEXT NOT NULL,
            IsAutoStart INTEGER DEFAULT 0,
            ConfigPath TEXT,
            ConfigKeys TEXT,
            LogPath TEXT
        );";
    cmd.ExecuteNonQuery();
}

// --- 3. 中间件与静态文件 ---
app.Use(async (context, next) => {
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var auth) || auth != sysConfig.AdminToken)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
    }
    await next();
});

var fileProvider = new PhysicalFileProvider($"{BaseDir}/web");
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider, RequestPath = "" });
app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider, RequestPath = "" });

// --- 4. 探针逻辑 ---
bool IsAppRunning(string cmdStr)
{
    try
    {
        var psi = new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{cmdStr}\"", UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode == 0;
    }
    catch { return false; }
}

// --- 5. 应用管理接口 ---
app.MapGet("/api/apps", () => {
    var apps = new List<AppEntity>();
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Name, Type, Icon, StartCommand, StopCommand, StatusCommand, IsAutoStart, ConfigPath, ConfigKeys, LogPath FROM apps_registry";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var statusCmd = reader.GetString(6);
        apps.Add(new AppEntity(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), statusCmd, reader.GetInt32(7),
            IsAppRunning(statusCmd), reader.IsDBNull(8) ? "" : reader.GetString(8),
            reader.IsDBNull(9) ? "" : reader.GetString(9), reader.IsDBNull(10) ? "" : reader.GetString(10)
        ));
    }
    return apps;
});

app.MapPost("/api/apps", (AppEntity newApp) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"INSERT OR REPLACE INTO apps_registry 
        (Id, Name, Type, Icon, StartCommand, StopCommand, StatusCommand, IsAutoStart, ConfigPath, ConfigKeys, LogPath) 
        VALUES (@Id, @Name, @Type, @Icon, @Start, @Stop, @Status, @Auto, @CPath, @CKeys, @LPath)";
    cmd.Parameters.AddWithValue("@Id", newApp.Id);
    cmd.Parameters.AddWithValue("@Name", newApp.Name);
    cmd.Parameters.AddWithValue("@Type", newApp.Type);
    cmd.Parameters.AddWithValue("@Icon", newApp.Icon ?? "box");
    cmd.Parameters.AddWithValue("@Start", newApp.StartCommand);
    cmd.Parameters.AddWithValue("@Stop", newApp.StopCommand);
    cmd.Parameters.AddWithValue("@Status", newApp.StatusCommand);
    cmd.Parameters.AddWithValue("@Auto", newApp.IsAutoStart);
    cmd.Parameters.AddWithValue("@CPath", newApp.ConfigPath ?? "");
    cmd.Parameters.AddWithValue("@CKeys", newApp.ConfigKeys ?? "");
    cmd.Parameters.AddWithValue("@LPath", newApp.LogPath ?? "");
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

app.MapPost("/api/apps/{id}/control", (string id, string action) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT StartCommand, StopCommand FROM apps_registry WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var exec = action == "start" ? reader.GetString(0) : (action == "stop" ? reader.GetString(1) : $"{reader.GetString(1)} ; sleep 1 ; {reader.GetString(0)}");
    Process.Start(new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{exec}\"", UseShellExecute = false });
    return Results.Ok();
});

app.MapPut("/api/apps/{id}/autostart", (string id, int state) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE apps_registry SET IsAutoStart = @state WHERE Id = @id";
    cmd.Parameters.AddWithValue("@state", state);
    cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery();
    return Results.Ok();
});

// --- 6. 配置读写接口 ---
app.MapGet("/api/apps/{id}/config", (string id) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath, ConfigKeys FROM apps_registry WHERE Id = @id";
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

app.MapPost("/api/apps/{id}/config", async (string id, Dictionary<string, string> payload) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath FROM apps_registry WHERE Id = @id";
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

// --- 7. 日志与定时任务接口 ---
app.MapGet("/api/apps/{id}/logs", (string id) => {
    using var conn = new SqliteConnection(DbPath);
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT LogPath FROM apps_registry WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    var logPath = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return Results.Ok(new LogResponse("无日志或未配置。"));
    var psi = new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"tail -n 200 {logPath}\"", RedirectStandardOutput = true, UseShellExecute = false };
    using var p = Process.Start(psi);
    return Results.Ok(new LogResponse(p?.StandardOutput.ReadToEnd() ?? ""));
});

app.MapGet("/api/system/logs", () => {
    var psi = new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"journalctl -u {ServiceName} -n 200 --no-pager\"", RedirectStandardOutput = true, UseShellExecute = false };
    using var p = Process.Start(psi);
    return Results.Ok(new LogResponse(p?.StandardOutput.ReadToEnd() ?? ""));
});

app.MapGet("/api/cron", () => {
    var list = new List<CronEntity>();
    try
    {
        var psi = new ProcessStartInfo { FileName = "/bin/bash", Arguments = "-c \"crontab -l 2>/dev/null\"", RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        foreach (var l in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (l.TrimStart().StartsWith("#")) continue;
            var parts = l.Split(new[] { ' ', '\t' }, 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6)
            {
                list.Add(new CronEntity(Convert.ToBase64String(Encoding.UTF8.GetBytes(l)), "任务", string.Join(" ", parts.Take(5)), parts[5]));
            }
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

// --- 8. API 接口：获取系统版本与自升级 ---
app.MapGet("/api/system/info", () => {
    var version = Assembly.GetExecutingAssembly()
                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion ?? "1.0.0-dev";
    return Results.Ok(new SystemInfo(version, "NativeAOT-.NET10", "UCG-Fiber"));
});

app.MapPost("/api/system/upgrade", () => {
    var scriptUrl = "https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/install.sh";
    var command = $"sleep 2 && curl -sSL {scriptUrl} | bash";
    Process.Start(new ProcessStartInfo
    {
        FileName = "systemd-run",
        Arguments = $"--unit=sc_updater --collect bash -c \"{command}\"",
        UseShellExecute = false,
        CreateNoWindow = true
    });
    return Results.Ok();
});

app.Run($"http://0.0.0.0:{sysConfig.Port}");

// ======================================================================
// AOT 序列化与数据模型
// ======================================================================
public class AppConfig { public int Port { get; set; } = 9958; public string AdminToken { get; set; } = "Your_Secret_Token_Here"; }
public record AppEntity(string Id, string Name, string Type, string Icon, string StartCommand, string StopCommand, string StatusCommand, int IsAutoStart, bool IsRunning, string ConfigPath, string ConfigKeys, string LogPath);
public record CronEntity(string Id, string Name, string Schedule, string Command);
public record CronRequest(string Schedule, string Command);
public record LogResponse(string Content);
public record SystemInfo(string Version, string Runtime, string Device);

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AppEntity))]
[JsonSerializable(typeof(CronEntity))]
[JsonSerializable(typeof(CronRequest))]
[JsonSerializable(typeof(LogResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<AppEntity>))]
[JsonSerializable(typeof(List<CronEntity>))]
[JsonSerializable(typeof(IEnumerable<CronEntity>))]
[JsonSerializable(typeof(SystemInfo))]
internal partial class AppJsonContext : JsonSerializerContext { }