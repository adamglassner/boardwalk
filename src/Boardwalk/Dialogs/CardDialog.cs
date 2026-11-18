using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Boardwalk.Services;

namespace Boardwalk.Dialogs;

/// <summary>
/// Everything about one card: title, status, priority, version, repeat, labels, due date, link,
/// notes, checklist, blocked-by, sub-tasks, attachments and history. Changes apply on Save
/// (adding a sub-task or checking GitHub happens straight away).
/// </summary>
public sealed class CardDialog : DialogBase
{
    private readonly Board _board;
    private readonly Card _card;
    private readonly TextBox _title, _due, _link, _notes, _newItem, _version, _blockerBox, _parentBox, _newSub;
    private readonly ComboBox _status, _priority, _repeat;
    private readonly WrapPanel _labelPanel = new();
    private readonly HashSet<string> _labelIds;
    private readonly List<CheckItem> _items;
    private readonly List<string> _blockedBy;
    private readonly List<string> _attachments;
    private readonly StackPanel _itemRows = new(), _subRows = new();
    private readonly WrapPanel _blockerChips = new(), _attachPanel = new();
    private readonly TextBlock _dueHint, _progress, _linkState, _blockerHint, _parentHint;

    public CardDialog(Board board, Card card) : base($"Card #{card.Number}", 600)
    {
        _board = board;
        _card = card;
        _labelIds = card.LabelIds.ToHashSet();
        _items = card.Checklist.Select(i => new CheckItem { Text = i.Text, Done = i.Done }).ToList();
        _blockedBy = card.BlockedBy.ToList();
        _attachments = card.Attachments.ToList();
        const double W = 540;

        var panel = new StackPanel { Width = W };

        _title = new TextBox { Text = card.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_title);

        // Status, priority / version, repeat
        _status = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var col in board.Columns) _status.Items.Add(col.Title);
        _status.SelectedIndex = board.Columns.IndexOf(board.ColumnOf(card));
        _priority = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (Priority p in Enum.GetValues(typeof(Priority))) _priority.Items.Add(p == Priority.None ? "None" : p.ToString());
        _priority.SelectedIndex = (int)card.Priority;
        panel.Children.Add(Pair("Status:", _status, "Priority:", _priority));

