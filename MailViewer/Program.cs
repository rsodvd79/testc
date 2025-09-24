using Microsoft.Extensions.FileProviders;
using MimeKit;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables(prefix: "MAILVIEWER_")
    .Build();

var fetcherDir = PathHelpers.GetFetcherConfigDirectory();
var dataRootSetting = configuration["DataRoot"];
if (string.IsNullOrWhiteSpace(dataRootSetting))
{
    dataRootSetting = Path.Combine(fetcherDir, "Data");
}
string dataRoot;
if (Path.IsPathRooted(dataRootSetting))
{
    dataRoot = Path.GetFullPath(dataRootSetting);
}
else
{
    dataRoot = Path.GetFullPath(Path.Combine(fetcherDir, dataRootSetting));
}
Directory.CreateDirectory(dataRoot);

var app = builder.Build();
app.Logger.LogInformation("Using data root {DataRoot}", dataRoot);


app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/accounts", () =>
{
    var dirs = Directory.EnumerateDirectories(dataRoot)
        .Select(Path.GetFileName)
        .Where(n => !string.IsNullOrEmpty(n))
        .ToArray();
    return Results.Ok(dirs);
});

app.MapGet("/api/folders/{account}", (string account) =>
{
    var accountPath = Path.Combine(dataRoot, account);
    if (!Directory.Exists(accountPath)) return Results.NotFound();

    var folders = Directory.EnumerateDirectories(accountPath, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(accountPath, p))
        .ToArray();
    return Results.Ok(folders);
});

app.MapGet("/api/messages/{account}/{*folder}", (string account, string folder) =>
{
    var folderPath = Path.Combine(dataRoot, account, folder);
    if (!Directory.Exists(folderPath)) return Results.NotFound();
    var emls = Directory.EnumerateFiles(folderPath, "*.eml")
        .Select(f => new { id = Path.GetFileNameWithoutExtension(f), file = Path.GetFileName(f), size = new FileInfo(f).Length })
        .OrderByDescending(m => m.id)
        .ToArray();
    return Results.Ok(emls);
});

app.MapGet("/api/message/{account}/{*path}", (string account, string path) =>
{
    var emlPath = Path.Combine(dataRoot, account, path);
    if (!System.IO.File.Exists(emlPath)) return Results.NotFound();
    var message = MimeMessage.Load(emlPath);
    var textBody = message.HtmlBody ?? message.TextBody ?? "";
    var attachments = message.Attachments.Select((a, i) => new
    {
        name = a.ContentDisposition?.FileName ?? a.ContentType.Name ?? $"attachment-{i}",
        index = i
    }).ToArray();
    return Results.Ok(new
    {
        headers = new { subject = message.Subject, from = message.From.ToString(), to = message.To.ToString(), date = message.Date.ToString() },
        body = textBody,
        attachments
    });
});

app.MapGet("/api/attachment/{account}/{*path}", (HttpContext ctx, string account, string path) =>
{
    var emlPath = Path.Combine(dataRoot, account, path);
    if (!System.IO.File.Exists(emlPath)) return Results.NotFound();
    var message = MimeMessage.Load(emlPath);

    var indexStr = ctx.Request.Query["index"].FirstOrDefault();
    if (indexStr == null || !int.TryParse(indexStr, out var index)) return Results.BadRequest("index required");

    var attachment = message.Attachments.ElementAtOrDefault(index);
    if (attachment == null) return Results.NotFound();

    var fileName = attachment.ContentDisposition?.FileName ?? attachment.ContentType.Name ?? "attachment";
    var mem = new MemoryStream();
    if (attachment is MessagePart rfc822)
    {
        rfc822.Message.WriteTo(mem);
    }
    else if (attachment is MimePart part)
    {
        part.Content.DecodeTo(mem);
    }
    mem.Position = 0;
    var contentType = attachment.ContentType.MimeType;
    return Results.File(mem, contentType, fileName);
});

app.MapGet("/", () => Results.Redirect("/index.html"));

// --- Config API for MailFetcher ---
var adminUsername = configuration["AdminAuth:Username"] ?? "admin";
var adminPassword = configuration["AdminAuth:Password"] ?? "changeme";

bool IsAuthorized(HttpContext ctx)
{
    var header = ctx.Request.Headers["Authorization"].FirstOrDefault();
    if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ")) return false;
    var token = header.Substring("Basic ".Length).Trim();
    string decoded;
    try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token)); }
    catch { return false; }
    var parts = decoded.Split(':', 2);
    if (parts.Length != 2) return false;
    return parts[0] == adminUsername && parts[1] == adminPassword;
}

var adminEnabled = !builder.Environment.IsProduction() || (configuration["AdminEnabled"]?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false);

