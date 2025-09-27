using System;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MailViewer.Desktop;

public partial class TestForm : Form
{
    private WebView2 webView = null!;

    public TestForm()
    {
        InitializeComponent();
        Load += TestForm_Load;
    }

    private void InitializeComponent()
    {
        webView = new WebView2();
        SuspendLayout();
        
        webView.Dock = DockStyle.Fill;
        webView.Name = "webView";
        webView.TabIndex = 0;
        webView.ZoomFactor = 1D;
        
        AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new System.Drawing.Size(800, 600);
        Controls.Add(webView);
        Name = "TestForm";
        Text = "Test WebView2";
        StartPosition = FormStartPosition.CenterScreen;
        
        ResumeLayout(false);
    }

    private async void TestForm_Load(object? sender, EventArgs e)
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            MessageBox.Show($"WebView2 Runtime Version: {version}");
            
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.NavigateToString(@"
                <html>
                <head><title>Test</title></head>
                <body>
                    <h1>WebView2 Test</h1>
                    <p>Se vedi questo messaggio, WebView2 funziona correttamente!</p>
                </body>
                </html>
            ");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Errore: {ex.Message}");
        }
    }
}