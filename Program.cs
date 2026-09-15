using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using System.Text.Json;
using System.Text.Json.Nodes;
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

// 已有服务单元时补写 KillMode=process，避免升级 stop 时连带杀掉插件子进程
if (!string.IsNullOrEmpty(currentExe) && File.Exists(ServicePath))
{
    try
    {
        var svc = File.ReadAllText(ServicePath);
        if (!svc.Contains("KillMode=process"))
        {
            if (svc.Contains("KillMode="))
                svc = Regex.Replace(svc, @"KillMode=\w+", "KillMode=process");
            else if (svc.Contains("[Service]"))
                svc = svc.Replace("[Service]", "[Service]\nKillMode=process");
            else
                svc += "\n[Service]\nKillMode=process\n";
            File.WriteAllText(ServicePath, svc);
            Process.Start("systemctl", "daemon-reload")?.WaitForExit();
        }
    }
    catch { }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

const string BaseDir = "/data/softcenter";
const string DbPath = $"Data Source={BaseDir}/manager.db";
const string ConfigPath = $"{BaseDir}/config.json";
const string BinDir = $"{BaseDir}/bin";
const string RepoRawBase = "https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/";

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
            Version TEXT DEFAULT '1.0.0', Description TEXT DEFAULT '', CustomCommands TEXT DEFAULT '[]', ConfigSchema TEXT DEFAULT '', Files TEXT DEFAULT '[]'
        );
        CREATE TABLE IF NOT EXISTS cron_registry (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Schedule TEXT NOT NULL, Command TEXT NOT NULL
        );";
    cmd.ExecuteNonQuery();
    try { using var c1 = conn.CreateCommand(); c1.CommandText = "ALTER TABLE apps_registry ADD COLUMN SortOrder INTEGER DEFAULT 0;"; c1.ExecuteNonQuery(); } catch { }
    try { using var c2 = conn.CreateCommand(); c2.CommandText = "ALTER TABLE apps_registry ADD COLUMN Version TEXT DEFAULT '0.0.1';"; c2.ExecuteNonQuery(); } catch { }
    try { using var c3 = conn.CreateCommand(); c3.CommandText = "ALTER TABLE apps_registry ADD COLUMN Description TEXT DEFAULT '';"; c3.ExecuteNonQuery(); } catch { }
    try { using var c4 = conn.CreateCommand(); c4.CommandText = "ALTER TABLE apps_registry ADD COLUMN CustomCommands TEXT DEFAULT '[]';"; c4.ExecuteNonQuery(); } catch { }
    try { using var c5 = conn.CreateCommand(); c5.CommandText = "ALTER TABLE apps_registry ADD COLUMN ConfigSchema TEXT DEFAULT '';"; c5.ExecuteNonQuery(); } catch { }
    try { using var c6 = conn.CreateCommand(); c6.CommandText = "ALTER TABLE apps_registry ADD COLUMN Files TEXT DEFAULT '[]';"; c6.ExecuteNonQuery(); } catch { }
}

string bootLock = "/tmp/softcenter_booted.lock";
string? DeviceModelCache = null;
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

    File.WriteAllText(bootLock, DateTime.Now.ToString());
}

// SoftCenter 每次启动（系统开机、服务重启、OTA 升级）都拉起「已开启自启且当前未运行」的插件
// 不依赖 boot lock：升级后 /tmp lock 仍在，若只在首次开机恢复会导致插件全部停着
_ = Task.Run(async () => {
    await Task.Delay(2500);
    try
    {
        var toStart = new List<(string Id, string StartCmd)>();
        using (var conn = new SqliteConnection(DbPath))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, StartCommand, StatusCommand FROM apps_registry WHERE IsAutoStart=1";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var startCmd = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var statusCmd = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (string.IsNullOrWhiteSpace(startCmd)) continue;
                var running = false;
                if (!string.IsNullOrWhiteSpace(statusCmd))
                {
                    try
                    {
                        using var p = Process.Start(new ProcessStartInfo
                        {
                            FileName = "/bin/bash",
                            Arguments = $"-c \"{statusCmd}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        p?.WaitForExit(5000);
                        running = p?.ExitCode == 0;
                    }
                    catch { }
                }
                if (!running) toStart.Add((id, startCmd));
            }
        }
        foreach (var (id, startCmd) in toStart)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{startCmd}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            catch { }
        }
    }
    catch { }
});

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

static string AppEnvDir() => $"{BaseDir}/config";
static string AppEnvPath(string appId) => $"{AppEnvDir()}/{appId}.env";