app.MapGet("/api/config", (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    var configDir = PathHelpers.GetFetcherConfigDirectory();
    var localConfig = Path.Combine(configDir, "appsettings.Local.json");
    var defaultConfig = Path.Combine(configDir, "appsettings.json");
    var pathToRead = System.IO.File.Exists(localConfig) ? localConfig : defaultConfig;
    if (!System.IO.File.Exists(pathToRead)) return Results.NotFound();
    var json = System.IO.File.ReadAllText(pathToRead);
    return Results.Json(JsonDocument.Parse(json));
});

app.MapPost("/api/config", async (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    var configDir = PathHelpers.GetFetcherConfigDirectory();
    Directory.CreateDirectory(configDir);
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    try
    {
        using var parsed = JsonDocument.Parse(body);
    }
    catch
    {
        return Results.BadRequest("Invalid JSON");
    }
    var targetPath = Path.Combine(configDir, "appsettings.Local.json");
    await System.IO.File.WriteAllTextAsync(targetPath, body);
    return Results.Ok();
});

app.MapGet("/admin.html", (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx))
    {
        ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"MailViewer Admin\"";
        return Results.Unauthorized();
    }
    var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot", "admin.html");
    return Results.File(path, "text/html; charset=utf-8");
});

// --- AdminAuth management ---
app.MapGet("/api/adminauth", (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    return Results.Ok(new { Username = adminUsername, Password = "" });
});

app.MapPost("/api/adminauth", async (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    try
    {
        var dto = JsonSerializer.Deserialize<Dictionary<string, string>>(body) ?? new();
        var newUser = dto.ContainsKey("Username") ? dto["Username"] : adminUsername;
        var passProvided = dto.ContainsKey("Password") && !string.IsNullOrEmpty(dto["Password"]);
        if (passProvided)
        {
            var confirm = dto.ContainsKey("PasswordConfirm") ? dto["PasswordConfirm"] : null;
            if (confirm != dto["Password"]) return Results.BadRequest();
        }
        var newPass = passProvided ? dto["Password"] : adminPassword;
        adminUsername = string.IsNullOrWhiteSpace(newUser) ? adminUsername : newUser;
        adminPassword = string.IsNullOrWhiteSpace(newPass) ? adminPassword : newPass;

        // persist to appsettings.Local.json (merge only AdminAuth)
        var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        Dictionary<string, object> root;
        if (System.IO.File.Exists(localPath))
        {
            try { root = JsonSerializer.Deserialize<Dictionary<string, object>>(await System.IO.File.ReadAllTextAsync(localPath)) ?? new(); }
            catch { root = new(); }
        }
        else root = new();

        root["AdminAuth"] = new Dictionary<string, object> { ["Username"] = adminUsername, ["Password"] = adminPassword };
        var jsonOut = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(localPath, jsonOut);

        return Results.Ok(new { Username = adminUsername });
    }
    catch
    {
        return Results.BadRequest();
    }
});

// --- Admin: Run MailFetcher now ---
var logBuffer = new ConcurrentQueue<string>();
var maxLogLines = 500;
Process? currentProcess = null;
DateTime? startedAt = null;

void EnqueueLog(string line)
{
    var redacted = Redact(line);
    logBuffer.Enqueue(redacted);
    while (logBuffer.Count > maxLogLines && logBuffer.TryDequeue(out _)) { }
}

string Redact(string input)
{
    if (string.IsNullOrEmpty(input)) return input;
    var s = input;
    // Redact common password patterns: password=..., pass: ..., pwd=...
    s = Regex.Replace(s, "(?i)(password|pass|pwd)\\s*[:=]\\s*[^\\s]+", m => m.Groups[1].Value + "=[REDACTED]");
    // Redact Basic Authorization tokens
    s = Regex.Replace(s, "(?i)(Authorization)\\s*:\\s*Basic\\s+[^\\s]+", "$1: Basic [REDACTED]");
    return s;
}

app.MapPost("/api/run-fetch/start", (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    if (currentProcess != null && !currentProcess.HasExited)
        return Results.BadRequest("Fetcher già in esecuzione");

    logBuffer = new ConcurrentQueue<string>();
    var slnRoot = PathHelpers.GetSolutionRoot();
    var csproj = Path.Combine(slnRoot, "MailFetcher", "MailFetcher.csproj");
    if (!System.IO.File.Exists(csproj)) return Results.NotFound("MailFetcher.csproj non trovato");

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"run --project \"{csproj}\" --no-build",
        WorkingDirectory = slnRoot,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    psi.Environment["MAILFETCHER_CONFIG_DIR"] = PathHelpers.GetFetcherConfigDirectory();
    currentProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
    currentProcess.OutputDataReceived += (_, e) => { if (e.Data != null) EnqueueLog(e.Data); };
    currentProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) EnqueueLog("ERR: " + e.Data); };
    currentProcess.Exited += (_, __) => { EnqueueLog($"Processo terminato con codice {currentProcess!.ExitCode}"); };
    try
    {
        startedAt = DateTime.UtcNow;
        currentProcess.Start();
        currentProcess.BeginOutputReadLine();
        currentProcess.BeginErrorReadLine();
    }
    catch (Exception ex)
    {
        EnqueueLog("Avvio fallito: " + ex.Message);
        currentProcess = null;
        startedAt = null;
        return Results.StatusCode(500);
    }
    EnqueueLog("MailFetcher avviato");
    return Results.Ok(new { started = true });
});

