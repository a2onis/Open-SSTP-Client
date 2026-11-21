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
                MessageBox.Show("Could not start Open SSTP Client: " + ex.Message,
                    "Open SSTP Client", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