// 卸载插件：优先按 Files[].Name 清理，再按插件 Id 前缀兜底
static void CleanupAppBinFiles(string appId, string? filesJson = null)
{
    if (string.IsNullOrWhiteSpace(appId)) return;
    if (appId.Contains("..") || appId.Contains('/') || appId.Contains('\\') || appId.Contains('\0')) return;
    if (!Directory.Exists(BinDir)) return;
    try
    {
        // 1) Files 清单（ssup/ssrule 等非 Id 前缀文件也能删掉）
        if (!string.IsNullOrWhiteSpace(filesJson))
        {
            try
            {
                var files = JsonSerializer.Deserialize(filesJson, AppJsonContext.Default.ListAppFileItem);
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        if (string.IsNullOrWhiteSpace(f.Name)) continue;
                        var rel = f.Name.Replace('\\', '/').TrimStart('/');
                        if (rel.Contains("..") || rel.StartsWith('/')) continue;
                        var path = Path.Combine(BinDir, rel);
                        try
                        {
                            if (Directory.Exists(path)) Directory.Delete(path, true);
                            else if (File.Exists(path)) File.Delete(path);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // 2) Id 前缀兜底（webssh-dl / koolssrule 等）
        foreach (var path in Directory.EnumerateFileSystemEntries(BinDir))
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith(appId, StringComparison.Ordinal)) continue;
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else File.Delete(path);
            }
            catch { }
        }

        var env = AppEnvPath(appId);
        if (File.Exists(env)) File.Delete(env);
    }
    catch { }
}

static string JsonGetString(JsonElement el, string name)
{
    if (el.ValueKind != JsonValueKind.Object) return "";
    if (el.TryGetProperty(name, out var v) || el.TryGetProperty(char.ToLowerInvariant(name[0]) + name.Substring(1), out v))
    {
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Null => "",
            _ => v.GetRawText()
        };
    }
    return "";
}

static bool JsonGetBool(JsonElement el, string name)
{
    if (el.ValueKind != JsonValueKind.Object) return false;
    if (el.TryGetProperty(name, out var v) || el.TryGetProperty(char.ToLowerInvariant(name[0]) + name.Substring(1), out v))
    {
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString() is "1" or "true" or "True",
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => false
        };
    }
    return false;
}

static int? JsonGetInt(JsonElement el, string name)
{
    if (el.ValueKind != JsonValueKind.Object) return null;
    if (el.TryGetProperty(name, out var v) || el.TryGetProperty(char.ToLowerInvariant(name[0]) + name.Substring(1), out v))
    {
        if (v.ValueKind == JsonValueKind.Number) return v.GetInt32();
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var n)) return n;
    }
    return null;
}

static IEnumerable<JsonElement> EnumerateSchemaFields(string schemaJson)
{
    if (string.IsNullOrWhiteSpace(schemaJson)) yield break;
    JsonDocument doc;
    try { doc = JsonDocument.Parse(schemaJson); } catch { yield break; }
    using (doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("Sections", out var sections) && !root.TryGetProperty("sections", out sections)) yield break;
        if (sections.ValueKind != JsonValueKind.Array) yield break;
        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("Fields", out var fields) && !section.TryGetProperty("fields", out fields)) continue;
            if (fields.ValueKind != JsonValueKind.Array) continue;
            foreach (var field in fields.EnumerateArray()) yield return field.Clone();
        }
    }
}

static string? ValidateConfigValue(JsonElement field, string value)
{
    var type = JsonGetString(field, "Type").ToLowerInvariant();
    if (string.IsNullOrEmpty(type)) type = "text";
    var label = JsonGetString(field, "Label");
    if (string.IsNullOrEmpty(label)) label = JsonGetString(field, "Key");
    var required = JsonGetBool(field, "Required");

    if (required && string.IsNullOrWhiteSpace(value)) return $"{label} 不能为空";

    if (string.IsNullOrWhiteSpace(value)) return null;

    switch (type)
    {
        case "number":
            if (!long.TryParse(value, out var num)) return $"{label} 必须是数字";
            var min = JsonGetInt(field, "Min");
            var max = JsonGetInt(field, "Max");
            if (min.HasValue && num < min.Value) return $"{label} 不能小于 {min.Value}";
            if (max.HasValue && num > max.Value) return $"{label} 不能大于 {max.Value}";
            break;
        case "port":
            if (!int.TryParse(value, out var port) || port < 1 || port > 65535) return $"{label} 必须是 1-65535 的端口";
            break;
        case "ip":
            if (!Regex.IsMatch(value, @"^(\d{1,3}\.){3}\d{1,3}$") && !Regex.IsMatch(value, @"^[0-9a-fA-F:]+$"))
                return $"{label} 不是合法的 IP 地址";
            break;
        case "switch":
            if (value is not ("0" or "1" or "true" or "false")) return $"{label} 开关值无效";
            break;
        case "select":
            if (field.TryGetProperty("Options", out var options) || field.TryGetProperty("options", out options))
            {
                if (options.ValueKind == JsonValueKind.Array)
                {
                    var ok = false;
                    foreach (var opt in options.EnumerateArray())
                    {
                        var ov = JsonGetString(opt, "Value");
                        if (string.IsNullOrEmpty(ov)) ov = JsonGetString(opt, "value");
                        if (ov == value) { ok = true; break; }
                    }
                    if (!ok) return $"{label} 的选项无效";
                }
            }
            break;
        case "multi-select":
            if (field.TryGetProperty("Options", out options) || field.TryGetProperty("options", out options))
            {
                if (options.ValueKind == JsonValueKind.Array)
                {
                    var allowed = new HashSet<string>();
                    foreach (var opt in options.EnumerateArray())
                    {
                        var ov = JsonGetString(opt, "Value");
                        if (string.IsNullOrEmpty(ov)) ov = JsonGetString(opt, "value");
                        if (!string.IsNullOrEmpty(ov)) allowed.Add(ov);
                    }
                    foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!allowed.Contains(part)) return $"{label} 包含非法选项 {part}";
                    }
                }
            }
            break;
    }
    return null;
}

