#nullable disable
using System.Drawing;
using System.Windows.Forms;

namespace MailViewer.Desktop;

partial class MailViewerForm
{
    private System.ComponentModel.IContainer components;
    private Microsoft.Web.WebView2.WinForms.WebView2 webView;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            components?.Dispose();
            webView?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        webView = new Microsoft.Web.WebView2.WinForms.WebView2();
        SuspendLayout();
        // 
        // webView
        // 
        webView.AllowExternalDrop = true;
        webView.CreationProperties = null;
        webView.DefaultBackgroundColor = Color.White;
        webView.Dock = DockStyle.Fill;
        webView.Location = new Point(0, 0);
        webView.Name = "webView";
        webView.Size = new Size(984, 661);
        webView.TabIndex = 0;
        webView.ZoomFactor = 1D;
        // 
        // MailViewerForm
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(984, 661);
        Controls.Add(webView);
        Name = "MailViewerForm";
        Text = "Mail Viewer";
        StartPosition = FormStartPosition.CenterScreen;
        ResumeLayout(false);
    }

    #endregion
}
