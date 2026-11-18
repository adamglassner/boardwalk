using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Boardwalk.Services;

/// <summary>A card that's due soon, with its board.</summary>
public sealed record DueCard(Board Board, Card Card, Column Column);

/// <summary>
/// The notification-area icon and the daily due-date reminder. Windows 10 and 11 show the
/// reminder as a notification. Optionally keeps Boardwalk running when its window is closed.
/// </summary>
public static class ReminderService
{
    private static Forms.NotifyIcon _icon;
    private static DispatcherTimer _timer;

    /// <summary>Asked to show the window (and My week when <c>true</c>).</summary>
    public static event Action<bool> OpenRequested;
    public static event Action ExitRequested;

    /// <summary>Cards that aren't complete or archived, due on or before <paramref name="until"/>, soonest first.</summary>
    public static List<DueCard> DueCards(DateTime until)
    {
        var list = new List<DueCard>();
        foreach (var b in BoardStore.Boards)
            foreach (var col in b.Columns.Where(c => !c.IsDone))
                foreach (var c in col.Cards)
                    if (!c.Archived && c.Due is DateTime d && d.Date <= until.Date) list.Add(new DueCard(b, c, col));
        return list.OrderBy(x => x.Card.Due).ThenByDescending(x => x.Card.Priority).ToList();
    }

    /// <summary>The reminder text for today, or null when nothing is due.</summary>
    public static string ReminderText(out string title)
    {
        var due = DueCards(DateTime.Today);
        int overdue = due.Count(x => x.Card.Due.Value.Date < DateTime.Today);
        int today = due.Count - overdue;
        title = "";
        if (due.Count == 0) return null;
        var parts = new List<string>();
        if (today > 0) parts.Add($"{today} card{(today == 1 ? "" : "s")} due today");
        if (overdue > 0) parts.Add($"{overdue} overdue");
        title = "Boardwalk: " + string.Join(", ", parts);
        var lines = due.Take(3).Select(x => $"• {x.Card.Title} ({x.Board.Name})").ToList();
        if (due.Count > 3) lines.Add($"…and {due.Count - 3} more");
        return string.Join("\n", lines);
    }

    public static void Start()
    {
        Apply();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _timer.Tick += (_, _) => CheckReminder();
        _timer.Start();
        // First check shortly after start-up.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first.Stop(); CheckReminder(); };
        first.Start();
        SettingsService.Changed += Apply;
    }

    /// <summary>Shows or hides the icon and updates the start-with-Windows entry to match the settings.</summary>
    public static void Apply()
    {
        var s = SettingsService.Current;
        bool want = s.RemindersOn || s.CloseToTray;
        if (want && _icon == null) CreateIcon();
        if (_icon != null) _icon.Visible = want;
        SetStartWithWindows(s.StartWithWindows);
    }

    private static void CreateIcon()
    {
        _icon = new Forms.NotifyIcon { Text = "Boardwalk" };
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/boardwalk.ico"))?.Stream;
            if (stream != null) _icon.Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Boardwalk", null, (_, _) => OpenRequested?.Invoke(false));
        menu.Items.Add("My week", null, (_, _) => OpenRequested?.Invoke(true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(false);
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(true);
    }

    /// <summary>Shows today's reminder once a day, at or after the chosen hour.</summary>
    public static void CheckReminder(bool force = false)
    {
        var s = SettingsService.Current;
        if (!force && (!s.RemindersOn || DateTime.Now.Hour < s.ReminderHour || s.LastReminderDay == DateTime.Today.ToString("yyyy-MM-dd"))) return;
        var text = ReminderText(out var title);
        s.LastReminderDay = DateTime.Today.ToString("yyyy-MM-dd");
        SettingsService.Save();
        if (text == null) return;
        if (_icon == null) CreateIcon();
        _icon.Visible = true;
        _icon.ShowBalloonTip(10000, title, text, Forms.ToolTipIcon.None);
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static void SetStartWithWindows(bool on)
    {
        if (App.IsSelfTest) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (on && exe != null) key.SetValue("Boardwalk", $"\"{exe}\" --tray");
            else if (key.GetValue("Boardwalk") != null) key.DeleteValue("Boardwalk", false);
        }
        catch { }
    }

    public static void Stop()
    {
        _timer?.Stop();
        if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; }
    }
}