static string EnvQuote(string value)
{
    if (string.IsNullOrEmpty(value)) return "";
    if (Regex.IsMatch(value, @"^[A-Za-z0-9_./:@+-]+$")) return value;
    return "'" + value.Replace("'", "'\\''") + "'";
}

static string NormalizeSwitch(string value) => value is "true" or "True" or "TRUE" ? "1" : value is "false" or "False" or "FALSE" ? "0" : value;

// 多行文本在 env 中存为单行 b64:...，读写时还原
static string EncodeEnvValue(string value)
{
    if (string.IsNullOrEmpty(value)) return "";
    if (value.Contains('\n') || value.Contains('\r'))
        return "b64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    return value;
}

static string DecodeEnvValue(string value)
{
    if (!string.IsNullOrEmpty(value) && value.StartsWith("b64:", StringComparison.Ordinal))
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value[4..])); }
        catch { return value; }
    }
    return value;
}

static Dictionary<string, string> DecodeEnvDict(Dictionary<string, string> dict)
{
    var outDict = new Dictionary<string, string>(dict.Count);
    foreach (var kv in dict) outDict[kv.Key] = DecodeEnvValue(kv.Value);
    return outDict;
}

// config.env 始终按行 KEY=VALUE；多行值经 EncodeEnvValue 存为 b64: 单行
static Dictionary<string, string> ReadEnvFile(string path)
{
    var dict = new Dictionary<string, string>();
    if (!File.Exists(path)) return dict;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        var idx = line.IndexOf('=');
        if (idx <= 0) continue;
        var key = line[..idx].Trim();
        var val = line[(idx + 1)..].Trim();
        if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'')
            val = val[1..^1].Replace("'\\''", "'");
        else if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
            val = val[1..^1];
        dict[key] = val;
    }
    return dict;
}

static Dictionary<string, string> BuildSchemaDefaults(string schemaJson)
{
    var values = new Dictionary<string, string>();
    foreach (var field in EnumerateSchemaFields(schemaJson))
    {
        var key = JsonGetString(field, "Key");
        if (string.IsNullOrEmpty(key)) continue;
        var def = JsonGetString(field, "Default");
        values[key] = NormalizeSwitch(def);
    }
    return values;
}

app.MapGet("/api/apps", () => {
    var apps = new List<AppEntity>();
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Name, Type, Icon, StartCommand, StopCommand, StatusCommand, IsAutoStart, ConfigPath, ConfigKeys, LogPath, SortOrder, Version, Description, CustomCommands, ConfigSchema, Files FROM apps_registry ORDER BY SortOrder ASC, Id ASC";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var sCmd = reader.GetString(6);
        apps.Add(new AppEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), sCmd, reader.GetInt32(7), IsAppRunning(sCmd), reader.IsDBNull(8) ? "" : reader.GetString(8), reader.IsDBNull(9) ? "" : reader.GetString(9), reader.IsDBNull(10) ? "" : reader.GetString(10), reader.IsDBNull(11) ? 0 : reader.GetInt32(11), reader.IsDBNull(12) ? "1.0.0" : reader.GetString(12), reader.IsDBNull(13) ? "" : reader.GetString(13), reader.IsDBNull(14) ? "[]" : reader.GetString(14), reader.IsDBNull(15) ? "" : reader.GetString(15), reader.IsDBNull(16) ? "[]" : reader.GetString(16)));
    }
    return apps;
});

