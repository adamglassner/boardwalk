using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boardwalk.Services;

namespace Boardwalk.Dialogs;

/// <summary>Settings. Every change applies straight away.</summary>
public sealed class SettingsWindow : DialogBase
{
    private readonly MainWindow _main;

    public SettingsWindow(MainWindow main) : base("Settings", 560)
    {
        _main = main;
        var s = SettingsService.Current;
        var panel = new StackPanel { Width = 500 };

        // ---------- Appearance ----------
        panel.Children.Add(Header("Appearance"));
        panel.Children.Add(Label("Mode:"));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (mode, text) in new[] { (ThemeMode.System, "Follow Windows"), (ThemeMode.Light, "Light"), (ThemeMode.Dark, "Dark") })
        {
            var m = mode;
            var rb = new RadioButton { Content = text, GroupName = "theme", IsChecked = s.Theme == mode, Margin = new Thickness(0, 0, 18, 0) };
            rb.Checked += (_, _) => { s.Theme = m; SettingsService.Commit(); };
            modes.Children.Add(rb);
        }
        panel.Children.Add(modes);

        panel.Children.Add(Label("Accent colour:"));
        var accents = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void BuildAccents()
        {
            accents.Children.Clear();
            foreach (var a in ThemeManager.Accents)
            {
                var acc = a;
                var btn = new Button
                {
                    Style = (Style)FindResource("SwatchButton"), Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 8),
                    Background = new SolidColorBrush(ThemeManager.IsDark ? a.Dark : a.Light), BorderThickness = new Thickness(0), ToolTip = a.Name,
                };
                if (s.Accent == a.Name)
                    btn.Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = ThemeManager.IsDark ? Brushes.Black : Brushes.White };
                btn.Click += (_, _) => { s.Accent = acc.Name; SettingsService.Commit(); BuildAccents(); };
                accents.Children.Add(btn);
            }
        }
        BuildAccents();
        ThemeManager.ThemeChanged += BuildAccents;
        Closed += (_, _) => ThemeManager.ThemeChanged -= BuildAccents;
        panel.Children.Add(accents);

        panel.Children.Add(Label("Column width:"));
        var width = Combo(new object[] { "Narrow", "Normal", "Wide" }, s.ColumnWidth < 270 ? 0 : s.ColumnWidth > 310 ? 2 : 1, 160);
        width.SelectionChanged += (_, _) => { s.ColumnWidth = width.SelectedIndex switch { 0 => 250, 2 => 340, _ => 290 }; SettingsService.Commit(); };
        width.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(width);

        panel.Children.Add(Check("Compact cards (titles and labels only)", s.CompactCards, v => s.CompactCards = v));
        panel.Children.Add(Check("Show the start of a card's notes on the card", s.ShowNotesPreview, v => s.ShowNotesPreview = v));

        // ---------- Behaviour ----------
        panel.Children.Add(Header("Deleting"));
        panel.Children.Add(Check("Ask before deleting a card, column or board", s.ConfirmDeletes, v => s.ConfirmDeletes = v));

        // ---------- Your boards ----------
        panel.Children.Add(Header("Your boards"));
        panel.Children.Add(Hint($"Boards are saved on this PC after every change, in {BoardStore.Folder}. The previous version is kept as boards.json.bak."));
        var dataRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        dataRow.Children.Add(SmallButton("Open folder", () =>
        {
            Directory.CreateDirectory(BoardStore.Folder);
            try { Process.Start(new ProcessStartInfo(BoardStore.Folder) { UseShellExecute = true }); } catch { }
        }));
        dataRow.Children.Add(SmallButton("Back up all boards…", ExportAll));
        dataRow.Children.Add(SmallButton("Import boards…", Import));
        panel.Children.Add(dataRow);

        // ---------- Sync ----------
        var syncHeader = new StackPanel { Orientation = Orientation.Horizontal };
        syncHeader.Children.Add(Header("Sync"));
        var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(10, 14, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        var badgeText = new TextBlock { Text = "Coming soon", FontSize = 11, FontWeight = FontWeights.SemiBold };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        badge.Child = badgeText;
        syncHeader.Children.Add(badge);
        panel.Children.Add(syncHeader);
        panel.Children.Add(Hint("Syncing your boards between your PCs and devices is coming in a future version. For now Boardwalk keeps everything on this PC. Use Back up all boards and Import boards to move them to another PC."));

        // ---------- About ----------
        panel.Children.Add(Header("About"));
        var about = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        about.Children.Add(new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/boardwalk-256.png")),
            Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0),
        });
        var aboutText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        aboutText.Children.Add(new TextBlock { Text = "Boardwalk " + App.Version, FontWeight = FontWeights.SemiBold });
        var copy = Hint("Kanban boards for Windows 10 and 11 · © 2026 Bytehawk Studio");
        copy.Margin = new Thickness(0);
        aboutText.Children.Add(copy);
        about.Children.Add(aboutText);
        panel.Children.Add(about);
        panel.Children.Add(Label("What's new:", 4));
        panel.Children.Add(new TextBox
        {
            Text = WhatsNew(), IsReadOnly = true, Height = 130, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });

        Body.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        AddButton("Close", primary: true, onClick: () => { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } });
    }

    private static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = text, IsChecked = value };
        cb.Checked += (_, _) => { set(true); SettingsService.Commit(); };
        cb.Unchecked += (_, _) => { set(false); SettingsService.Commit(); };
        return cb;
    }

    private static Button SmallButton(string text, Action click)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        b.Click += (_, _) => click();
        return b;
    }

    private void ExportAll()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Boardwalk boards (*.json)|*.json", FileName = $"Boardwalk backup {DateTime.Now:yyyy-MM-dd}.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            BoardStore.Save();
            File.Copy(BoardStore.FilePath, dlg.FileName, true);
            MessageDialog.Show(this, "Back up", $"Saved {BoardStore.Boards.Count} board{(BoardStore.Boards.Count == 1 ? "" : "s")} to {dlg.FileName}.");
        }
        catch (Exception ex) { MessageDialog.Show(this, "Back up", "Couldn't save the backup.\n\n" + ex.Message, MessageDialog.Kind.Error); }
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Boardwalk boards (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var added = BoardStore.ImportBoards(dlg.FileName);
            _main.RefreshBoardList(added.FirstOrDefault());
            MessageDialog.Show(this, "Import", added.Count == 0 ? "That file has no boards in it." : $"Added {added.Count} board{(added.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex) { MessageDialog.Show(this, "Import", "That file couldn't be read as Boardwalk boards.\n\n" + ex.Message, MessageDialog.Kind.Error); }
    }

    internal static string WhatsNew()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (stream == null) return "";
            var lines = new StreamReader(stream).ReadToEnd().Replace("\r", "").Split('\n');
            int start = Array.FindIndex(lines, l => l.StartsWith("## "));
            if (start < 0) return "";
            var section = new List<string> { lines[start][3..] };
            for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## "); i++) section.Add(lines[i].Replace("**", "").Replace("### ", ""));
            return string.Join("\n", section).Trim();
        }
        catch { return ""; }
    }
}
