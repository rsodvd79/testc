using MailKit.Net.Imap;
using MailKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

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
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Local.json", optional: true)
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

        var outputRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, fetcherConfig.OutputRoot));
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
        using var client = new ImapClient();
        await client.ConnectAsync(account.Host, account.Port, account.UseSsl);
        await client.AuthenticateAsync(account.Username, account.Password);

        await client.Inbox.OpenAsync(FolderAccess.ReadOnly);

        var personal = client.GetFolder(client.PersonalNamespaces[0]);
        await personal.OpenAsync(FolderAccess.ReadOnly);

        foreach (var folder in await personal.GetSubfoldersAsync(true))
        {
            await MirrorFolderAsync(client, folder, accountRoot, logger);
        }

        await MirrorFolderAsync(client, client.Inbox, accountRoot, logger);

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

            await File.WriteAllTextAsync(Path.Combine(folderPath, ".folder"), $"UIDVALIDITY={folder.UidValidity}\nTotal={folder.Count}\n");

            if (folder.Count > 0)
            {
                var uids = await folder.SearchAsync(MailKit.Search.SearchQuery.All);
                foreach (var uid in uids)
                {
                    var emlPath = Path.Combine(folderPath, $"{uid}.eml");
                    if (File.Exists(emlPath))
                        continue;

                    var message = await folder.GetMessageAsync(uid);
                    await using var stream = File.Open(emlPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await message.WriteToAsync(stream, CancellationToken.None);
                    logger.LogInformation("Saved {Path}", emlPath);
                }
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