app.MapPost("/api/apps", (AppEntity a) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT OR REPLACE INTO apps_registry VALUES (@Id,@Name,@Type,@Icon,@Start,@Stop,@Status,@Auto,@CPath,@CKeys,@LPath,@SortOrder,@Ver,@Desc,@Custom,@Schema,@Files)";
    cmd.Parameters.AddWithValue("@Id", a.Id); cmd.Parameters.AddWithValue("@Name", a.Name); cmd.Parameters.AddWithValue("@Type", a.Type);
    cmd.Parameters.AddWithValue("@Icon", a.Icon ?? "box"); cmd.Parameters.AddWithValue("@Start", a.StartCommand); cmd.Parameters.AddWithValue("@Stop", a.StopCommand);
    cmd.Parameters.AddWithValue("@Status", a.StatusCommand); cmd.Parameters.AddWithValue("@Auto", a.IsAutoStart);
    cmd.Parameters.AddWithValue("@CPath", a.ConfigPath ?? ""); cmd.Parameters.AddWithValue("@CKeys", a.ConfigKeys ?? ""); cmd.Parameters.AddWithValue("@LPath", a.LogPath ?? "");
    cmd.Parameters.AddWithValue("@SortOrder", a.SortOrder); cmd.Parameters.AddWithValue("@Ver", a.Version ?? "0.0.1"); cmd.Parameters.AddWithValue("@Desc", a.Description ?? "");
    cmd.Parameters.AddWithValue("@Custom", a.CustomCommands ?? "[]"); cmd.Parameters.AddWithValue("@Schema", a.ConfigSchema ?? ""); cmd.Parameters.AddWithValue("@Files", a.Files ?? "[]");
    cmd.ExecuteNonQuery(); return Results.Ok(new SimpleSuccess(true));
});

app.MapDelete("/api/apps/{id}", (string id) => {
    string? filesJson = null;
    using (var conn = new SqliteConnection(DbPath))
    {
        conn.Open();
        using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT Files FROM apps_registry WHERE Id=@id";
        sel.Parameters.AddWithValue("@id", id);
        filesJson = sel.ExecuteScalar()?.ToString();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM apps_registry WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }
    CleanupAppBinFiles(id, filesJson);
    return Results.Ok(new SimpleSuccess(true));
});

app.MapPost("/api/apps/reorder", (List<AppOrderReq> req) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var tx = conn.BeginTransaction();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE apps_registry SET SortOrder = @so WHERE Id = @id";
    var pId = cmd.Parameters.Add("@id", SqliteType.Text);
    var pSo = cmd.Parameters.Add("@so", SqliteType.Integer);
    foreach (var r in req) { pId.Value = r.Id; pSo.Value = r.SortOrder; cmd.ExecuteNonQuery(); }
    tx.Commit(); return Results.Ok(new SimpleSuccess(true));
});

app.MapPost("/api/apps/{id}/control", (string id, string action) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT StartCommand, StopCommand FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var startCmd = reader.GetString(0);
    var stopCmd = reader.GetString(1);
    reader.Close();
    var exec = action == "start" ? startCmd : (action == "stop" ? stopCmd : $"{stopCmd};sleep 1;{startCmd}");
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{exec}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new InstallResult(false, null, $"启动命令失败: {ex.Message}"));
    }
    return Results.Ok(new SimpleSuccess(true));
});

// 安装/更新进度（内存态，供前端轮询）
var InstallProgressStore = new ConcurrentDictionary<string, InstallProgress>();

void SetInstallProgress(string appId, InstallProgress p) => InstallProgressStore[appId] = p;