        _version = new TextBox { Text = card.Version, ToolTip = "The release this card is part of, like 1.2.0. Used by Write changelog." };
        var versionRow = new DockPanel();
        var versions = BoardStore.Versions(board);
        if (versions.Count > 0)
        {
            var pick = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 30, Height = 30, FontSize = 11, ToolTip = "Pick a version" };
            pick.Click += (_, _) =>
            {
                var m = new ContextMenu();
                foreach (var v in versions) { var vv = v; m.Items.Add(Ui.Item(v, () => _version.Text = vv)); }
                m.Items.Add(Ui.Item("None", () => _version.Text = ""));
                Ui.Open(pick, m);
            };
            DockPanel.SetDock(pick, Dock.Right);
            versionRow.Children.Add(pick);
        }
        versionRow.Children.Add(_version);
        _repeat = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "When a repeating card is completed, the next one is made with the next due date." };
        foreach (RepeatKind r in Enum.GetValues(typeof(RepeatKind))) _repeat.Items.Add(r == RepeatKind.None ? "Doesn't repeat" : r.ToString());
        _repeat.SelectedIndex = (int)card.Repeat;
        panel.Children.Add(Pair("Version:", versionRow, "Repeat:", _repeat));

        // Labels: click to turn on or off
        panel.Children.Add(Header("Labels"));
        if (board.Labels.Count == 0) panel.Children.Add(Hint("This board has no labels yet. Add them in Board options › Board settings."));
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

        // Link (and GitHub state)
        panel.Children.Add(Header("Link"));
        var linkRow = new DockPanel();
        var open = new Button { Content = "Open", MinWidth = 0, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Open the link in your browser" };
        open.Click += (_, _) => OpenUrl(_link.Text);
        DockPanel.SetDock(open, Dock.Right);
        linkRow.Children.Add(open);
        _link = new TextBox { Text = card.Link, ToolTip = "A web address, such as a GitHub issue, pull request or document" };
        _link.TextChanged += (_, _) => UpdateLinkState();
        linkRow.Children.Add(_link);
        panel.Children.Add(linkRow);
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _linkState = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        _linkState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        stateRow.Children.Add(_linkState);
        var check = new Button { Content = "Check GitHub now", MinWidth = 0, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(10, 0, 0, 0), Name = "CheckGitHub" };
        check.Click += async (_, _) => await CheckGitHub(check);
        stateRow.Children.Add(check);
        panel.Children.Add(stateRow);
        UpdateLinkState();

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
        _newItem = EnterBox("Add an item and press Enter", t => { _items.Add(new CheckItem { Text = t }); BuildItems(); }, out var newItemGrid);
        panel.Children.Add(newItemGrid);
        BuildItems();

        // Blocked by
        panel.Children.Add(Header("Blocked by"));
        panel.Children.Add(_blockerChips);
        _blockerBox = EnterBox("Card number, like 12, then Enter", AddBlocker, out var blockerGrid);
        blockerGrid.Width = 260;
        blockerGrid.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(blockerGrid);
        _blockerHint = Hint("This card shows as blocked until those cards are completed.");
        panel.Children.Add(_blockerHint);
        BuildBlockers();

        // Sub-tasks and parent
        panel.Children.Add(Header("Sub-tasks"));
        panel.Children.Add(_subRows);
        _newSub = EnterBox("Add a sub-task and press Enter (it becomes its own card)", AddSubTask, out var subGrid);
        panel.Children.Add(subGrid);
        BuildSubTasks();
        var parentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        parentRow.Children.Add(new TextBlock { Text = "This card is part of #", VerticalAlignment = VerticalAlignment.Center });
        var parent = board.CardById(card.ParentId);
        _parentBox = new TextBox { Width = 70, Text = parent?.Number.ToString() ?? "", Margin = new Thickness(4, 0, 0, 0), ToolTip = "The number of the bigger card this is a sub-task of. Leave empty for none." };
        _parentBox.TextChanged += (_, _) => UpdateParentHint();
        parentRow.Children.Add(_parentBox);
        panel.Children.Add(parentRow);
        _parentHint = Hint("");
        panel.Children.Add(_parentHint);
        UpdateParentHint();

        // Attachments
        panel.Children.Add(Header("Attachments"));
        panel.Children.Add(_attachPanel);
        var attachButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var paste = new Button { Content = Ui.IconText("", "Paste image"), ToolTip = "Paste a screenshot (Ctrl+V)" };
        paste.Click += (_, _) => PasteImage(showErrors: true);
        var addFiles = new Button { Content = Ui.IconText("", "Add files…"), Margin = new Thickness(8, 0, 0, 0) };
        addFiles.Click += (_, _) => AddFiles();
        attachButtons.Children.Add(paste);
        attachButtons.Children.Add(addFiles);
        panel.Children.Add(attachButtons);
        panel.Children.Add(Hint("Press Ctrl+V to paste a screenshot, or drag files onto this window."));
        BuildAttachments();

        // History
        panel.Children.Add(Header("History"));
        var col0 = board.ColumnOf(card);
        if (col0 != null)
        {
            var since = BoardStore.InStatusSince(board, card);
            panel.Children.Add(new TextBlock { Text = $"In {col0.Title} for {BoardStore.Ago(DateTime.Now - since)}", Margin = new Thickness(0, 0, 0, 6) });
        }
        foreach (var h in card.History.AsEnumerable().Reverse().Take(12))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var when = new TextBlock { Text = h.When.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture), Width = 130, FontSize = 12.5 };
            when.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            row.Children.Add(when);
            row.Children.Add(new TextBlock { Text = h.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 });
            panel.Children.Add(row);
        }
        if (card.History.Count > 12) panel.Children.Add(Hint($"…and {card.History.Count - 12} earlier"));

        var footerRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var saveTpl = new Button { Content = "Save as template…", MinWidth = 0, ToolTip = "Make new cards that start like this one" };
        saveTpl.Click += (_, _) => SaveAsTemplate();
        DockPanel.SetDock(saveTpl, Dock.Right);
        footerRow.Children.Add(saveTpl);
        var info = Hint($"#{card.Number} · created {card.Created.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture)}");
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Margin = new Thickness(0);
        footerRow.Children.Add(info);
        panel.Children.Add(footerRow);

        Body.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 8, 0) });

        AddButton("Delete", onClick: Delete).ToolTip = "Delete this card (Ctrl+Z on the board brings it back)";
        AddButton(card.Archived ? "Unarchive" : "Archive", onClick: ToggleArchive).ToolTip = "Archived cards are hidden from the board but kept";
        AddButton("Save", primary: true, onClick: Save);
        AddButton("Cancel", cancel: true);

        AllowDrop = true;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { foreach (var f in files) AttachPath(f); BuildAttachments(); }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage() && !Clipboard.ContainsText())
            {
                e.Handled = true;
                PasteImage(showErrors: true);
            }
        };
        Loaded += (_, _) => { _title.Focus(); _title.CaretIndex = _title.Text.Length; };
    }

    private static Grid Pair(string l1, FrameworkElement c1, string l2, FrameworkElement c2)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Label(l1, 4));
        left.Children.Add(c1);
        grid.Children.Add(left);
        var right = new StackPanel();
        right.Children.Add(Label(l2, 4));
        right.Children.Add(c2);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>A text box with a grey placeholder that runs <paramref name="onEnter"/> when Enter is pressed.</summary>
    private static TextBox EnterBox(string placeholder, Action<string> onEnter, out Grid host)
    {
        var box = new TextBox { Margin = new Thickness(0, 4, 0, 0) };
        var hint = new TextBlock { Text = placeholder, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 4, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            var t = box.Text.Trim();
            if (t.Length == 0) return;
            box.Text = "";
            onEnter(t);
        };
        host = new Grid();
        host.Children.Add(box);
        host.Children.Add(hint);
        return box;
    }

    // ---------------- labels and checklist ----------------

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

    // ---------------- blocked by ----------------

    private void BuildBlockers()
    {
        _blockerChips.Children.Clear();
        foreach (var id in _blockedBy.ToList())
        {
            var c = _board.CardById(id);
            if (c == null) { _blockedBy.Remove(id); continue; }
            bool done = _board.IsComplete(c) || c.Archived;
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 2, 4, 2), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BorderBrushProperty, done ? "Divider" : "DangerText");
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var t = new TextBlock { Text = $"#{c.Number} {c.Title}" + (done ? " (done)" : ""), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis };
            if (done) t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(t);
            var x = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 22, Height = 22, FontSize = 9, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove" };
            var cid = id;
            x.Click += (_, _) => { _blockedBy.Remove(cid); BuildBlockers(); };
            sp.Children.Add(x);
            chip.Child = sp;
            _blockerChips.Children.Add(chip);
        }
    }

    private void AddBlocker(string text)
    {
        if (!int.TryParse(text.TrimStart('#'), out int n) || _board.CardByNumber(n) is not Card c)
        {
            SetHint(_blockerHint, $"There's no card #{text.TrimStart('#')} on this board.", error: true);
            return;
        }
        if (c == _card) { SetHint(_blockerHint, "A card can't block itself.", error: true); return; }
        if (BoardStore.WouldLoop(_board, _card, c)) { SetHint(_blockerHint, $"#{c.Number} is already waiting on this card, so it can't block it too.", error: true); return; }
        if (!_blockedBy.Contains(c.Id)) _blockedBy.Add(c.Id);
        SetHint(_blockerHint, "This card shows as blocked until those cards are completed.", error: false);
        BuildBlockers();
    }

    private static void SetHint(TextBlock hint, string text, bool error)
    {
        hint.Text = text;
        hint.SetResourceReference(TextBlock.ForegroundProperty, error ? "DangerText" : "TextSecondary");
    }

    // ---------------- sub-tasks ----------------

    private void BuildSubTasks()
    {
        _subRows.Children.Clear();
        var kids = _board.Children(_card).ToList();
        foreach (var k in kids)
        {
            var kid = k;
            var col = _board.ColumnOf(k);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var openBtn = new Button { Content = "Open", MinWidth = 0, Padding = new Thickness(10, 2, 10, 2) };
            openBtn.Click += (_, _) => { new CardDialog(_board, kid) { Owner = this }.ShowDialog(); BuildSubTasks(); };
            DockPanel.SetDock(openBtn, Dock.Right);
            row.Children.Add(openBtn);
            row.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0), Background = Ui.Brush(BoardStore.Hex(col?.Color ?? "Gray")), VerticalAlignment = VerticalAlignment.Center, ToolTip = col?.Title });
            var t = new TextBlock { Text = $"#{k.Number} {k.Title}  ·  {col?.Title}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (col?.IsDone == true) t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            row.Children.Add(t);
            _subRows.Children.Add(row);
        }
        if (kids.Count > 0)
        {
            int done = kids.Count(k => _board.IsComplete(k));
            _subRows.Children.Add(Hint($"{done} of {kids.Count} completed"));
        }
    }

    private void AddSubTask(string title)
    {
        BoardStore.AddSubTask(_board, _card, title);
        BuildSubTasks();
    }

    private Card ParsedParent(out string problem)
    {
        problem = null;
        var t = _parentBox.Text.Trim().TrimStart('#');
        if (t.Length == 0) return null;
        if (!int.TryParse(t, out int n) || _board.CardByNumber(n) is not Card p) { problem = $"There's no card #{t} on this board."; return null; }
        if (p == _card) { problem = "A card can't be part of itself."; return null; }
        for (var x = p; x != null; x = _board.CardById(x.ParentId))
            if (x == _card) { problem = $"#{p.Number} is already part of this card."; return null; }
        return p;
    }

    private void UpdateParentHint()
    {
        var p = ParsedParent(out var problem);
        if (problem != null) SetHint(_parentHint, problem, error: true);
        else SetHint(_parentHint, p == null ? "Not part of another card." : $"Part of #{p.Number} {p.Title}", error: false);
    }

    // ---------------- attachments ----------------

    private static readonly string[] ImageTypes = { ".png", ".jpg", ".jpeg", ".gif", ".bmp" };

    private void BuildAttachments()
    {
        _attachPanel.Children.Clear();
        foreach (var name in _attachments.ToList())
        {
            var file = name;
            var path = BoardStore.AttachmentPath(name);
            bool exists = File.Exists(path);
            var tile = new Border { Width = 150, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(4), Cursor = Cursors.Hand, ToolTip = exists ? "Click to open" : "This file is missing" };
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            tile.SetResourceReference(Border.BackgroundProperty, "CardBg");
            var sp = new StackPanel();
            UIElement preview = null;
            if (exists && ImageTypes.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;   // don't keep the file locked
                    bmp.DecodePixelWidth = 280;
                    bmp.UriSource = new Uri(path);
                    bmp.EndInit();
                    bmp.Freeze();
                    preview = new Image { Source = bmp, Height = 84, Stretch = Stretch.Uniform };
                }
                catch { }
            }
            preview ??= new TextBlock { Text = exists ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 32, Height = 84, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 22, 0, 0) };
            sp.Children.Add(preview);
            var bottom = new DockPanel { Margin = new Thickness(2, 4, 0, 0) };
            var x = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 22, Height = 22, FontSize = 9, ToolTip = "Remove" };
            x.Click += (_, e) => { e.Handled = true; _attachments.Remove(file); BuildAttachments(); };
            DockPanel.SetDock(x, Dock.Right);
            bottom.Children.Add(x);
            bottom.Children.Add(new TextBlock { Text = BoardStore.AttachmentName(name), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(bottom);
            tile.Child = sp;
            tile.MouseLeftButtonUp += (_, e) =>
            {
                if (!exists || e.OriginalSource is DependencyObject d && IsInButton(d)) return;
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            };
            _attachPanel.Children.Add(tile);
        }
        if (_attachments.Count == 0)
        {
            var none = new TextBlock { Text = "No attachments.", Margin = new Thickness(0, 0, 0, 4) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _attachPanel.Children.Add(none);
        }
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }

    private void AttachPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var dest = BoardStore.NewAttachment(_card, Path.GetFileName(path), out var fileName);
            File.Copy(path, dest);
            _attachments.Add(fileName);
        }
        catch (Exception ex) { MessageDialog.Show(this, "Attachments", "Couldn't add the file.\n\n" + ex.Message, MessageDialog.Kind.Error); }
    }

    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Add files to the card" };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) AttachPath(f);
        BuildAttachments();
    }

    /// <summary>Saves the image on the clipboard (such as a screenshot) as a PNG attachment.</summary>
    internal bool PasteImage(bool showErrors)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var f in Clipboard.GetFileDropList()) if (f != null) AttachPath(f);
                BuildAttachments();
                return true;
            }
            if (!Clipboard.ContainsImage())
            {
                if (showErrors) MessageDialog.Show(this, "Paste image", "There's no image on the clipboard. Take a screenshot (Windows+Shift+S) and try again.");
                return false;
            }
            var img = Clipboard.GetImage();
            if (img == null) return false;
            AttachBitmap(img, $"Screenshot {DateTime.Now:yyyy-MM-dd h.mm.ss tt}.png");
            BuildAttachments();
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors) MessageDialog.Show(this, "Paste image", "Couldn't paste the image.\n\n" + ex.Message, MessageDialog.Kind.Error);
            return false;
        }
    }

    internal void AttachBitmap(BitmapSource img, string name)
    {
        // Clipboard images often carry a broken alpha channel; drop it so the picture isn't transparent.
        var opaque = new FormatConvertedBitmap(img, PixelFormats.Bgr32, null, 0);
        var path = BoardStore.NewAttachment(_card, name, out var fileName);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(opaque));
        using (var fs = File.Create(path)) enc.Save(fs);
        _attachments.Add(fileName);
    }

    internal int AttachmentCount => _attachments.Count;

    // ---------------- link ----------------

    private void UpdateLinkState()
    {
        var gh = GitHubService.ParseLink(_link.Text);
        var btn = (_linkState.Parent as StackPanel)?.Children.OfType<Button>().FirstOrDefault();
        if (gh == null)
        {
            _linkState.Text = "";
            if (btn != null) btn.Visibility = Visibility.Collapsed;
            return;
        }
        if (btn != null) btn.Visibility = Visibility.Visible;
        var state = _link.Text.Trim() == _card.Link ? _card.LinkState : "";
        var what = gh.IsPull ? "Pull request" : "Issue";
        _linkState.Text = state.Length == 0
            ? $"GitHub {what.ToLowerInvariant()} #{gh.Number} in {gh.Owner}/{gh.Repo}"
            : $"{what} #{gh.Number} is {state}" + (_card.LinkChecked is DateTime t ? $" (checked {t.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)})" : "");
    }

    private async Task CheckGitHub(Button btn)
    {
        var gh = GitHubService.ParseLink(_link.Text);
        if (gh == null) return;
        btn.IsEnabled = false;
        try
        {
            var state = await GitHubService.GetState(gh);
            if (_link.Text.Trim() == _card.Link)
            {
                _card.LinkState = state;
                _card.LinkChecked = DateTime.Now;
                BoardStore.SaveQuietly();
                UpdateLinkState();
            }
            else _linkState.Text = $"{(gh.IsPull ? "Pull request" : "Issue")} #{gh.Number} is {state}";
        }
        catch (Exception ex) { _linkState.Text = ex.Message; }
        finally { btn.IsEnabled = true; }
    }

    internal static void OpenUrl(string text)
    {
        var url = (text ?? "").Trim();
        if (url.Length == 0) return;
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    // ---------------- due date ----------------

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
            SetHint(_dueHint, "That doesn't look like a date. Try 11/20/2026, Nov 20, tomorrow or friday.", error: true);
            return;
        }
        SetHint(_dueHint, due is DateTime d ? d.ToString("D") : "No due date. Type a date, or a word like tomorrow or friday.", error: false);
    }

    // ---------------- save ----------------

    private void SaveAsTemplate()
    {
        var d = new TextPromptDialog("Save as template", "Template name:", _title.Text.Trim(), "New cards made from it start with this card's title, notes, labels, priority and checklist.") { Owner = this };
        if (d.ShowDialog() != true || d.Value.Length == 0) return;
        _board.Templates.Add(new CardTemplate
        {
            Name = d.Value, Title = _title.Text.Trim(), Notes = _notes.Text.TrimEnd(),
            Priority = (Priority)Math.Max(0, _priority.SelectedIndex),
            LabelIds = _board.Labels.Where(l => _labelIds.Contains(l.Id)).Select(l => l.Id).ToList(),
            Checklist = _items.Select(i => i.Text).ToList(),
        });
        BoardStore.Touch(_board);
        MessageDialog.Show(this, "Template saved", $"\"{d.Value}\" is in the template button next to Add a card.");
    }

    internal bool Save(bool close)
    {
        var title = _title.Text.Trim();
        if (title.Length == 0) { _title.Focus(); return false; }
        if (!TryParseDue(_due.Text, out var due)) { _due.Focus(); return false; }
        var parent = ParsedParent(out var problem);
        if (problem != null) { _parentBox.Focus(); return false; }
        var repeat = (RepeatKind)Math.Max(0, _repeat.SelectedIndex);
        if (repeat != RepeatKind.None && due == null) due = DateTime.Today;
        var link = _link.Text.Trim();
        if (link != _card.Link) { _card.LinkState = ""; _card.LinkChecked = null; }
        _card.Title = title;
        _card.Notes = _notes.Text.TrimEnd();
        _card.Link = link;
        _card.Due = due;
        _card.Repeat = repeat;
        _card.Version = _version.Text.Trim();
        _card.Priority = (Priority)Math.Max(0, _priority.SelectedIndex);
        _card.LabelIds = _board.Labels.Where(l => _labelIds.Contains(l.Id)).Select(l => l.Id).ToList();
        _card.Checklist = _items;
        _card.BlockedBy = _blockedBy.Where(id => _board.CardById(id) != null).ToList();
        _card.ParentId = parent?.Id ?? "";
        _card.Attachments = _attachments;
        _card.Modified = DateTime.Now;
        var target = _status.SelectedIndex >= 0 && _status.SelectedIndex < _board.Columns.Count ? _board.Columns[_status.SelectedIndex] : null;
        if (target != null && target != _board.ColumnOf(_card)) BoardStore.MoveCard(_board, _card, target, -1);
        else BoardStore.Touch(_board);
        if (close) DialogResult = true;
        return true;
    }

    private void Save() => Save(close: true);

    private void ToggleArchive()
    {
        if (!Save(close: false)) return;
        _card.Archived = !_card.Archived;
        BoardStore.Log(_card, _card.Archived ? "Archived" : "Unarchived");
        BoardStore.Touch(_board);
        DialogResult = true;
    }

    private void Delete()
    {
        if (SettingsService.Current.ConfirmDeletes && !MessageDialog.Ask(this, "Delete card", $"Delete \"{_card.Title}\"? You can undo this on the board with Ctrl+Z.")) return;
        BoardStore.DeleteCard(_board, _card);
        DialogResult = true;
    }
}
