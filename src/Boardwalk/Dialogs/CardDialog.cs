using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Boardwalk.Services;

namespace Boardwalk.Dialogs;

/// <summary>Everything about one card: title, status, labels, priority, due date, link, notes and checklist.</summary>
public sealed class CardDialog : DialogBase
{
    private readonly Board _board;
    private readonly Card _card;
    private readonly TextBox _title, _due, _link, _notes, _newItem;
    private readonly ComboBox _status, _priority;
    private readonly WrapPanel _labelPanel = new();
    private readonly HashSet<string> _labelIds;
    private readonly List<CheckItem> _items;
    private readonly StackPanel _itemRows = new();
    private readonly TextBlock _dueHint, _progress;

    public CardDialog(Board board, Card card) : base($"Card #{card.Number}", 580)
    {
        _board = board;
        _card = card;
        _labelIds = card.LabelIds.ToHashSet();
        _items = card.Checklist.Select(i => new CheckItem { Text = i.Text, Done = i.Done }).ToList();
        const double W = 520;

        var panel = new StackPanel { Width = W };

        _title = new TextBox { Text = card.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_title);

        // Status (column) and priority side by side
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Label("Status:", 4));
        _status = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var col in board.Columns) _status.Items.Add(col.Title);
        _status.SelectedIndex = board.Columns.IndexOf(board.ColumnOf(card));
        left.Children.Add(_status);
        grid.Children.Add(left);
        var right = new StackPanel();
        right.Children.Add(Label("Priority:", 4));
        _priority = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (Priority p in Enum.GetValues(typeof(Priority))) _priority.Items.Add(p == Priority.None ? "None" : p.ToString());
        _priority.SelectedIndex = (int)card.Priority;
        right.Children.Add(_priority);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        panel.Children.Add(grid);

        // Labels: click to turn on or off
        panel.Children.Add(Header("Labels"));
        if (board.Labels.Count == 0) panel.Children.Add(Hint("This board has no labels yet. Add them in Board options › Board settings and labels."));
        panel.Children.Add(_labelPanel);
        BuildLabels();

        // Due date
        panel.Children.Add(Header("Due date"));
        var dueRow = new StackPanel { Orientation = Orientation.Horizontal };
        _due = new TextBox { Width = 170, Text = card.Due?.ToString("d") ?? "" };
        _due.TextChanged += (_, _) => UpdateDueHint();
        dueRow.Children.Add(_due);
        foreach (var (text, days) in new[] { ("Today", 0), ("Tomorrow", 1), ("Next week", 7) })
        {
            var d = days;
            var b = new Button { Content = text, MinWidth = 0, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (_, _) => _due.Text = DateTime.Today.AddDays(d).ToString("d");
            dueRow.Children.Add(b);
        }
        var clear = new Button { Content = "Clear", MinWidth = 0, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => _due.Text = "";
        dueRow.Children.Add(clear);
        panel.Children.Add(dueRow);
        _dueHint = Hint("");
        panel.Children.Add(_dueHint);
        UpdateDueHint();

        // Link
        panel.Children.Add(Header("Link"));
        var linkRow = new DockPanel();
        var open = new Button { Content = "Open", MinWidth = 0, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Open the link in your browser" };
        open.Click += (_, _) => OpenLink();
        DockPanel.SetDock(open, Dock.Right);
        linkRow.Children.Add(open);
        _link = new TextBox { Text = card.Link, ToolTip = "A web address, such as an issue, pull request or document" };
        linkRow.Children.Add(_link);
        panel.Children.Add(linkRow);

        // Notes
        panel.Children.Add(Header("Notes"));
        _notes = new TextBox
        {
            Text = card.Notes, Height = 120, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        panel.Children.Add(_notes);

        // Checklist
        var checkHeader = new DockPanel();
        _progress = new TextBlock { Style = (Style)FindResource("Hint"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_progress, Dock.Right);
        checkHeader.Children.Add(_progress);
        checkHeader.Children.Add(Header("Checklist"));
        panel.Children.Add(checkHeader);
        panel.Children.Add(_itemRows);
        _newItem = new TextBox { ToolTip = "Type an item and press Enter", Margin = new Thickness(0, 4, 0, 0) };
        var newHint = new TextBlock { Text = "Add an item and press Enter", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 4, 0, 0) };
        newHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _newItem.TextChanged += (_, _) => newHint.Visibility = _newItem.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _newItem.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var t = _newItem.Text.Trim();
            if (t.Length == 0) return;
            _items.Add(new CheckItem { Text = t });
            _newItem.Text = "";
            BuildItems();
        };
        var newGrid = new Grid();
        newGrid.Children.Add(_newItem);
        newGrid.Children.Add(newHint);
        panel.Children.Add(newGrid);
        BuildItems();

        var info = Hint($"#{card.Number} · created {card.Created:g} · changed {card.Modified:g}");
        info.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(info);

        Body.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });

        AddButton("Delete", onClick: Delete).ToolTip = "Delete this card";
        AddButton(card.Archived ? "Unarchive" : "Archive", onClick: ToggleArchive).ToolTip = "Archived cards are hidden from the board but kept";
        AddButton("Save", primary: true, onClick: Save);
        AddButton("Cancel", cancel: true);
        Loaded += (_, _) => { _title.Focus(); _title.CaretIndex = _title.Text.Length; };
    }

    private void BuildLabels()
    {
        _labelPanel.Children.Clear();
        foreach (var l in _board.Labels)
        {
            var label = l;
            var pill = Ui.LabelPill(l, _labelIds.Contains(l.Id));
            pill.Cursor = Cursors.Hand;
            pill.Padding = new Thickness(10, 3, 10, 4);
            pill.Margin = new Thickness(0, 0, 6, 6);
            pill.ToolTip = _labelIds.Contains(l.Id) ? "Click to remove" : "Click to add";
            pill.MouseLeftButtonUp += (_, _) =>
            {
                if (!_labelIds.Remove(label.Id)) _labelIds.Add(label.Id);
                BuildLabels();
            };
            _labelPanel.Children.Add(pill);
        }
    }

    private void BuildItems()
    {
        _itemRows.Children.Clear();
        foreach (var item in _items)
        {
            var it = item;
            var row = new DockPanel();
            var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 28, Height = 28, FontSize = 11, ToolTip = "Remove item" };
            del.Click += (_, _) => { _items.Remove(it); BuildItems(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var cb = new CheckBox { Content = new TextBlock { Text = it.Text, TextWrapping = TextWrapping.Wrap }, IsChecked = it.Done, VerticalAlignment = VerticalAlignment.Center };
            cb.Checked += (_, _) => { it.Done = true; UpdateProgress(); };
            cb.Unchecked += (_, _) => { it.Done = false; UpdateProgress(); };
            row.Children.Add(cb);
            _itemRows.Children.Add(row);
        }
        UpdateProgress();
    }

    private void UpdateProgress() =>
        _progress.Text = _items.Count == 0 ? "" : $"{_items.Count(i => i.Done)} of {_items.Count} done";

    /// <summary>Accepts dates like 11/20/2026, 2026-11-20, Nov 20, today, tomorrow, next week or a weekday name.</summary>
    internal static bool TryParseDue(string text, out DateTime? due)
    {
        due = null;
        text = (text ?? "").Trim();
        if (text.Length == 0) return true;
        var t = text.ToLowerInvariant();
        if (t == "today") { due = DateTime.Today; return true; }
        if (t == "tomorrow") { due = DateTime.Today.AddDays(1); return true; }
        if (t == "next week") { due = DateTime.Today.AddDays(7); return true; }
        foreach (DayOfWeek d in Enum.GetValues(typeof(DayOfWeek)))
        {
            var name = d.ToString().ToLowerInvariant();
            if (t == name || t == name[..3])
            {
                int add = ((int)d - (int)DateTime.Today.DayOfWeek + 7) % 7;
                due = DateTime.Today.AddDays(add == 0 ? 7 : add);
                return true;
            }
        }
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var dt) ||
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
        {
            due = dt.Date;
            return true;
        }
        return false;
    }

    private void UpdateDueHint()
    {
        if (!TryParseDue(_due.Text, out var due))
        {
            _dueHint.Text = "That doesn't look like a date. Try 11/20/2026, Nov 20, tomorrow or friday.";
            _dueHint.SetResourceReference(TextBlock.ForegroundProperty, "DangerText");
            return;
        }
        _dueHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        _dueHint.Text = due is DateTime d ? d.ToString("D") : "No due date. Type a date, or a word like tomorrow or friday.";
    }

    private void OpenLink()
    {
        var url = _link.Text.Trim();
        if (url.Length == 0) return;
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    private void Save()
    {
        var title = _title.Text.Trim();
        if (title.Length == 0) { _title.Focus(); return; }
        if (!TryParseDue(_due.Text, out var due)) { _due.Focus(); return; }
        _card.Title = title;
        _card.Notes = _notes.Text.TrimEnd();
        _card.Link = _link.Text.Trim();
        _card.Due = due;
        _card.Priority = (Priority)Math.Max(0, _priority.SelectedIndex);
        _card.LabelIds = _board.Labels.Where(l => _labelIds.Contains(l.Id)).Select(l => l.Id).ToList();
        _card.Checklist = _items;
        _card.Modified = DateTime.Now;
        var target = _status.SelectedIndex >= 0 && _status.SelectedIndex < _board.Columns.Count ? _board.Columns[_status.SelectedIndex] : null;
        if (target != null && target != _board.ColumnOf(_card)) BoardStore.MoveCard(_board, _card, target, -1);
        else BoardStore.Touch(_board);
        DialogResult = true;
    }

    private void ToggleArchive()
    {
        Save();
        if (DialogResult != true) return;
        _card.Archived = !_card.Archived;
        BoardStore.Touch(_board);
    }

    private void Delete()
    {
        if (SettingsService.Current.ConfirmDeletes && !MessageDialog.Ask(this, "Delete card", $"Delete \"{_card.Title}\"? This can't be undone.")) return;
        BoardStore.DeleteCard(_board, _card);
        DialogResult = true;
    }
}