// 从云端下载插件运行文件并校验 SHA256，写入 /data/softcenter/bin
// 全部下载并校验通过后才落盘，避免半更新；下载过程写入进度供前端展示
app.MapPost("/api/apps/{id}/install", async (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Files, StopCommand, StartCommand, StatusCommand FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    string? stopCmd = null;
    string? startCmd = null;
    string? statusCmd = null;
    string? filesJson;
    using (var reader = cmd.ExecuteReader())
    {
        if (!reader.Read())
        {
            SetInstallProgress(id, new InstallProgress(id, "error", 0, 0, "", 0, null, "插件不存在", true, false));
            return Results.NotFound(new InstallResult(false, null, "插件不存在"));
        }
        filesJson = reader.IsDBNull(0) ? null : reader.GetString(0);
        stopCmd = reader.IsDBNull(1) ? null : reader.GetString(1);
        startCmd = reader.IsDBNull(2) ? null : reader.GetString(2);
        statusCmd = reader.IsDBNull(3) ? null : reader.GetString(3);
    }
    if (string.IsNullOrWhiteSpace(filesJson))
    {
        SetInstallProgress(id, new InstallProgress(id, "done", 0, 0, "", 0, null, "无运行文件需要下载", true, true));
        return Results.Ok(new InstallResult(true, new List<string> { "无运行文件需要下载" }, null));
    }

    List<AppFileItem>? files;
    try { files = JsonSerializer.Deserialize(filesJson, AppJsonContext.Default.ListAppFileItem); }
    catch
    {
        SetInstallProgress(id, new InstallProgress(id, "error", 0, 0, "", 0, null, "Files JSON 无效", true, false));
        return Results.BadRequest(new InstallResult(false, null, "Files JSON 无效"));
    }
    if (files is null || files.Count == 0)
    {
        SetInstallProgress(id, new InstallProgress(id, "done", 0, 0, "", 0, null, "无运行文件需要下载", true, true));
        return Results.Ok(new InstallResult(true, new List<string> { "无运行文件需要下载" }, null));
    }

    Directory.CreateDirectory(BinDir);
    var logs = new List<string>();
    var handler = new HttpClientHandler();
    if (!string.IsNullOrWhiteSpace(sysConfig.LocalProxy))
        handler.Proxy = new WebProxy(sysConfig.LocalProxy);
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

    InstallProgress Fail(string phase, string msg, int idx = 0, string name = "")
    {
        var p = new InstallProgress(id, phase, idx, files.Count, name, 0, null, msg, true, false);
        SetInstallProgress(id, p);
        return p;
    }

    // 阶段 1：全部下载 + SHA256 校验，任一失败则不写入任何文件
    var pending = new List<(AppFileItem File, byte[] Bytes, string Hash)>();
    for (int i = 0; i < files.Count; i++)
    {
        var f = files[i];
        if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.Path) || string.IsNullOrWhiteSpace(f.Sha256))
        {
            Fail("error", $"文件清单不完整: {f.Name}，已中止，未替换任何文件", i, f.Name);
            return Results.BadRequest(new InstallResult(false, logs, $"文件清单不完整: {f.Name}，已中止，未替换任何文件"));
        }

        var url = RepoRawBase + f.Path.TrimStart('/');
        SetInstallProgress(id, new InstallProgress(id, "downloading", i + 1, files.Count, f.Name, 0, null, $"正在下载 {f.Name}", false, false));

        byte[] bytes;
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var stream = await resp.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            long received = 0;
            int n;
            long lastTick = Environment.TickCount64;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                ms.Write(buffer, 0, n);
                received += n;
                var now = Environment.TickCount64;
                if (now - lastTick >= 200)
                {
                    lastTick = now;
                    SetInstallProgress(id, new InstallProgress(id, "downloading", i + 1, files.Count, f.Name, received, total, $"正在下载 {f.Name}", false, false));
                }
            }
            bytes = ms.ToArray();
            SetInstallProgress(id, new InstallProgress(id, "verifying", i + 1, files.Count, f.Name, received, total, $"正在校验 {f.Name}", false, false));
        }
        catch (Exception ex)
        {
            Fail("error", $"下载失败 {f.Name}: {ex.Message}，已中止，未替换任何文件", i + 1, f.Name);
            return Results.BadRequest(new InstallResult(false, logs, $"下载失败 {f.Name}: {ex.Message}，已中止，未替换任何文件"));
        }

        string hash;
        using (var sha = SHA256.Create())
            hash = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, f.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            logs.Add($"校验失败 {f.Name}: 期望 {f.Sha256} 实际 {hash}");
            Fail("error", $"SHA256 校验失败: {f.Name}，已中止，未替换任何文件", i + 1, f.Name);
            return Results.BadRequest(new InstallResult(false, logs, $"SHA256 校验失败: {f.Name}，已中止，未替换任何文件"));
        }

        pending.Add((f, bytes, hash));
        logs.Add($"已下载并校验 {f.Name} ({hash[..12]}…)");
        SetInstallProgress(id, new InstallProgress(id, "downloading", i + 1, files.Count, f.Name, bytes.Length, bytes.Length, $"{f.Name} 校验通过", false, false));
    }

    // 阶段 2：若插件正在运行则先停掉，替换完成后按原状态恢复
    var wasRunning = false;
    if (!string.IsNullOrWhiteSpace(statusCmd))
    {
        try
        {
            using var st = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{statusCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            st?.WaitForExit(5000);
            wasRunning = st?.ExitCode == 0;
        }
        catch { }
    }

    if (wasRunning && !string.IsNullOrWhiteSpace(stopCmd))
    {
        SetInstallProgress(id, new InstallProgress(id, "writing", 0, pending.Count, "", 0, null, "正在停止运行中的插件…", false, false));
        logs.Add($"插件原为运行中，执行停止: {stopCmd}");
        try
        {
            using var stopP = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{stopCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            stopP?.WaitForExit(15000);
        }
        catch { }
        await Task.Delay(800);
    }

    // 全部就绪后再统一写入
    for (int i = 0; i < pending.Count; i++)
    {
        var (f, bytes, hash) = pending[i];
        SetInstallProgress(id, new InstallProgress(id, "writing", i + 1, pending.Count, f.Name, bytes.Length, bytes.Length, $"正在写入 {f.Name}", false, false));
        var dest = Path.Combine(BinDir, f.Name.Replace('\\', '/'));
        var destDir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        try
        {
            // Linux：目标正被占用时直接写会 ETXTBSY；先 unlink 再写入新 inode
            if (File.Exists(dest)) File.Delete(dest);
            await File.WriteAllBytesAsync(dest, bytes);
        }
        catch (Exception ex)
        {
            Fail("error", $"写入失败 {f.Name}: {ex.Message}", i + 1, f.Name);
            return Results.BadRequest(new InstallResult(false, logs, $"写入失败 {f.Name}: {ex.Message}"));
        }
        var mode = string.IsNullOrWhiteSpace(f.Mode) ? "0755" : f.Mode;
        Process.Start("/bin/bash", $"-c \"chmod {mode} {dest}\"")?.WaitForExit();
        logs.Add($"已安装 {f.Name} → {dest}");
    }

    if (pending.Count != files.Count)
    {
        Fail("error", $"文件数量不完整（{pending.Count}/{files.Count}）");
        return Results.BadRequest(new InstallResult(false, logs, $"文件数量不完整（{pending.Count}/{files.Count}）"));
    }

    // 更新前在运行的插件，替换成功后拉回原状态
    if (wasRunning && !string.IsNullOrWhiteSpace(startCmd))
    {
        SetInstallProgress(id, new InstallProgress(id, "writing", pending.Count, pending.Count, "", 0, null, "正在恢复运行中的插件…", false, false));
        logs.Add($"恢复启动: {startCmd}");
        try
        {
            using var startP = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{startCmd}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            startP?.WaitForExit(30000);
        }
        catch { }
        logs.Add("已按更新前状态重新启动插件");
    }

    logs.Add($"全部 {pending.Count} 个文件下载、校验并替换成功");
    var doneMsg = wasRunning
        ? $"全部 {pending.Count} 个文件安装成功，已恢复运行"
        : $"全部 {pending.Count} 个文件安装成功";
    SetInstallProgress(id, new InstallProgress(id, "done", pending.Count, pending.Count, "", 0, null, doneMsg, true, true));
    return Results.Ok(new InstallResult(true, logs, null));
});

