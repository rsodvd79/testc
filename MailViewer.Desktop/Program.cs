using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MailViewer.Desktop;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [STAThread]
    private static void Main()
    {
        // Alloca una console per vedere i log
        AllocConsole();
        
        ApplicationConfiguration.Initialize();
        Application.Run(new MailViewerForm());
    }
}
