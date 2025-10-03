using Microsoft.Extensions.FileProviders;
using MimeKit;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Text.RegularExpressions;

namespace MailViewer;

public static class MailViewerApp
{
    public static WebApplication Build(string[]? args = null, MailViewerAppOptions? options = null)
    {
        args ??= Array.Empty<string>();
        options ??= new MailViewerAppOptions();

        var contentRoot = options.ContentRootPath ?? GetDefaultContentRoot();
        var builderOptions = new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = string.IsNullOrWhiteSpace(contentRoot) ? null : contentRoot,
            WebRootPath = options.WebRootPath ?? Path.Combine(contentRoot, "wwwroot")
        };

        var builder = WebApplication.CreateBuilder(builderOptions);

        var urls = options.Urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToArray();
        if (urls.Length > 0)
        {
            builder.WebHost.UseUrls(urls);
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(builder.Environment.ContentRootPath)
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

        var dataRootFull = Path.GetFullPath(dataRoot);
        var dataRootFullWithSep = EnsureTrailingSeparator(dataRootFull);
        var invalidNameChars = Path.GetInvalidFileNameChars();

        string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        }

        bool TryGetAccountPath(string? accountSegment, out string accountPath)
        {
            accountPath = string.Empty;
            if (string.IsNullOrWhiteSpace(accountSegment))
            {
                return false;
            }

            var trimmed = accountSegment.Trim();
            if (trimmed.Contains("..", StringComparison.Ordinal) ||
                trimmed.IndexOfAny(invalidNameChars) >= 0 ||
                trimmed.Contains(Path.DirectorySeparatorChar) ||
                trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                return false;
            }

            var candidate = Path.GetFullPath(Path.Combine(dataRootFull, trimmed));
            if (!candidate.StartsWith(dataRootFullWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Directory.Exists(candidate))
            {
                return false;
            }

            accountPath = candidate;
            return true;
        }

        bool TryResolveAccountRelativePath(string accountPath, string? relativePath, out string resolvedPath, bool allowBaseOnEmpty = false)
        {
            resolvedPath = accountPath;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return allowBaseOnEmpty;
            }

            var trimmed = relativePath.Trim();
            if (ContainsInvalidSegments(trimmed))
            {
                return false;
            }

            var normalized = trimmed.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            normalized = normalized.TrimStart(Path.DirectorySeparatorChar);
            if (normalized.Length == 0)
            {
                return allowBaseOnEmpty;
            }

            var candidate = Path.GetFullPath(Path.Combine(accountPath, normalized));
            var accountWithSep = EnsureTrailingSeparator(accountPath);
            if (!candidate.StartsWith(accountWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolvedPath = candidate;
            return true;
        }

        bool ContainsInvalidSegments(string value)
        {
            var segments = value.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (segment == "." || segment == "..")
                {
                    return true;
                }

                if (segment.IndexOfAny(invalidNameChars) >= 0)
                {
                    return true;
                }
            }

            return false;
        }


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
            if (!TryGetAccountPath(account, out var accountPath))
            {
                return Results.NotFound();
            }

            var folders = Directory.EnumerateDirectories(accountPath, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(accountPath, p))
                .ToArray();
            return Results.Ok(folders);
        });

        app.MapGet("/api/messages/{account}/{*folder}", (string account, string? folder) =>
        {
            if (!TryGetAccountPath(account, out var accountPath))
            {
                return Results.NotFound();
            }

            if (!TryResolveAccountRelativePath(accountPath, folder, out var folderPath, allowBaseOnEmpty: true) || !Directory.Exists(folderPath))
            {
                return Results.NotFound();
            }

            var emls = Directory.EnumerateFiles(folderPath, "*.eml")
                .Select(f => {
                    var info = new FileInfo(f);
                    var (subject, sent) = ReadMessagePreview(f);
                    return new
                    {
                        id = Path.GetFileNameWithoutExtension(f),
                        file = info.Name,
                        size = info.Length,
                        subject,
                        sent
                    };
                })
                .OrderByDescending(m => m.id)
                .ToArray();
            return Results.Ok(emls);
        });

        app.MapGet("/api/message/{account}/{*path}", (string account, string path) =>
        {
            if (!TryGetAccountPath(account, out var accountPath))
            {
                return Results.NotFound();
            }

            if (!TryResolveAccountRelativePath(accountPath, path, out var emlPath) ||
                !System.IO.File.Exists(emlPath) ||
                !string.Equals(Path.GetExtension(emlPath), ".eml", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

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
            if (!TryGetAccountPath(account, out var accountPath))
            {
                return Results.NotFound();
            }

            if (!TryResolveAccountRelativePath(accountPath, path, out var emlPath) ||
                !System.IO.File.Exists(emlPath) ||
                !string.Equals(Path.GetExtension(emlPath), ".eml", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

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

        app.MapGet("/api/search", (string q, string? account, string? folder, int? limit, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Results.BadRequest("Query required");
            }

            var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToArray();
            if (tokens.Length == 0)
            {
                return Results.BadRequest("Query required");
            }

            var maxResults = Math.Clamp(limit ?? 100, 1, 500);
            var matches = new List<SearchResultItem>();
            var dataRootFull = Path.GetFullPath(dataRoot);

            IEnumerable<string?> accountsToSearch;
            if (!string.IsNullOrWhiteSpace(account))
            {
                accountsToSearch = new[] { account.Trim() };
            }
            else
            {
                accountsToSearch = Directory.EnumerateDirectories(dataRoot)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n));
            }

            var limitReached = false;
            foreach (var accountName in accountsToSearch)
            {
                if (limitReached) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(accountName)) continue;

                var accountKey = accountName.Trim();
                if (accountKey.Contains("..", StringComparison.Ordinal)) continue;

                var accountPath = Path.Combine(dataRoot, accountKey);
                if (!Directory.Exists(accountPath)) continue;

                var accountFull = Path.GetFullPath(accountPath);
                if (!accountFull.StartsWith(dataRootFull, StringComparison.OrdinalIgnoreCase)) continue;

                IEnumerable<string> rootsToSearch;
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    var normalizedFolder = folder!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).Trim();
                    if (string.IsNullOrEmpty(normalizedFolder))
                    {
                        rootsToSearch = new[] { accountPath };
                    }
                    else
                    {
                        var folderPath = Path.Combine(accountPath, normalizedFolder);
                        if (!Directory.Exists(folderPath)) continue;

                        var folderFull = Path.GetFullPath(folderPath);
                        if (!folderFull.StartsWith(accountFull, StringComparison.OrdinalIgnoreCase)) continue;

                        rootsToSearch = new[] { folderPath };
                    }
                }
                else
                {
                    rootsToSearch = new[] { accountPath };
                }

                foreach (var root in rootsToSearch)
                {
                    if (limitReached) break;

                    foreach (var file in Directory.EnumerateFiles(root, "*.eml", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            using var stream = File.OpenRead(file);
                            var message = MimeMessage.Load(stream);

                            var bodyText = ExtractPlainText(message);
                            var haystack = BuildSearchHaystack(message, bodyText);
                            if (!ContainsAllTokens(haystack, tokens)) continue;

                            var snippet = BuildSnippet(bodyText, haystack, tokens);
                            var relativePath = Path.GetRelativePath(accountPath, file).Replace('\\', '/');
                            var folderPart = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
                            if (folderPart == ".") folderPart = string.Empty;

                            var info = new FileInfo(file);
                            var sent = message.Date != DateTimeOffset.MinValue ? message.Date.ToLocalTime() : (DateTimeOffset?)null;

                            matches.Add(new SearchResultItem
                            {
                                Account = accountKey,
                                Path = relativePath,
                                Folder = folderPart ?? string.Empty,
                                File = Path.GetFileName(file),
                                Id = Path.GetFileNameWithoutExtension(file),
                                Subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject.Trim(),
                                From = message.From?.ToString(),
                                To = message.To?.ToString(),
                                Sent = sent?.ToString("yyyy-MM-dd HH:mm"),
                                Size = info.Length,
                                Snippet = snippet,
                                SortDate = sent
                            });

                            if (matches.Count >= maxResults)
                            {
                                limitReached = true;
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            app.Logger.LogWarning(ex, "Failed to load message {File} during search", file);
                        }
                    }
                }
            }

            var ordered = matches
                .OrderByDescending(m => m.SortDate ?? DateTimeOffset.MinValue)
                .ThenByDescending(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(m => new
                {
                    account = m.Account,
                    path = m.Path,
                    folder = m.Folder,
                    file = m.File,
                    id = m.Id,
                    subject = m.Subject,
                    from = m.From,
                    to = m.To,
                    sent = m.Sent,
                    size = m.Size,
                    snippet = m.Snippet
                })
                .ToArray();

            return Results.Ok(ordered);
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
 
            var webRoot = app.Environment.WebRootPath;
            if (string.IsNullOrWhiteSpace(webRoot))
            {
                app.Logger.LogWarning("WebRootPath not configured; unable to serve admin.html");
                return Results.NotFound();
            }
 
            var adminPagePath = Path.Combine(webRoot, "admin.html");
            if (!File.Exists(adminPagePath))
            {
                app.Logger.LogWarning("admin.html not found under WebRootPath '{WebRootPath}'", webRoot);
                return Results.NotFound();
            }
 
            return Results.File(adminPagePath, "text/html; charset=utf-8");
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
                return Results.BadRequest("Fetcher gia in esecuzione");

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

        return app;
    }

    static string ExtractPlainText(MimeMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            return NormalizeWhitespace(message.TextBody);
        }
        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            return StripHtml(message.HtmlBody);
        }
        return string.Empty;
    }

    static string BuildSearchHaystack(MimeMessage message, string bodyText)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message.Subject)) builder.Append(message.Subject).Append(' ');
        var from = message.From?.ToString();
        if (!string.IsNullOrWhiteSpace(from)) builder.Append(from).Append(' ');
        var to = message.To?.ToString();
        if (!string.IsNullOrWhiteSpace(to)) builder.Append(to).Append(' ');
        var cc = message.Cc?.ToString();
        if (!string.IsNullOrWhiteSpace(cc)) builder.Append(cc).Append(' ');
        var bcc = message.Bcc?.ToString();
        if (!string.IsNullOrWhiteSpace(bcc)) builder.Append(bcc).Append(' ');
        builder.Append(bodyText);
        return NormalizeWhitespace(builder.ToString());
    }

    static bool ContainsAllTokens(string text, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (!text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    static string BuildSnippet(string primary, string secondary, string[] tokens)
    {
        var snippet = BuildSnippetInternal(primary, tokens);
        if (!string.IsNullOrEmpty(snippet))
        {
            return snippet;
        }
        return BuildSnippetInternal(secondary, tokens);
    }

    static string BuildSnippetInternal(string? text, string[] tokens)
    {
        var normalized = NormalizeWhitespace(text);
        if (string.IsNullOrEmpty(normalized))
        {
            return string.Empty;
        }
        foreach (var token in tokens)
        {
            var idx = normalized.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = Math.Max(0, idx - 60);
                var end = Math.Min(normalized.Length, idx + token.Length + 80);
                var snippet = normalized.Substring(start, end - start);
                if (start > 0) snippet = "..." + snippet;
                if (end < normalized.Length) snippet += "...";
                return snippet;
            }
        }
        if (normalized.Length <= 160)
        {
            return normalized;
        }
        return normalized.Substring(0, 160) + "...";
    }

    static string NormalizeWhitespace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    static string StripHtml(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }
        var withoutTags = Regex.Replace(input, @"<.*?>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return NormalizeWhitespace(decoded);
    }

    static (string? subject, string? sent) ReadMessagePreview(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var message = MimeMessage.Load(stream);
            var subject = string.IsNullOrWhiteSpace(message.Subject) ? null : message.Subject.Trim();
            string? sent = null;
            if (message.Date != DateTimeOffset.MinValue)
            {
                sent = message.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
            return (subject, sent);
        }
        catch
        {
            return (null, null);
        }
    }

    public static string GetDefaultContentRoot()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDir != null && !currentDir.GetFiles("MailSuite.sln").Any())
        {
            currentDir = currentDir.Parent;
        }

        if (currentDir != null)
        {
            var projectRoot = Path.Combine(currentDir.FullName, "MailViewer");
            if (Directory.Exists(projectRoot))
            {
                return projectRoot;
            }
        }

        // Fallback per ambienti in cui la sln non è presente
        var fallbackDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "MailViewer"));
        if (Directory.Exists(fallbackDir))
        {
            return fallbackDir;
        }

        return AppContext.BaseDirectory;
    }

    public sealed class MailViewerAppOptions
    {
        public string? ContentRootPath { get; init; }
        public string[] Urls { get; init; } = Array.Empty<string>();
        public string? WebRootPath { get; init; }
    }

    static class PathHelpers
    {
        public static string GetSolutionRoot()
        {
            var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
            while (currentDir != null && !currentDir.GetFiles("MailSuite.sln").Any())
            {
                currentDir = currentDir.Parent;
            }

            if (currentDir != null)
            {
                return currentDir.FullName;
            }

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
        }

        public static string GetFetcherConfigDirectory()
        {
            return Path.Combine(GetSolutionRoot(), "MailFetcher");
        }
    }

    class SearchResultItem
    {
        public string Account { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Sent { get; set; }
        public long Size { get; set; }
        public string? Snippet { get; set; }
        public DateTimeOffset? SortDate { get; set; }
    }

    public class SchedulerState
    {
        public bool Enabled { get; set; }
        public int IntervalMinutes { get; set; } = 30;
    }
}