app.MapGet("/api/apps/{id}/install/progress", (string id) => {
    if (InstallProgressStore.TryGetValue(id, out var p)) return Results.Ok(p);
    return Results.Ok(new InstallProgress(id, "idle", 0, 0, "", 0, null, null, false, false));
});

app.MapPost("/api/apps/{id}/custom_command", async (string id, CustomCommandReq req) => {
    using var p = Process.Start(new ProcessStartInfo {
        FileName = "/bin/bash",
        Arguments = $"-c \"export TERM=xterm; {req.Command}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    });
    if (p is null) return Results.Ok(new LogResponse("命令启动失败"));
    // 必须边等边读：先 WaitForExit 再 ReadToEnd 会在输出写满管道时死锁
    var stdoutTask = p.StandardOutput.ReadToEndAsync();
    var stderrTask = p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    var output = (await stdoutTask) + (await stderrTask);
    return Results.Ok(new LogResponse(output));
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

    return Results.Ok(new SimpleSuccess(true));
});

// Schema 驱动配置：有 ConfigSchema 时读 /data/softcenter/config/{id}.env；否则兼容旧 ConfigKeys 正则解析
app.MapGet("/api/apps/{id}/config", (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath, ConfigKeys, ConfigSchema FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var path = reader.IsDBNull(0) ? "" : reader.GetString(0);
    var keysRaw = reader.IsDBNull(1) ? "" : reader.GetString(1);
    var schemaJson = reader.IsDBNull(2) ? "" : reader.GetString(2);

    if (!string.IsNullOrWhiteSpace(schemaJson))
    {
        var values = BuildSchemaDefaults(schemaJson);
        var envPath = AppEnvPath(id);
        if (File.Exists(envPath))
        {
            foreach (var kv in DecodeEnvDict(ReadEnvFile(envPath))) values[kv.Key] = kv.Value;
        }
        JsonNode? schemaNode = null;
        try { schemaNode = JsonNode.Parse(schemaJson); } catch { }
        return Results.Ok(new ConfigResponse("schema", schemaNode, values, null));
    }

    // legacy: ConfigKeys + 脚本文件正则解析
    var legacy = new Dictionary<string, ConfigItem>();
    if (!string.IsNullOrEmpty(path) && File.Exists(path) && !string.IsNullOrWhiteSpace(keysRaw))
    {
        var keys = keysRaw.Split(',', StringSplitOptions.RemoveEmptyEntries);
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
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (string.IsNullOrWhiteSpace(lines[j])) continue;
                        if (lines[j].TrimStart().StartsWith("#"))
                            comment = lines[j].TrimStart().TrimStart('#').Trim();
                        break;
                    }
                    break;
                }
            }
            legacy[k] = new ConfigItem(val, comment);
        }
    }
    return Results.Ok(new ConfigResponse("legacy", null, new Dictionary<string, string>(), legacy));
});

