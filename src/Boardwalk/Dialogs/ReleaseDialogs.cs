using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boardwalk.Services;

namespace Boardwalk.Dialogs;

/// <summary>Turns the completed cards of one version into a CHANGELOG.md section.</summary>
public sealed class ChangelogDialog : DialogBase
{
    private readonly Board _board;
    private readonly ComboBox _version;
    private readonly TextBox _date, _output;
    private readonly CheckBox _numbers;

    public ChangelogDialog(Board board) : base("Write changelog", 620)
    {
        _board = board;
        var versions = BoardStore.Versions(board);
        Body.Children.Add(Hint("Completed cards with this version, sorted into Added (Feature labels), Fixed (Bug labels) and Changed (everything else)."));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
        row.Children.Add(new TextBlock { Text = "Version:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _version = new ComboBox { Width = 140 };
        foreach (var v in versions) _version.Items.Add(v);
        int start = versions.IndexOf(board.NextVersion.Trim());
        _version.SelectedIndex = versions.Count == 0 ? -1 : Math.Max(0, start);
        row.Children.Add(_version);
        row.Children.Add(new TextBlock { Text = "Date:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0) });
        _date = new TextBox { Width = 120, Text = DateTime.Today.ToString("yyyy-MM-dd") };
        row.Children.Add(_date);
        _numbers = new CheckBox { Content = "Card numbers", IsChecked = false, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_numbers);
        Body.Children.Add(row);

        _output = new TextBox
        {
            Width = 570, Height = 320, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Left,
        };
        Body.Children.Add(_output);
        if (versions.Count == 0)
            Body.Children.Add(Hint("No cards have a version yet. Set the version you're working on in Board settings, or give cards a version in their window."));

        _version.SelectionChanged += (_, _) => Update();
        _date.TextChanged += (_, _) => Update();
        _numbers.Checked += (_, _) => Update();
        _numbers.Unchecked += (_, _) => Update();
        Update();

        AddButton("Start next version…", onClick: NextVersion).ToolTip = "Set the version you're working on next";
        AddButton("Save as…", onClick: SaveAs);
        AddButton("Copy", onClick: () => { Clipboard.SetText(_output.Text); ((Button)ButtonRow.Children[2]).Content = "Copied"; });
        AddButton("Close", primary: true, cancel: true, onClick: () => { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } });
    }

    internal string Text => _output.Text;

    private void Update()
    {
        if (_version.SelectedItem is not string v) { _output.Text = ""; return; }
        var date = DateTime.TryParse(_date.Text, out var d) ? d : DateTime.Today;
        _output.Text = BoardStore.Changelog(_board, v, date, _numbers.IsChecked == true);
        if (ButtonRow.Children.Count > 2) ((Button)ButtonRow.Children[2]).Content = "Copy";
    }

    private void SaveAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = "CHANGELOG section.md" };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, _output.Text); }
        catch (Exception ex) { MessageDialog.Show(this, "Save", "Couldn't save the file.\n\n" + ex.Message, MessageDialog.Kind.Error); }
    }

    /// <summary>1.2.0 → 1.3.0 (keeps any "v").</summary>
    internal static string Bump(string v)
    {
        var t = (v ?? "").Trim();
        var prefix = t.StartsWith('v') || t.StartsWith('V') ? t[..1] : "";
        if (Version.TryParse(t.TrimStart('v', 'V'), out var p))
            return prefix + (p.Build >= 0 ? $"{p.Major}.{p.Minor + 1}.0" : $"{p.Major}.{p.Minor + 1}");
        return t;
    }

    private void NextVersion()
    {
        var current = _board.NextVersion.Trim().Length > 0 ? _board.NextVersion : (_version.SelectedItem as string ?? "1.0.0");
        var d = new TextPromptDialog("Start next version", "Version you're working on next:", Bump(current),
            "New completed cards get this version. Cards already completed keep theirs.") { Owner = this };
        if (d.ShowDialog() != true || d.Value.Length == 0) return;
        _board.NextVersion = d.Value;
        BoardStore.Touch(_board);
        MessageDialog.Show(this, "Next version", $"Now working on {d.Value}.");
    }
}

