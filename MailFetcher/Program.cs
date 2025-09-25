using MailKit.Net.Imap;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Collections.Generic;

namespace MailFetcher;

public class ImapAccountConfig
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 993;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class FetcherConfig
{
    public string OutputRoot { get; set; } = "Data";
    public List<ImapAccountConfig> Accounts { get; set; } = new();
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true);

        string? configBase = null;
        var configDir = Environment.GetEnvironmentVariable("MAILFETCHER_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            configDir = Path.GetFullPath(configDir);
            if (Directory.Exists(configDir))
            {
                configBase = configDir;
                var provider = new PhysicalFileProvider(configDir);
                configBuilder.AddJsonFile(provider, "appsettings.json", optional: true, reloadOnChange: false);
                configBuilder.AddJsonFile(provider, "appsettings.Local.json", optional: true, reloadOnChange: false);
            }
        }

        if (configBase is null)
        {
            var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
            if (Directory.Exists(projectDir))
            {
                configBase = projectDir;
                var provider = new PhysicalFileProvider(projectDir);
                configBuilder.AddJsonFile(provider, "appsettings.json", optional: true, reloadOnChange: false);
                configBuilder.AddJsonFile(provider, "appsettings.Local.json", optional: true, reloadOnChange: false);
            }
        }

        var config = configBuilder
            .AddEnvironmentVariables(prefix: "MAILFETCHER_")
            .Build();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });
        var logger = loggerFactory.CreateLogger("MailFetcher");

        var fetcherConfig = new FetcherConfig();
        config.Bind(fetcherConfig);

        if (fetcherConfig.Accounts.Count == 0)
        {
            logger.LogError("No accounts configured. Add Accounts in appsettings.json.");
            return 1;
        }

        var outputRootSetting = string.IsNullOrWhiteSpace(fetcherConfig.OutputRoot) ? "Data" : fetcherConfig.OutputRoot;
        var outputRoot = Path.IsPathRooted(outputRootSetting)
            ? Path.GetFullPath(outputRootSetting)
            : Path.GetFullPath(Path.Combine(configBase ?? AppContext.BaseDirectory, outputRootSetting));
        Directory.CreateDirectory(outputRoot);

        foreach (var account in fetcherConfig.Accounts)
        {
            var accountRoot = Path.Combine(outputRoot, SanitizePath(account.Name.Length > 0 ? account.Name : account.Username));
            Directory.CreateDirectory(accountRoot);
            try
            {
                await FetchAccountAsync(account, accountRoot, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error fetching account {Account}", account.Name);
            }
        }

        logger.LogInformation("Done.");
        return 0;
    }

    private static async Task FetchAccountAsync(ImapAccountConfig account, string accountRoot, ILogger logger)
    {
        var accountLabel = string.IsNullOrWhiteSpace(account.Name) ? account.Username : account.Name;
        using var client = new ImapClient();
        
        // Aggiungo timeout e logging per diagnostica
        logger.LogInformation("Connecting to IMAP server {Host}:{Port} (SSL: {UseSsl}) for account {Account}", 
            account.Host, account.Port, account.UseSsl, accountLabel);
            
        client.Timeout = 30000; // 30 secondi di timeout
        
        try
        {
            await client.ConnectAsync(account.Host, account.Port, account.UseSsl);
            logger.LogInformation("Successfully connected to {Host}:{Port} for account {Account}", 
                account.Host, account.Port, accountLabel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to {Host}:{Port} for account {Account}", 
                account.Host, account.Port, accountLabel);
            throw;
        }
        
        try
        {
            await client.AuthenticateAsync(account.Username, account.Password);
            logger.LogInformation("Successfully authenticated for account {Account}", accountLabel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to authenticate for account {Account}", accountLabel);
            throw;
        }

        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);
        logger.LogInformation("Opened INBOX for account {Account} - {Count} messages", accountLabel, client.Inbox.Count);

        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task MirrorTreeAsync(IMailFolder folder)
        {
            if (!processed.Add(folder.FullName))
                return;

            await MirrorFolderAsync(client, folder, accountRoot, logger);

            try
            {
                foreach (var sub in await folder.GetSubfoldersAsync(false))
                {
                    await MirrorTreeAsync(sub);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to enumerate subfolders for account {Account}", accountLabel);
            }
        }

        if (client.PersonalNamespaces.Count > 0)
        {
            try
            {
                var personalRoot = client.GetFolder(client.PersonalNamespaces[0]);
                foreach (var folder in await personalRoot.GetSubfoldersAsync(false))
                {
                    await MirrorTreeAsync(folder);
                }
            }
            catch (ImapCommandException ex)
            {
                logger.LogWarning(ex, "Unable to enumerate personal folders for account {Account}", accountLabel);
            }
            catch (FolderNotFoundException ex)
            {
                logger.LogWarning(ex, "Unable to enumerate personal folders for account {Account}", accountLabel);
            }
        }
        else
        {
            logger.LogWarning("No personal namespaces returned for account {Account}", accountLabel);
        }

        await MirrorTreeAsync(client.Inbox);

        await client.DisconnectAsync(true);
    }

    private static async Task MirrorFolderAsync(ImapClient client, IMailFolder folder, string accountRoot, ILogger logger)
    {
        try
        {
            var pathParts = folder.FullName.Split(folder.DirectorySeparator);
            var folderPath = Path.Combine(new[] { accountRoot }.Concat(pathParts.Select(SanitizePath)).ToArray());
            Directory.CreateDirectory(folderPath);

            await folder.OpenAsync(FolderAccess.ReadOnly);
            logger.LogInformation("Processing folder {FolderName} with {Count} messages", folder.FullName, folder.Count);

            await File.WriteAllTextAsync(Path.Combine(folderPath, ".folder"), $"UIDVALIDITY={folder.UidValidity}\nTotal={folder.Count}\n");

            if (folder.Count > 0)
            {
                var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All);
                logger.LogInformation("Found {UidCount} UIDs to process in folder {FolderName}", uids.Count, folder.FullName);
                
                int processed = 0;
                int skipped = 0;
                
                foreach (var uid in uids)
                {
                    var emlPath = Path.Combine(folderPath, $"{uid}.eml");
                    if (File.Exists(emlPath))
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        var message = await folder.GetMessageAsync(uid);
                        await using var stream = File.Open(emlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        await message.WriteToAsync(stream, CancellationToken.None);
                        processed++;
                        
                        if (processed % 10 == 0)
                        {
                            logger.LogInformation("Processed {Processed} messages in folder {FolderName}", processed, folder.FullName);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error processing message UID {Uid} in folder {FolderName}", uid, folder.FullName);
                    }
                }
                
                logger.LogInformation("Completed folder {FolderName}: processed {Processed}, skipped {Skipped}", 
                    folder.FullName, processed, skipped);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error mirroring folder {Folder}", folder.FullName);
        }
    }

    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }
        return sb.ToString();
    }
}