app.MapPost("/api/apps/{id}/config", async (string id, Dictionary<string, string> payload) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT ConfigPath, ConfigKeys, ConfigSchema FROM apps_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var path = reader.IsDBNull(0) ? "" : reader.GetString(0);
    var keysRaw = reader.IsDBNull(1) ? "" : reader.GetString(1);
    var schemaJson = reader.IsDBNull(2) ? "" : reader.GetString(2);
    reader.Close();

    if (!string.IsNullOrWhiteSpace(schemaJson))
    {
        JsonDocument schemaDoc;
        try { schemaDoc = JsonDocument.Parse(schemaJson); }
        catch { return Results.BadRequest(new { success = false, errors = new Dictionary<string, string> { ["_"] = "ConfigSchema 不是合法 JSON" } }); }
        using (schemaDoc)
        {
            var errors = new Dictionary<string, string>();
            var normalized = new Dictionary<string, string>();
            foreach (var field in EnumerateSchemaFields(schemaJson))
            {
                var key = JsonGetString(field, "Key");
                if (string.IsNullOrEmpty(key)) continue;
                var type = JsonGetString(field, "Type").ToLowerInvariant();
                payload.TryGetValue(key, out var raw);
                var value = raw ?? JsonGetString(field, "Default");
                if (type == "switch") value = NormalizeSwitch(value);
                var err = ValidateConfigValue(field, value);
                if (err != null) errors[key] = err;
                else normalized[key] = value;
            }
            // 保留 schema 未声明但前端传来的键（向后兼容扩展）
            foreach (var kv in payload)
            {
                if (!normalized.ContainsKey(kv.Key)) normalized[kv.Key] = kv.Value;
            }
            if (errors.Count > 0) return Results.BadRequest(new ConfigSaveResult(false, errors, null));

            var envDir = AppEnvDir();
            var envPath = AppEnvPath(id);
            Directory.CreateDirectory(envDir);
            var sb = new StringBuilder();
            sb.AppendLine($"# SoftCenter generated config for {id}");
            sb.AppendLine($"# source this file in shell: source {envPath}");
            sb.AppendLine($"# updated: {DateTime.UtcNow:O}");
            foreach (var field in EnumerateSchemaFields(schemaJson))
            {
                var key = JsonGetString(field, "Key");
                if (string.IsNullOrEmpty(key)) continue;
                normalized.TryGetValue(key, out var val);
                sb.AppendLine($"{key}={EnvQuote(EncodeEnvValue(val ?? ""))}");
            }
            foreach (var kv in normalized)
            {
                var known = false;
                foreach (var field in EnumerateSchemaFields(schemaJson))
                {
                    if (JsonGetString(field, "Key") == kv.Key) { known = true; break; }
                }
                if (!known) sb.AppendLine($"{kv.Key}={EnvQuote(EncodeEnvValue(kv.Value))}");
            }
            await File.WriteAllTextAsync(envPath, sb.ToString());
            return Results.Ok(new ConfigSaveResult(true, null, envPath));
        }
    }

    // legacy：正则改写原脚本
    if (!string.IsNullOrEmpty(path) && File.Exists(path))
    {
        var content = await File.ReadAllTextAsync(path);
        foreach (var kv in payload) content = Regex.Replace(content, $@"{Regex.Escape(kv.Key)}=[^\n\r]*", _ => $"{kv.Key}={kv.Value}");
        await File.WriteAllTextAsync(path, content);
        return Results.Ok(new SimpleSuccess(true));
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
    Process.Start("/bin/bash", $"-c \"(crontab -l 2>/dev/null; echo '{req.Schedule} {req.Command}') | crontab -\""); return Results.Ok(new SimpleSuccess(true));
});

app.MapDelete("/api/cron/{id}", (string id) => {
    using var conn = new SqliteConnection(DbPath); conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM cron_registry WHERE Id=@id";
    cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery();
    var lineToRemove = Encoding.UTF8.GetString(Convert.FromBase64String(id));
    Process.Start("/bin/bash", $"-c \"crontab -l | grep -vF '{lineToRemove}' | crontab -\""); return Results.Ok(new SimpleSuccess(true));
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
            var u = match.Groups[1].Value.Trim();
            int days = 0, hours = 0, minutes = 0;
            var mDays = Regex.Match(u, @"(\d+)\s*day");
            if (mDays.Success) days = int.Parse(mDays.Groups[1].Value);
            var mHm = Regex.Match(u, @"(\d+):(\d+)");
            if (mHm.Success)
            {
                hours = int.Parse(mHm.Groups[1].Value);
                minutes = int.Parse(mHm.Groups[2].Value);
            }
            else
            {
                var mMin = Regex.Match(u, @"(\d+)\s*min");
                if (mMin.Success) minutes = int.Parse(mMin.Groups[1].Value);
            }
            uptime = days > 0 ? $"{days}d{hours}h" : (hours > 0 ? $"{hours}h{minutes}m" : $"{minutes}m");
        }
        else { uptime = uptimeRaw; }
    }

    // 设备型号：mca-cli-op info → Model: UniFi Cloud Gateway Fiber（进程开销大，缓存）
    var model = DeviceModelCache;
    if (string.IsNullOrEmpty(model))
    {
        var infoRaw = GetBashOutput("mca-cli-op info 2>/dev/null");
        if (!string.IsNullOrEmpty(infoRaw))
        {
            var m = Regex.Match(infoRaw, @"^Model:\s*(.+)$", RegexOptions.Multiline);
            if (m.Success) model = m.Groups[1].Value.Trim();
        }
        if (string.IsNullOrEmpty(model)) model = "UCG-Fiber";
        DeviceModelCache = model;
    }

    return Results.Ok(new SystemInfo(rawVersion.Split('+')[0], "NativeAOT-.NET10", model, cpuTemp, sfpTemp, uptime));
});

