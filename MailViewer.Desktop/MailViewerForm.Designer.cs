#nullable disable
using System.Drawing;
using System.Windows.Forms;

namespace MailViewer.Desktop;

partial class MailViewerForm
{
    private System.ComponentModel.IContainer components;
    private Microsoft.Web.WebView2.WinForms.WebView2 webView;
    private FlowLayoutPanel toolbarPanel;
    private Button homeButton;
    private Button refreshButton;
    private Button zoomResetButton;
    private Button zoomOutButton;
    private Button zoomInButton;
    private StatusStrip statusStrip;
    private ToolStripStatusLabel statusLabel;


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
        toolbarPanel = new FlowLayoutPanel();
        homeButton = new Button();
        refreshButton = new Button();
        zoomResetButton = new Button();
        zoomOutButton = new Button();
        zoomInButton = new Button();
        statusStrip = new StatusStrip();
        statusLabel = new ToolStripStatusLabel();
        webView = new Microsoft.Web.WebView2.WinForms.WebView2();
        SuspendLayout();
        // 
        // toolbarPanel
        // 
        toolbarPanel.Dock = DockStyle.Top;
        toolbarPanel.FlowDirection = FlowDirection.LeftToRight;
        toolbarPanel.Location = new Point(0, 0);
        toolbarPanel.Name = "toolbarPanel";
        toolbarPanel.Padding = new Padding(8, 8, 8, 8);
        toolbarPanel.Size = new Size(984, 48);
        toolbarPanel.TabIndex = 1;
        toolbarPanel.WrapContents = false;
        // 
        // homeButton
        // 
        homeButton.AutoSize = true;
        homeButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        homeButton.Margin = new Padding(0, 0, 8, 0);
        homeButton.Name = "homeButton";
        homeButton.Size = new Size(55, 25);
        homeButton.TabIndex = 0;
        homeButton.Text = "Home";
        homeButton.UseVisualStyleBackColor = true;
        homeButton.Click += HomeButton_Click;
        // 
        // refreshButton
        // 
        refreshButton.AutoSize = true;
        refreshButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        refreshButton.Margin = new Padding(0, 0, 8, 0);
        refreshButton.Name = "refreshButton";
        refreshButton.Size = new Size(70, 25);
        refreshButton.TabIndex = 1;
        refreshButton.Text = "Aggiorna";
        refreshButton.UseVisualStyleBackColor = true;
        refreshButton.Click += RefreshButton_Click;
        // 
        // zoomResetButton
        // 
        zoomResetButton.AutoSize = true;
        zoomResetButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        zoomResetButton.Margin = new Padding(0, 0, 8, 0);
        zoomResetButton.Name = "zoomResetButton";
        zoomResetButton.Size = new Size(84, 25);
        zoomResetButton.TabIndex = 2;
        zoomResetButton.Text = "Zoom 100%";
        zoomResetButton.UseVisualStyleBackColor = true;
        zoomResetButton.Click += ZoomResetButton_Click;
        // 
        // zoomOutButton
        // 
        zoomOutButton.AutoSize = true;
        zoomOutButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        zoomOutButton.Margin = new Padding(0, 0, 8, 0);
        zoomOutButton.Name = "zoomOutButton";
        zoomOutButton.Size = new Size(81, 25);
        zoomOutButton.TabIndex = 3;
        zoomOutButton.Text = "Zoom -5%";
        zoomOutButton.UseVisualStyleBackColor = true;
        zoomOutButton.Click += ZoomOutButton_Click;
        // 
        // zoomInButton
        // 
        zoomInButton.AutoSize = true;
        zoomInButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        zoomInButton.Margin = new Padding(0, 0, 0, 0);
        zoomInButton.Name = "zoomInButton";
        zoomInButton.Size = new Size(78, 25);
        zoomInButton.TabIndex = 4;
        zoomInButton.Text = "Zoom +5%";
        zoomInButton.UseVisualStyleBackColor = true;
        zoomInButton.Click += ZoomInButton_Click;
        // 
        // statusStrip
        // 
        statusStrip.Dock = DockStyle.Bottom;
        statusStrip.Items.AddRange(new ToolStripItem[] { statusLabel });
        statusStrip.Location = new Point(0, 639);
        statusStrip.Name = "statusStrip";
        statusStrip.Padding = new Padding(1, 0, 10, 0);
        statusStrip.Size = new Size(984, 22);
        statusStrip.SizingGrip = false;
        statusStrip.TabIndex = 2;
        statusStrip.Text = "statusStrip";
        // 
        // statusLabel
        // 
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(973, 17);
        statusLabel.Spring = true;
        statusLabel.Text = "Pronto";
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // webView
        // 
        webView.AllowExternalDrop = true;
        webView.CreationProperties = null;
        webView.DefaultBackgroundColor = Color.White;
        webView.Dock = DockStyle.Fill;
        webView.Location = new Point(0, 48);
        webView.Name = "webView";
        webView.Size = new Size(984, 591);
        webView.TabIndex = 0;
        webView.ZoomFactor = 1D;
        // 
        // MailViewerForm
        // 
        toolbarPanel.Controls.Add(homeButton);
        toolbarPanel.Controls.Add(refreshButton);
        toolbarPanel.Controls.Add(zoomResetButton);
        toolbarPanel.Controls.Add(zoomOutButton);
        toolbarPanel.Controls.Add(zoomInButton);
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(984, 661);
        Controls.Add(webView);
        Controls.Add(statusStrip);
        Controls.Add(toolbarPanel);
        Name = "MailViewerForm";
        Text = "Mail Viewer";
        StartPosition = FormStartPosition.CenterScreen;
        ResumeLayout(false);
    }

    #endregion
}