/// <summary>Counts and charts for one board.</summary>
public sealed class StatsDialog : DialogBase
{
    public StatsDialog(Board board) : base($"Stats · {board.Name}", 600)
    {
        var panel = new StackPanel { Width = 540 };
        var open = board.AllCards.Where(c => !c.Archived && !board.IsComplete(c)).ToList();
        var completed = board.AllCards.Where(board.IsComplete).Select(c => (Card: c, At: BoardStore.CompletedAt(board, c) ?? c.Modified)).ToList();
        var weekStart = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));   // Monday
        int thisWeek = completed.Count(x => x.At >= weekStart);
        int overdue = open.Count(c => c.Due is DateTime d && d.Date < DateTime.Today);
        int blocked = open.Count(c => board.OpenBlockers(c).Count > 0);
        var recent = completed.Where(x => x.At >= DateTime.Today.AddDays(-90)).ToList();
        string avg = recent.Count == 0 ? "–" : $"{recent.Average(x => (x.At - x.Card.Created).TotalDays):0.#}";

        var tiles = new UniformGrid5();
        tiles.Add("Open", open.Count.ToString());
        tiles.Add("Done this week", thisWeek.ToString());
        tiles.Add("Overdue", overdue.ToString(), overdue > 0 ? "DangerText" : null);
        tiles.Add("Blocked", blocked.ToString(), blocked > 0 ? "WarnText" : null);
        tiles.Add("Days to finish", avg, null, "Average days from making a card to completing it (last 90 days)");
        panel.Children.Add(tiles.Panel);

        panel.Children.Add(Header("Cards in each status"));
        int max = Math.Max(1, board.Columns.Select(c => c.ActiveCount).DefaultIfEmpty(0).Max());
        foreach (var col in board.Columns)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var name = new TextBlock { Text = col.Title, Width = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(name);
            var count = new TextBlock { Text = col.ActiveCount.ToString(), Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var bar = new Border { Height = 16, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.Brush(BoardStore.Hex(col.Color)), Width = Math.Max(2, 340.0 * col.ActiveCount / max) };
            row.Children.Add(bar);
            panel.Children.Add(row);
        }

        panel.Children.Add(Header("Completed each week"));
        var weeks = Enumerable.Range(0, 8).Select(i => weekStart.AddDays(-7 * (7 - i))).ToList();
        var counts = weeks.Select(w => completed.Count(x => x.At >= w && x.At < w.AddDays(7))).ToList();
        int wmax = Math.Max(1, counts.Max());
        var chart = new Grid { Height = 150 };
        for (int i = 0; i < weeks.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            var cell = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };
            var label = new TextBlock { Text = weeks[i].ToString("MMM d"), FontSize = 11, TextAlignment = TextAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            DockPanel.SetDock(label, Dock.Bottom);
            cell.Children.Add(label);
            var value = new TextBlock { Text = counts[i].ToString(), FontSize = 11, TextAlignment = TextAlignment.Center };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            stack.Children.Add(value);
            var bar = new Border { Height = Math.Max(2, 100.0 * counts[i] / wmax), CornerRadius = new CornerRadius(3, 3, 0, 0), Margin = new Thickness(0, 2, 0, 4) };
            bar.SetResourceReference(Border.BackgroundProperty, "Accent");
            stack.Children.Add(bar);
            cell.Children.Add(stack);
            Grid.SetColumn(cell, i);
            chart.Children.Add(cell);
        }
        panel.Children.Add(chart);

        panel.Children.Add(Header("Waiting longest"));
        var oldest = open.Select(c => (Card: c, Since: BoardStore.InStatusSince(board, c))).OrderBy(x => x.Since).Take(5).ToList();
        foreach (var (c, since) in oldest)
            panel.Children.Add(new TextBlock { Text = $"#{c.Number} {c.Title}  ·  {board.ColumnOf(c)?.Title} for {BoardStore.Ago(DateTime.Now - since)}", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) });
        if (oldest.Count == 0) panel.Children.Add(Hint("No open cards."));

        Body.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });
        AddButton("Close", primary: true, cancel: true, onClick: () => { try { DialogResult = true; } catch (InvalidOperationException) { Close(); } });
    }

    /// <summary>A row of five number tiles.</summary>
    private sealed class UniformGrid5
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Columns = 5, Margin = new Thickness(0, 0, 0, 4) };

        public void Add(string label, string value, string colorKey = null, string tip = null)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 8, 0), BorderThickness = new Thickness(1), ToolTip = tip };
            b.SetResourceReference(Border.BackgroundProperty, "CardBg");
            b.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var sp = new StackPanel();
            var v = new TextBlock { Text = value, FontSize = 22, FontWeight = FontWeights.SemiBold };
            if (colorKey != null) v.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            sp.Children.Add(v);
            var l = new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(l);
            b.Child = sp;
            Panel.Children.Add(b);
        }
    }
}

/// <summary>Pick which open GitHub issues to add as cards.</summary>
public sealed class ImportIssuesDialog : DialogBase
{
    private readonly List<(GitHubIssue Issue, CheckBox Box)> _rows = new();

    public List<GitHubIssue> Selected => _rows.Where(r => r.Box.IsChecked == true && r.Box.IsEnabled).Select(r => r.Issue).ToList();

    public ImportIssuesDialog(Board board, string repo, List<GitHubIssue> issues) : base("Import GitHub issues", 560)
    {
        var have = board.AllCards.Select(c => c.Link.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var col = board.FirstOpenColumn;
        Body.Children.Add(Hint($"{issues.Count} open issue{(issues.Count == 1 ? "" : "s")} in {repo}. Ticked ones become cards in {col?.Title ?? "the first column"}, with a link back to GitHub."));
        var list = new StackPanel();
        foreach (var i in issues)
        {
            bool already = have.Contains(i.Url.TrimEnd('/'));
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"#{i.Number}  {i.Title}", TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            var extra = already ? "Already on this board" : string.Join(", ", i.Labels);
            if (extra.Length > 0)
            {
                var h = new TextBlock { Text = extra, FontSize = 12 };
                h.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                sp.Children.Add(h);
            }
            var cb = new CheckBox { Content = sp, IsChecked = !already, IsEnabled = !already, Margin = new Thickness(0, 0, 0, 8) };
            _rows.Add((i, cb));
            list.Children.Add(cb);
        }
        if (issues.Count == 0) list.Children.Add(new TextBlock { Text = "This repository has no open issues." });
        Body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 4) });
        AddButton("Select all", onClick: () => { foreach (var r in _rows) if (r.Box.IsEnabled) r.Box.IsChecked = true; });
        AddButton("Select none", onClick: () => { foreach (var r in _rows) r.Box.IsChecked = false; });
        AddButton("Import", primary: true, onClick: () => DialogResult = true);
        AddButton("Cancel", cancel: true);
    }
}
