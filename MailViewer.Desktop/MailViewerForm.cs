using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MailViewer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MailViewer.Desktop;

public partial class MailViewerForm : Form
{
    private WebApplication? _webApp;
    private CancellationTokenSource? _webAppCts;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private static readonly HttpClient FaviconHttpClient = new();
    private string? _lastFaviconUri;
    private Icon? _currentFavicon;

    private const string LoadingPageHtml = """
<!doctype html>
<html lang="it">
<head>
  <meta charset="utf-8" />
  <title>Mail Viewer</title>
  <style>
    body {
      margin: 0;
      height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      font-family: "Segoe UI", Arial, sans-serif;
      background: #f8fafc;
      color: #1e293b;
    }
    .wrapper {
      text-align: center;
    }
    .spinner {
      width: 48px;
      height: 48px;
      border-radius: 50%;
      border: 4px solid rgba(37, 99, 235, 0.2);
      border-top-color: #2563eb;
      animation: spin 0.9s linear infinite;
      margin: 0 auto 16px;
    }
    @keyframes spin {
      to { transform: rotate(360deg); }
    }
  </style>
</head>
<body>
  <div class="wrapper">
    <div class="spinner"></div>
    <p>Caricamento interfaccia…</p>
  </div>
</body>
</html>
""";

    private static void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Console.WriteLine(logMessage);
        