app.MapPost("/api/run-fetch/stop", () =>
{
    if (!adminEnabled) return Results.NotFound();
    if (currentProcess == null || currentProcess.HasExited) return Results.Ok(new { stopped = false });
    try
    {
        currentProcess.Kill(true);
        EnqueueLog("Processo terminato dall'utente");
        return Results.Ok(new { stopped = true });
    }
    catch (Exception ex)
    {
        EnqueueLog("Stop fallito: " + ex.Message);
        return Results.StatusCode(500);
    }
});

app.MapGet("/api/run-fetch/status", () =>
{
    if (!adminEnabled) return Results.NotFound();
    var running = currentProcess != null && !currentProcess.HasExited;
    var logs = logBuffer.ToArray();
    return Results.Ok(new { running, startedAt, logs });
});

// --- Scheduler ---
var schedulerState = new SchedulerState
{
    Enabled = configuration["Scheduler:Enabled"]?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
    IntervalMinutes = int.TryParse(configuration["Scheduler:IntervalMinutes"], out var m) ? Math.Max(1, m) : 30
};

var scheduler = new Timer(async _ =>
{
    if (!adminEnabled || !schedulerState.Enabled) return;
    if (currentProcess != null && !currentProcess.HasExited) return; // skip if running
    EnqueueLog($"Scheduler: avvio fetch alle {DateTime.Now:HH:mm:ss}");
    // Start process directly (duplicate minimal logic)
    var slnRoot = PathHelpers.GetSolutionRoot();
    var csproj = Path.Combine(slnRoot, "MailFetcher", "MailFetcher.csproj");
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"run --project \"{csproj}\" --no-build",
        WorkingDirectory = slnRoot,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    psi.Environment["MAILFETCHER_CONFIG_DIR"] = PathHelpers.GetFetcherConfigDirectory();
    currentProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
    currentProcess.OutputDataReceived += (_, e) => { if (e.Data != null) EnqueueLog(e.Data); };
    currentProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) EnqueueLog("ERR: " + e.Data); };
    currentProcess.Exited += (_, __) => { EnqueueLog($"Processo terminato con codice {currentProcess!.ExitCode}"); };
    try
    {
        startedAt = DateTime.UtcNow;
        currentProcess.Start();
        currentProcess.BeginOutputReadLine();
        currentProcess.BeginErrorReadLine();
    }
    catch (Exception ex)
    {
        EnqueueLog("Scheduler: avvio fallito: " + ex.Message);
        currentProcess = null;
        startedAt = null;
    }
}, null, TimeSpan.FromMinutes(schedulerState.IntervalMinutes), TimeSpan.FromMinutes(schedulerState.IntervalMinutes));

app.MapGet("/api/scheduler", () =>
{
    if (!adminEnabled) return Results.NotFound();
    return Results.Ok(schedulerState);
});

app.MapPost("/api/scheduler", async (HttpContext ctx) =>
{
    if (!adminEnabled) return Results.NotFound();
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    try
    {
        var dto = JsonSerializer.Deserialize<SchedulerState>(body) ?? new SchedulerState();
        schedulerState.Enabled = dto.Enabled;
        schedulerState.IntervalMinutes = Math.Max(1, dto.IntervalMinutes);
        // reset timer period
        scheduler.Change(TimeSpan.FromMinutes(schedulerState.IntervalMinutes), TimeSpan.FromMinutes(schedulerState.IntervalMinutes));

        // persist to appsettings.Local.json (merge only Scheduler section)
        var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        Dictionary<string, object> root;
        if (System.IO.File.Exists(localPath))
        {
            try
            {
                root = JsonSerializer.Deserialize<Dictionary<string, object>>(await System.IO.File.ReadAllTextAsync(localPath)) ?? new();
            }
            catch { root = new(); }
        }
        else root = new();

        var schedulerObj = new Dictionary<string, object>
        {
            ["Enabled"] = schedulerState.Enabled,
            ["IntervalMinutes"] = schedulerState.IntervalMinutes
        };
        root["Scheduler"] = schedulerObj;
        var jsonOut = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        await System.IO.File.WriteAllTextAsync(localPath, jsonOut);
        return Results.Ok(schedulerState);
    }
    catch
    {
        return Results.BadRequest();
    }
});

app.Run();

static class PathHelpers
{
    public static string GetSolutionRoot()
    {
        var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
        return Path.GetFullPath(Path.Combine(projectDir, ".."));
    }

    public static string GetFetcherConfigDirectory()
    {
        return Path.Combine(GetSolutionRoot(), "MailFetcher");
    }
}

public class SchedulerState
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 30;
}


