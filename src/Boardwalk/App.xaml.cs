using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Boardwalk.Services;

namespace Boardwalk;

public partial class App : Application
{
    /// <summary>Set by "--self-test &lt;output.txt&gt; [screenshot folder]" (used by the build to test Boardwalk).</summary>
    public static string SelfTestOutput { get; private set; }
    public static string SelfTestShots { get; private set; }
    public static bool IsSelfTest => SelfTestOutput != null;
    internal static string MainWindowTestName = "(starting)";

    public static string Version =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        int st = Array.FindIndex(e.Args, a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase));
        if (st >= 0)
        {
            SelfTestOutput = st + 1 < e.Args.Length ? Path.GetFullPath(e.Args[st + 1]) : Path.Combine(Path.GetTempPath(), "boardwalk-selftest.txt");
            SelfTestShots = st + 2 < e.Args.Length ? Path.GetFullPath(e.Args[st + 2]) : null;
            // Never touch the real settings or boards while testing.
            var temp = Path.Combine(Path.GetTempPath(), "BoardwalkSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
            SettingsService.Folder = temp;
            BoardStore.Folder = temp;
        }

        SettingsService.Load();
        ThemeManager.Apply(SettingsService.Current);
        SettingsService.Changed += () => ThemeManager.Apply(SettingsService.Current);

        BoardStore.Load();

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is ResourceReferenceKeyNotFoundException rk)
        {
            if (IsSelfTest)
            {
                try { File.AppendAllText(SelfTestOutput, $"FAIL missing resource '{rk.Key}' during \"{MainWindowTestName}\"\r\n"); } catch { }
            }
            e.Handled = true;
            return;
        }
        if (IsSelfTest)
        {
            try { File.AppendAllText(SelfTestOutput, $"FAIL unhandled exception during \"{MainWindowTestName}\": {e.Exception}\r\n"); } catch { }
            e.Handled = true;
            Environment.Exit(99);
            return;
        }
        // Boards are saved after every change, so make one last attempt and report the problem.
        try { BoardStore.Save(); } catch { }
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(Path.Combine(SettingsService.Folder, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\r\n\r\n");
        }
        catch { }
        Dialogs.MessageDialog.Show(MainWindow, "Something went wrong",
            "Boardwalk hit an unexpected error. Your boards were saved.\n\n" + e.Exception.Message, Dialogs.MessageDialog.Kind.Error);
        e.Handled = true;
    }
}