        // Scrivi anche su file per debug
        try
        {
            var logFile = Path.Combine(Path.GetTempPath(), "MailViewer.Desktop.log");
            File.AppendAllText(logFile, logMessage + Environment.NewLine);
        }
        catch { } // Ignora errori di logging
    }

    public MailViewerForm()
    {
        InitializeComponent();
        Shown += MailViewerForm_ShownAsync;
        FormClosed += MailViewerForm_FormClosedAsync;
        webView.CoreWebView2InitializationCompleted += WebView_CoreWebView2InitializationCompleted;
        webView.NavigationCompleted += WebView_NavigationCompleted;
        webView.NavigationStarting += WebView_NavigationStarting;
    }

    private async void MailViewerForm_ShownAsync(object? sender, EventArgs e)
    {
        Shown -= MailViewerForm_ShownAsync;
        Log($"MailViewerForm_ShownAsync on thread {Environment.CurrentManagedThreadId} (UI {_uiThreadId}), HandleCreated={IsHandleCreated}");
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            Log("InitializeAsync: start");
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log($"InitializeAsync: WebView2 runtime version: {runtimeVersion}");
            if (string.IsNullOrWhiteSpace(runtimeVersion))
            {
                Log("InitializeAsync: WebView2 runtime not found");
                MessageBox.Show(this,
                    "Non è stato trovato il runtime Microsoft Edge WebView2. Installalo dal sito Microsoft per poter visualizzare i contenuti.",
                    "WebView2 mancante",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            Log($"InitializeAsync: reported WebView2 runtime {runtimeVersion}");

            await webView.EnsureCoreWebView2Async();
            if (webView.CoreWebView2 != null)
            {
                var version = webView.CoreWebView2.Environment?.BrowserVersionString ?? runtimeVersion;
                Log($"InitializeAsync: CoreWebView2 ready (runtime {version})");
                webView.CoreWebView2.NavigateToString(LoadingPageHtml);
                Log("InitializeAsync: loading page displayed");
            }
            else
            {
                Log("InitializeAsync: CoreWebView2 is null after EnsureCoreWebView2Async");
            }

            await Task.Yield();
            Log("InitializeAsync: resumed after displaying loading page");

            // Trova una porta libera invece di usare porta 0
            var availablePort = GetAvailablePort(5000, 5100);
            Log($"InitializeAsync: found available port: {availablePort}");
            
            // Verifica nuovamente che la porta sia libera
            if (!IsPortAvailable(availablePort))
            {
                Log($"InitializeAsync: Port {availablePort} is no longer available, searching for another");
                availablePort = GetAvailablePort(availablePort + 1, 5100);
            }

            var contentRoot = MailViewerApp.GetDefaultContentRoot();
            var webRoot = Path.GetFullPath(Path.Combine(contentRoot, "wwwroot"));
            Log($"InitializeAsync: determined WebRootPath: {webRoot}");

            var options = new MailViewerApp.MailViewerAppOptions
            {
                ContentRootPath = contentRoot,
                WebRootPath = webRoot,
                Urls = new[] { $"http://127.0.0.1:{availablePort}" }
            };
            Log($"InitializeAsync: options prepared ContentRoot='{options.ContentRootPath}' Urls=[{string.Join(",", options.Urls)}]");
            Log($"InitializeAsync: ContentRootPath exists: {Directory.Exists(options.ContentRootPath)}");

            _webAppCts = new CancellationTokenSource();

            // Avvia il build e il backend in un thread separato asincrono (fire-and-forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    Log("InitializeAsync.BuildTask: Building WebApplication");
                    var app = MailViewerApp.Build(options: options);
                    Log("InitializeAsync.BuildTask: WebApplication built successfully");

                    Log($"InitializeAsync.BackendStart: running on thread {Environment.CurrentManagedThreadId}");
                    await app.StartAsync(_webAppCts.Token).ConfigureAwait(false);
                    Log("InitializeAsync.BackendStart: StartAsync completed");

                    // Imposta _webApp sul thread UI
                    this.Invoke(() => _webApp = app);
                }
                catch (Exception ex)
                {
                    Log($"InitializeAsync.BackendStart: StartAsync failed: {ex}");
                }
            });

            Log("InitializeAsync: Checking backend readiness");

            // Aspetta che il server sia effettivamente raggiungibile
            var maxWaitTime = TimeSpan.FromSeconds(10);
            var startTime = DateTime.Now;
            var serverReady = false;
            
            while (DateTime.Now - startTime < maxWaitTime && !serverReady)
            {
                try
                {
                    using var client = new System.Net.Http.HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(1);
                    var response = await client.GetAsync($"http://127.0.0.1:{availablePort}/");
                    if (response.IsSuccessStatusCode)
                    {
                        serverReady = true;
                        Log("InitializeAsync: Backend is responding to requests");
                    }
                }
                catch
                {
                    // Server non ancora pronto
                }
                
                if (!serverReady)
                {
                    await Task.Delay(500); // Aspetta 500ms prima di riprovare
                }
            }
            
            if (!serverReady)
            {
                Log("InitializeAsync: Backend did not become ready within timeout");
                _webAppCts?.Cancel();
                // Il backend potrebbe non essere ancora avviato, ma la cancellazione dovrebbe fermarlo
                _webAppCts?.Dispose();
                _webAppCts = null;
                throw new InvalidOperationException("Il server interno non è diventato raggiungibile entro il timeout.");
            }

            if (IsDisposed || Disposing)
            {
                Log("InitializeAsync: form disposing after backend start, cleaning up");
                _webAppCts?.Cancel();
                // Se _webApp è già impostato, fermalo
                if (_webApp != null)
                {
                    await _webApp.StopAsync();
                    await _webApp.DisposeAsync();
                }
                _webAppCts?.Dispose();
                _webAppCts = null;
                return;
            }

            // _webApp è impostato nel task in background

            // Usiamo l'URL che abbiamo specificato direttamente
            var backendAddress = options.Urls[0];
            Log($"InitializeAsync: backend running at {backendAddress}");

            var baseAddress = backendAddress.EndsWith("/", StringComparison.Ordinal)
                ? backendAddress
                : backendAddress + "/";

            var uriBuilder = new UriBuilder(baseAddress)
            {
                Host = "127.0.0.1"
            };
            var navigationUri = uriBuilder.Uri;

            Log($"InitializeAsync: navigating WebView2 to {navigationUri}");

            if (webView.CoreWebView2 != null)
            {
                webView.CoreWebView2.Navigate(navigationUri.AbsoluteUri);
                Log($"InitializeAsync: CoreWebView2.Navigate -> {navigationUri}");
            }
            else
            {
                Log("InitializeAsync: CoreWebView2 was null when navigating to app");
            }

            webView.Source = navigationUri;
            Log("InitializeAsync: navigation assigned to WebView2 control");
            _ = RefreshFaviconAsync();
        }
        catch (Exception ex)
        {
            var errorMessage = $"Errore durante l'avvio dell'applicazione web: {ex}";
            Console.Error.WriteLine(errorMessage);
            Debug.WriteLine(ex);
            MessageBox.Show(this, $"Errore durante l'avvio dell'applicazione web: {ex.Message}", "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log($"InitializeAsync: exception {ex}");
            Close();
        }
    }


    private void RefreshButton_Click(object? sender, EventArgs e)
    {
        try
        {
            if (webView.CoreWebView2 != null)
            {
                Log("Toolbar: refresh requested");
                webView.CoreWebView2.Reload();
            }
            else if (webView.Source != null)
            {
                Log("Toolbar: refresh fallback via navigation restart");
                try
                {
                    var currentSource = webView.Source;
                    if (currentSource != null)
                    {
                        webView.Source = new Uri(currentSource.ToString());
                    }
                }
                catch (Exception navEx)
                {
                    Log($"Toolbar: refresh fallback failed {navEx.Message}");
                }
            }
            else
            {
                Log("Toolbar: refresh ignored, WebView2 not ready");
            }
        }
        catch (Exception ex)
        {
            Log($"Toolbar: refresh failed {ex}");
        }
    }

    private void ZoomResetButton_Click(object? sender, EventArgs e)
    {
        SetZoomFactor(1.0);
    }

    private void ZoomOutButton_Click(object? sender, EventArgs e)
    {
        ChangeZoomBy(-0.05);
    }

    private void ZoomInButton_Click(object? sender, EventArgs e)
    {
        ChangeZoomBy(0.05);
    }

    private void ChangeZoomBy(double delta)
    {
        var current = webView.ZoomFactor;
        SetZoomFactor(current + delta);
    }

    private void SetZoomFactor(double value)
    {
        var target = Math.Clamp(value, 0.25, 3.0);
        if (Math.Abs(webView.ZoomFactor - target) < 0.0001)
        {
            return;
        }

        try
        {
            webView.ZoomFactor = target;
            Log($"Toolbar: zoom set to {Math.Round(target * 100)}%");
        }
        catch (Exception ex)
        {
            Log($"Toolbar: zoom change failed {ex}");
        }
    }


    private async Task RefreshFaviconAsync()
    {
        try
        {
            var core = webView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            var faviconUri = core.FaviconUri;
            if (string.IsNullOrWhiteSpace(faviconUri))
            {
                return;
            }

            if (!Uri.TryCreate(faviconUri, UriKind.Absolute, out var uri))
            {
                Log($"RefreshFaviconAsync: invalid favicon URI '{faviconUri}'");
                return;
            }

            if (string.Equals(faviconUri, _lastFaviconUri, StringComparison.Ordinal))
            {
                return;
            }

            Log($"RefreshFaviconAsync: downloading {uri}");
            byte[] iconBytes;
            try
            {
                iconBytes = await FaviconHttpClient.GetByteArrayAsync(uri);
            }
            catch (Exception ex)
            {
                Log($"RefreshFaviconAsync: download failed {ex.Message}");
                return;
            }

            if (iconBytes.Length == 0)
            {
                Log("RefreshFaviconAsync: received empty favicon bytes");
                return;
            }

            void ApplyIcon()
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                try
                {
                    using var ms = new MemoryStream(iconBytes);
                    using var iconTemp = new Icon(ms);
                    var clone = (Icon)iconTemp.Clone();
                    _currentFavicon?.Dispose();
                    _currentFavicon = clone;
                    Icon = _currentFavicon;
                    _lastFaviconUri = faviconUri;
                    Log($"RefreshFaviconAsync: icon updated from {faviconUri}");
                }
                catch (Exception ex)
                {
                    Log($"RefreshFaviconAsync: failed to apply favicon {ex}");
                }
            }

            if (InvokeRequired)
            {
                if (!IsHandleCreated)
                {
                    return;
                }

                BeginInvoke((Action)ApplyIcon);
            }
            else
            {
                ApplyIcon();
            }
        }
        catch (Exception ex)
        {
            Log($"RefreshFaviconAsync: unexpected error {ex}");
        }
    }


    private static bool IsPortInUseException(Exception ex)
    {
        if (ex is null)
        {
            return false;
        }

        if (ex is AddressInUseException)
        {
            return true;
        }

        if (ex is IOException ioEx && ioEx.InnerException != null)
        {
            return IsPortInUseException(ioEx.InnerException);
        }

        return ex.InnerException != null && IsPortInUseException(ex.InnerException);
    }

    private static int GetAvailablePort(int startPort, int endPort)
    {
        for (int port = startPort; port <= endPort; port++)
        {
            if (IsPortAvailable(port))
                return port;
        }
        throw new InvalidOperationException($"Nessuna porta disponibile nell'intervallo {startPort}-{endPort}");
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private void WebView_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        Log($"CoreWebView2InitializationCompleted: Success={e.IsSuccess}");
        if (!e.IsSuccess)
        {
            Console.Error.WriteLine($"MailViewer: WebView2 initialization failed: {e.InitializationException}");
            Log($"CoreWebView2InitializationCompleted: failure {e.InitializationException}");
            return;
        }

        if (webView.CoreWebView2 != null)
        {
            Log("CoreWebView2InitializationCompleted: CoreWebView2 available, configuring events");
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                Log($"[WebView2] NavigationCompleted Success={args.IsSuccess} StatusCode={args.WebErrorStatus}");
                if (!args.IsSuccess)
                {
                    BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show(this, $"WebView2 non è riuscito a caricare la pagina: {args.WebErrorStatus}", "WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }));
                }
            };
            webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                Log($"[WebView2] NavigationStarting {args.Uri}");
            };
            webView.CoreWebView2.ContentLoading += (_, args) =>
            {
                Log($"[WebView2] ContentLoading IsErrorPage={args.IsErrorPage}");
            };
            webView.CoreWebView2.SourceChanged += (_, _) =>
            {
                Log($"[WebView2] SourceChanged -> {webView.CoreWebView2.Source}");
            };
            webView.CoreWebView2.ProcessFailed += (_, args) =>
            {
                Log($"[WebView2] ProcessFailed Kind={args.ProcessFailedKind}");
            };
            webView.CoreWebView2.FaviconChanged += (_, _) => _ = RefreshFaviconAsync();
            webView.CoreWebView2.OpenDevToolsWindow();
            Log("CoreWebView2InitializationCompleted: DevTools window requested");
            _ = RefreshFaviconAsync();
        }
        else
        {
            Log("CoreWebView2InitializationCompleted: CoreWebView2 is null");
        }
    }

    private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Log($"MailViewer: NavigationCompleted Source={webView.Source} Success={e.IsSuccess} Status={e.WebErrorStatus}");
        _ = RefreshFaviconAsync();
    }

    private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Log($"MailViewer: NavigationStarting Uri={e.Uri}");
    }

    private async void MailViewerForm_FormClosedAsync(object? sender, FormClosedEventArgs e)
    {
        Log($"FormClosedAsync: invoked (Reason={e.CloseReason})");
        if (_webApp == null)
        {
            Log("FormClosedAsync: web app already null");
            return;
        }

        try
        {
            if (_webAppCts != null && !_webAppCts.IsCancellationRequested)
            {
                _webAppCts.Cancel();
                Log("FormClosedAsync: cancellation requested for backend");
            }
            await _webApp.StopAsync();
            Log("FormClosedAsync: backend stopped");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore durante l'arresto dell'applicazione web: {ex}");
            Debug.WriteLine(ex);
            Log($"FormClosedAsync: exception {ex}");
        }
        finally
        {
            await _webApp.DisposeAsync();
            _webApp = null;
            _webAppCts?.Dispose();
            _webAppCts = null;
            _currentFavicon?.Dispose();
            _currentFavicon = null;
            _lastFaviconUri = null;
            Log("FormClosedAsync: backend disposed");
        }
    }
}