app.MapGet("/api/system/config", () => Results.Ok(sysConfig));

app.MapPost("/api/system/config", async (AppConfig newConfig) => {
    sysConfig.Port = newConfig.Port; sysConfig.AdminToken = newConfig.AdminToken; sysConfig.LocalProxy = newConfig.LocalProxy;
    await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(sysConfig, AppJsonContext.Default.AppConfig));
    Process.Start("systemd-run", $"--unit=sc_restarter --collect bash -c \"sleep 1 && systemctl restart {ServiceName}\""); return Results.Ok(new SimpleSuccess(true));
});

app.MapPost("/api/system/upgrade", () => {
    var proxy = sysConfig.LocalProxy;
    var scriptUrl = "https://raw.githubusercontent.com/fw867/unifi-softcenterstore/master/install.sh";
    if (File.Exists("/tmp/sc_update.log")) File.Delete("/tmp/sc_update.log");
    string curlCmd = !string.IsNullOrEmpty(proxy) ? $"curl -x {proxy} -sSL {scriptUrl} | bash -s '{proxy}' > /tmp/sc_update.log 2>&1" : $"curl -sSL {scriptUrl} | bash > /tmp/sc_update.log 2>&1";
    Process.Start("systemd-run", $"--unit=sc_updater --collect bash -c \"sleep 1 && {curlCmd}\""); return Results.Ok(new SimpleSuccess(true));
});

app.MapGet("/api/system/upgrade/log", () => {
    if (File.Exists("/tmp/sc_update.log")) return Results.Ok(new LogResponse(File.ReadAllText("/tmp/sc_update.log")));
    return Results.Ok(new LogResponse("正在准备更新环境..."));
});

app.Run($"http://0.0.0.0:{sysConfig.Port}");

public record AppConfig { public int Port { get; set; } = 9958; public string AdminToken { get; set; } = "Your_Secret_Token_Here"; public string LocalProxy { get; set; } = ""; }
public record AppEntity(string Id, string Name, string Type, string Icon, string StartCommand, string StopCommand, string StatusCommand, int IsAutoStart, bool IsRunning, string ConfigPath, string ConfigKeys, string LogPath, int SortOrder, string Version, string Description, string CustomCommands, string ConfigSchema = "", string Files = "[]");
public record AppFileItem(string Name, string Path, string Sha256, string Mode = "0755");
public record InstallResult(bool Success, List<string>? Logs, string? Error);
public record InstallProgress(string AppId, string Phase, int FileIndex, int FileCount, string FileName, long BytesReceived, long? TotalBytes, string? Message, bool Done, bool Success);
public record SimpleSuccess(bool Success);
public record AppOrderReq(string Id, int SortOrder);
public record CronEntity(string Id, string Name, string Schedule, string Command);
public record CronRequest(string Schedule, string Command);
public record CustomCommandReq(string Command);
public record LogResponse(string Content);
public record SystemInfo(string Version, string Runtime, string Device, string CpuTemp, string SfpTemp, string Uptime);

public record ConfigItem(string Value, string Comment);
public record ConfigResponse(string Mode, JsonNode? Schema, Dictionary<string, string> Values, Dictionary<string, ConfigItem>? Legacy);
public record ConfigSaveResult(bool Success, Dictionary<string, string>? Errors, string? EnvPath);

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(AppEntity))]
[JsonSerializable(typeof(AppFileItem))]
[JsonSerializable(typeof(InstallResult))]
[JsonSerializable(typeof(InstallProgress))]
[JsonSerializable(typeof(SimpleSuccess))]
[JsonSerializable(typeof(AppOrderReq))]
[JsonSerializable(typeof(CronEntity))]
[JsonSerializable(typeof(CronRequest))]
[JsonSerializable(typeof(CustomCommandReq))]
[JsonSerializable(typeof(LogResponse))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(ConfigItem))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(ConfigSaveResult))]
[JsonSerializable(typeof(Dictionary<string, ConfigItem>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<AppEntity>))]
[JsonSerializable(typeof(List<AppFileItem>))]
[JsonSerializable(typeof(List<AppOrderReq>))]
[JsonSerializable(typeof(List<CronEntity>))]
[JsonSerializable(typeof(IEnumerable<CronEntity>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppJsonContext : JsonSerializerContext { }