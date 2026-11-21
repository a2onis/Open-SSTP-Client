using System.Runtime.InteropServices;
using OpenSstpWin.UI;

namespace OpenSstpWin;

/// <summary>Double-click entry point; command-line arguments still use the console frontend.</summary>
internal static class DesktopApp
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static void Run()
    {
        // Detach only from a console created for this process, never the user's terminal.
        var processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) == 1)
            FreeConsole();

        // A single failing event handler must never take the whole app down.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);

        // The existing asynchronous CLI entry point is not STA. WinForms needs its own STA thread.
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ReportCrash(ex, "Could not start Open SSTP Client");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void ReportCrash(Exception exception, string? title = null)
    {
        string? logPath = null;
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenSstpWin");
            Directory.CreateDirectory(folder);
            logPath = Path.Combine(folder, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            File.WriteAllText(logPath, DateTime.Now + Environment.NewLine + exception);
        }
        catch
        {
            // Reporting must never throw again.
        }

        try
        {
            MessageBox.Show(
                (title ?? "Open SSTP Client hit an unexpected error") + ":\n\n" +
                exception.Message +
                (logPath is null ? string.Empty : "\n\nA report was saved to:\n" + logPath) +
                "\n\nThe app will keep running. Disconnect and reconnect if the tunnel misbehaves.",
                "Open SSTP Client",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Ignore failures while showing the report.
        }
    }
}
