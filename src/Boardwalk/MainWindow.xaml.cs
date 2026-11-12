using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Boardwalk.Dialogs;
using Boardwalk.Services;

namespace Boardwalk;

public partial class MainWindow : Window
{
    internal const string DragFormat = "BoardwalkCard";

    internal Board CurrentBoard { get; private set; }

    /// <summary>Column id → the panel that shows its cards (also used by the self-test).</summary>
    internal readonly Dictionary<string, StackPanel> CardPanels = new();

    private bool _loadingList, _loadingFilter;
    private string _search = "";
    private string _labelFilter;          // label name, or null for all
    private string _addingInColumnId;     // column whose "Add a card" box is open
    private Point _dragStart;
    private Card _pressedCard;
    private bool _dragging;
    private readonly Border _dropMarker;
    private StackPanel _markerPanel;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _dropMarker = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 0, 2, 8), IsHitTestVisible = false };
        _dropMarker.SetResourceReference(Border.BackgroundProperty, "Accent");
        RestoreWindow();
        Loaded += OnLoaded;
        Closing += (_, _) => SaveWindow();
        PreviewKeyDown += OnPreviewKeyDown;
        ThemeManager.ThemeChanged += () => { UpdateThemeToggle(); RenderBoard(); };
        UpdateThemeToggle();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var last = BoardStore.Boards.FirstOrDefault(b => b.Id == SettingsService.Current.LastBoardId);
        RefreshBoardList(last);
        if (App.IsSelfTest)
        {
            Dispatcher.BeginInvoke(new Action(async () => await RunSelfTest()), DispatcherPriority.ApplicationIdle);
            return;
        }
        if (BoardStore.LoadWarning != null)
            MessageDialog.Show(this, "Boards restored", BoardStore.LoadWarning, MessageDialog.Kind.Warning);
    }

    // =====================================================================
    //  Sidebar: boards
    // =====================================================================

    internal void RefreshBoardList(Board select = null)
    {
        select ??= CurrentBoard;
        if (select != null && !BoardStore.Boards.Contains(select)) select = null;
        select ??= BoardStore.Boards.FirstOrDefault();
        _loadingList = true;
        BoardList.Items.Clear();
        foreach (var b in BoardStore.Boards)
        {
            var item = new ListBoxItem { Tag = b, Content = BoardRow(b), ContextMenu = BoardMenu(b, forSidebar: true) };
            BoardList.Items.Add(item);
            if (b == select) item.IsSelected = true;
        }
        _loadingList = false;
        ShowBoard(select);
    }

    private static UIElement BoardRow(Board b)
    {
        var dp = new DockPanel();
        var count = new TextBlock { Text = b.ActiveCardCount.ToString(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        count.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        DockPanel.SetDock(count, Dock.Right);
        dp.Children.Add(count);
        dp.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 10, 0),
            Background = Ui.Brush(BoardStore.Hex(b.Color)), VerticalAlignment = VerticalAlignment.Center,
        });
        dp.Children.Add(new TextBlock { Text = b.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return dp;
    }

    private void UpdateBoardRowCounts()
    {
        foreach (var o in BoardList.Items)
            if (o is ListBoxItem { Tag: Board b } item) item.Content = BoardRow(b);
    }

    private void BoardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingList) return;
        if (BoardList.SelectedItem is ListBoxItem { Tag: Board b } && b != CurrentBoard) ShowBoard(b);
    }

    internal void ShowBoard(Board b)
    {
        CurrentBoard = b;
        _addingInColumnId = null;
        _labelFilter = null;
        if (SettingsService.Current.LastBoardId != (b?.Id ?? ""))
        {
            SettingsService.Current.LastBoardId = b?.Id ?? "";
            SettingsService.Save();
        }
        RebuildLabelFilter();
        RenderBoard();
        BoardScroll.ScrollToLeftEnd();
    }

    private void SelectInList(Board b)
    {
        _loadingList = true;
        foreach (var o in BoardList.Items)
            if (o is ListBoxItem item) item.IsSelected = item.Tag == b;
        _loadingList = false;
    }

    // =====================================================================
    //  The board
    // =====================================================================

    internal void RenderBoard()
    {
        if (ColumnsPanel == null) return;
        var b = CurrentBoard;
        bool has = b != null;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        BoardHeader.Visibility = BoardScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ColumnsPanel.Children.Clear();
        CardPanels.Clear();
        _markerPanel = null;
        if (!has) { Title = "Boardwalk"; UpdateBoardRowCounts(); return; }

        Title = $"{b.Name} - Boardwalk";
        BoardTitle.Text = b.Name;
        BoardDot.Background = Ui.Brush(BoardStore.Hex(b.Color));
        int cards = b.ActiveCardCount;
        var sub = $"{cards} card{(cards == 1 ? "" : "s")} · {b.Columns.Count} column{(b.Columns.Count == 1 ? "" : "s")}";
        if (!string.IsNullOrWhiteSpace(b.Description))
            sub = b.Description.Trim().Replace("\r", " ").Replace("\n", " ") + "   ·   " + sub;
        BoardSubtitle.Text = sub;

        foreach (var col in b.Columns) ColumnsPanel.Children.Add(BuildColumn(b, col));
        ColumnsPanel.Children.Add(BuildAddColumnTile(b));
        UpdateBoardRowCounts();
    }

    private Border BuildColumn(Board b, Column col)
    {
        var root = new Border
        {
            Width = SettingsService.Current.ColumnWidth, Margin = new Thickness(0, 0, 12, 0), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 10, 8, 8), AllowDrop = true, Tag = col,
        };
        root.SetResourceReference(Border.BackgroundProperty, "ColumnBg");
        root.SetResourceReference(Border.BorderBrushProperty, "ColumnBorder");
        var dock = new DockPanel();
        root.Child = dock;

        // ---- header: status colour, name, count, menu ----
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        DockPanel.SetDock(header, Dock.Top);
        var menu = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 28, Height = 28, ToolTip = "Column options" };
        menu.Click += (_, _) => Ui.Open(menu, ColumnMenu(b, col));
        DockPanel.SetDock(menu, Dock.Right);
        header.Children.Add(menu);

        int n = col.ActiveCount;
        var countText = new TextBlock { Text = col.WipLimit > 0 ? $"{n}/{col.WipLimit}" : n.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold };
        var count = new Border
        {
            CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center, Child = countText,
        };
        if (col.OverLimit)
        {
            count.SetResourceReference(Border.BackgroundProperty, "WarnBg");
            countText.SetResourceReference(TextBlock.ForegroundProperty, "WarnText");
            count.ToolTip = $"More than the limit of {col.WipLimit} card{(col.WipLimit == 1 ? "" : "s")}";
        }
        else
        {
            count.SetResourceReference(Border.BackgroundProperty, "ControlHoverBg");
            countText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            if (col.WipLimit > 0) count.ToolTip = $"Limit: {col.WipLimit} card{(col.WipLimit == 1 ? "" : "s")}";
        }
        DockPanel.SetDock(count, Dock.Right);
        header.Children.Add(count);

        header.Children.Add(new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0),
            Background = Ui.Brush(BoardStore.Hex(col.Color)), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = col.IsDone ? "Status colour · counts as complete" : "Status colour",
        });
        var title = new TextBlock
        {
            Text = col.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Double-click to rename",
        };
        title.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { e.Handled = true; RenameColumn(b, col); } };
        header.Children.Add(title);
        dock.Children.Add(header);

        // ---- footer: add a card ----
        var footer = BuildAddCard(b, col);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        // ---- the cards ----
        var panel = new StackPanel { Background = Brushes.Transparent, MinHeight = 40, Tag = col };
        CardPanels[col.Id] = panel;
        foreach (var card in col.Cards)
        {
            if (card.Archived || !Matches(b, card)) continue;
            panel.Children.Add(BuildCard(b, col, card));
        }
        if (panel.Children.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = Filtering ? "No matching cards" : "No cards yet. Drag one here or add one below.",
                Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, Margin = new Thickness(8, 14, 8, 14),
            };
            panel.Children.Add(empty);
        }
        var scroll = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Padding = new Thickness(0, 0, 2, 0),
        };
        dock.Children.Add(scroll);
        if (_addingInColumnId == col.Id) scroll.Loaded += (_, _) => scroll.ScrollToBottom();

        root.DragOver += (_, e) => OnColumnDragOver(panel, e);
        root.Drop += (_, e) => OnColumnDrop(b, col, panel, e);
        return root;
    }

    private FrameworkElement BuildAddCard(Board b, Column col)
    {
        var host = new ContentControl { Margin = new Thickness(0, 4, 2, 0), Focusable = false };

        void ShowButton()
        {
            var btn = new Button
            {
                Style = (Style)FindResource("FlatBase"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6),
                Content = Ui.IconText("", "Add a card"), ToolTip = "Add a card to " + col.Title,
            };
            btn.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
            btn.Click += (_, _) => { _addingInColumnId = col.Id; ShowEditor(); };
            host.Content = btn;
        }

        void ShowEditor()
        {
            var box = new TextBox
            {
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 56,
                VerticalContentAlignment = VerticalAlignment.Top, ToolTip = "Type a title and press Enter. Esc to stop adding.",
            };
            var add = new Button { Content = "Add card", Style = (Style)FindResource("AccentButton") };
            var cancel = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "Stop adding (Esc)", Margin = new Thickness(4, 0, 0, 0) };
            void Commit()
            {
                var text = box.Text.Trim();
                if (text.Length == 0) return;
                BoardStore.AddCard(b, col, text);
                RenderBoard();   // keeps the box open (_addingInColumnId) for the next card
            }
            void Stop() { _addingInColumnId = null; ShowButton(); }
            add.Click += (_, _) => Commit();
            cancel.Click += (_, _) => Stop();
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Commit(); }
                else if (e.Key == Key.Escape) { e.Handled = true; Stop(); }
            };
            box.Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(add);
            buttons.Children.Add(cancel);
            var sp = new StackPanel();
            sp.Children.Add(box);
            sp.Children.Add(buttons);
            host.Content = sp;
        }

        if (_addingInColumnId == col.Id) ShowEditor(); else ShowButton();
        return host;
    }

    private Border BuildAddColumnTile(Board b)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("FlatBase"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 10, 12, 10),
            Content = Ui.IconText("", "Add a column"),
        };
        btn.Click += (_, _) => Ui.Open(btn, AddColumnMenu(b));
        var tile = new Border
        {
            Width = 240, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1), Child = btn, Margin = new Thickness(0, 0, 12, 0),
        };
        tile.SetResourceReference(Border.BorderBrushProperty, "ColumnBorder");
        return tile;
    }

    private Border BuildCard(Board b, Column col, Card card)
    {
        var s = SettingsService.Current;
        var root = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Tag = card, Focusable = true,
            Padding = s.CompactCards ? new Thickness(10, 6, 10, 6) : new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8),
            FocusVisualStyle = (Style)FindResource("FocusRingVisual"),
        };
        root.SetResourceReference(Border.BackgroundProperty, "CardBg");
        root.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        root.MouseEnter += (_, _) => root.SetResourceReference(Border.BorderBrushProperty, "CardHoverBorder");
        root.MouseLeave += (_, _) => root.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        var sp = new StackPanel();
        root.Child = sp;

        // Labels
        var labels = card.LabelIds.Select(b.Label).Where(l => l != null).ToList();
        if (labels.Count > 0)
        {
            var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, s.CompactCards ? 4 : 6) };
            foreach (var l in labels)
            {
                if (s.CompactCards)
                    wp.Children.Add(new Border { Width = 30, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 0), Background = Ui.Brush(BoardStore.Hex(l.Color)), ToolTip = l.Name });
                else
                    wp.Children.Add(Ui.LabelPill(l));
            }
            sp.Children.Add(wp);
        }

        // Title (with a tick in a "complete" status)
        var titleRow = new DockPanel();
        if (col.IsDone)
        {
            var tick = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 13, Margin = new Thickness(0, 2, 6, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = Ui.Brush(BoardStore.Hex("Green")) };
            titleRow.Children.Add(tick);
        }
        var title = new TextBlock { Text = card.Title, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        if (col.IsDone) title.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        titleRow.Children.Add(title);
        sp.Children.Add(titleRow);

        if (!s.CompactCards && s.ShowNotesPreview && !string.IsNullOrWhiteSpace(card.Notes))
        {
            var notes = new TextBlock
            {
                Text = card.Notes.Trim(), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 36, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0),
            };
            notes.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(notes);
        }

        // Number, priority, due date, checklist, link
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        meta.Children.Add(Ui.Meta(null, "#" + card.Number, "TextSecondary"));
        if (card.Priority != Priority.None) meta.Children.Add(Ui.PriorityBadge(card.Priority));
        if (card.Due is DateTime due)
        {
            int days = (due.Date - DateTime.Today).Days;
            string key = col.IsDone ? "TextSecondary" : days < 0 ? "DangerText" : days <= 1 ? "WarnText" : "TextSecondary";
            var m = Ui.Meta("", Ui.FriendlyDate(due), key);
            m.ToolTip = (days < 0 && !col.IsDone ? "Overdue · was due " : "Due ") + due.ToString("D");
            meta.Children.Add(m);
        }
        if (card.Checklist.Count > 0)
        {
            int done = card.Checklist.Count(i => i.Done);
            var m = Ui.Meta("", $"{done}/{card.Checklist.Count}", done == card.Checklist.Count ? "Accent" : "TextSecondary");
            m.ToolTip = "Checklist";
            meta.Children.Add(m);
        }
        if (!string.IsNullOrWhiteSpace(card.Link))
        {
            var m = Ui.Meta("", "", "TextSecondary");
            m.ToolTip = card.Link;
            meta.Children.Add(m);
        }
        if (!string.IsNullOrWhiteSpace(card.Notes) && (s.CompactCards || !s.ShowNotesPreview))
        {
            var m = Ui.Meta("", "", "TextSecondary");
            m.ToolTip = "Has notes";
            meta.Children.Add(m);
        }
        if (!s.CompactCards || meta.Children.Count > 1) sp.Children.Add(meta);

        // Click to open, drag to move, right-click for more
        root.PreviewMouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _pressedCard = card; };
        root.MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressedCard != card || _dragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            root.Opacity = 0.45;
            try { DragDrop.DoDragDrop(root, new DataObject(DragFormat, card.Id), DragDropEffects.Move); }
            finally
            {
                root.Opacity = 1;
                RemoveDropMarker();
                _dragging = false;
                _pressedCard = null;
            }
        };
        root.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressedCard != card || _dragging) return;
            _pressedCard = null;
            e.Handled = true;
            OpenCard(b, card);
        };
        root.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OpenCard(b, card); } };
        root.ContextMenu = CardMenu(b, card);
        return root;
    }

    private bool Filtering => _search.Length > 0 || _labelFilter != null;

    internal bool Matches(Board b, Card card)
    {
        if (_labelFilter != null && !card.LabelIds.Select(b.Label).Any(l => l != null && l.Name == _labelFilter)) return false;
        if (_search.Length == 0) return true;
        var q = _search;
        if (q.StartsWith('#') && int.TryParse(q[1..], out int num)) return card.Number == num;
        return card.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || card.Notes.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || card.Link.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || card.Checklist.Any(i => i.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            || card.LabelIds.Select(b.Label).Any(l => l != null && l.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    // =====================================================================
    //  Drag and drop
    // =====================================================================

    private void OnColumnDragOver(StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        var y = e.GetPosition(panel).Y;
        int visual = 0, i = 0;
        bool found = false;
        foreach (UIElement child in panel.Children)
        {
            if (child == _dropMarker) continue;
            if (child is FrameworkElement { Tag: Card } fe && !found)
            {
                var mid = fe.TranslatePoint(new Point(0, 0), panel).Y + fe.ActualHeight / 2;
                if (y < mid) { visual = i; found = true; }
            }
            i++;
        }
        if (!found) visual = i;
        if (_markerPanel != null && _markerPanel != panel) _markerPanel.Children.Remove(_dropMarker);
        panel.Children.Remove(_dropMarker);
        panel.Children.Insert(Math.Min(visual, panel.Children.Count), _dropMarker);
        _markerPanel = panel;
    }

    private void RemoveDropMarker()
    {
        _markerPanel?.Children.Remove(_dropMarker);
        _markerPanel = null;
    }

    private void OnColumnDrop(Board b, Column col, StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is not string id) return;
        var card = b.AllCards.FirstOrDefault(c => c.Id == id);
        if (card == null) return;
        var y = e.GetPosition(panel).Y;
        int index = col.Cards.Count;
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement { Tag: Card other } fe && fe != _dropMarker)
            {
                var top = fe.TranslatePoint(new Point(0, 0), panel).Y;
                // The marker sits above the card it was shown before, so measure without it.
                if (_markerPanel == panel && panel.Children.IndexOf(_dropMarker) < panel.Children.IndexOf(fe)) top -= _dropMarker.ActualHeight + _dropMarker.Margin.Bottom;
                if (y < top + fe.ActualHeight / 2) { index = col.Cards.IndexOf(other); break; }
            }
        }
        RemoveDropMarker();
        MoveCard(b, card, col, index);
    }

    /// <summary>Moves a card and redraws (used by drag and drop, menus and the self-test).</summary>
    internal void MoveCard(Board b, Card card, Column target, int index)
    {
        BoardStore.MoveCard(b, card, target, index);
        RenderBoard();
    }

    // =====================================================================
    //  Menus
    // =====================================================================

    private ContextMenu BoardMenu(Board b, bool forSidebar = false)
    {
        var m = new ContextMenu();
        if (forSidebar) m.Items.Add(Ui.Item("Open", () => { SelectInList(b); ShowBoard(b); }, ""));
        m.Items.Add(Ui.Item("Rename…", () => RenameBoard(b), ""));
        m.Items.Add(Ui.Item("Board settings and labels…", () => EditBoard(b), ""));
        if (!forSidebar)
        {
            m.Items.Add(Ui.Item("Archived cards…", () => ShowArchived(b), ""));
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Item("Copy as Markdown", () => { Clipboard.SetText(BoardStore.ToMarkdown(b)); }, ""));
            m.Items.Add(Ui.Item("Export as Markdown…", () => ExportMarkdown(b), ""));
            m.Items.Add(Ui.Item("Export board…", () => ExportBoard(b), ""));
        }
        m.Items.Add(new Separator());
        var dup = Ui.Item("Duplicate", null, "");
        dup.Items.Add(Ui.Item("With its cards", () => RefreshBoardList(BoardStore.DuplicateBoard(b, true))));
        dup.Items.Add(Ui.Item("Columns and labels only", () => RefreshBoardList(BoardStore.DuplicateBoard(b, false))));
        m.Items.Add(dup);
        int i = BoardStore.Boards.IndexOf(b);
        m.Items.Add(Ui.Item("Move up in the list", () => { BoardStore.MoveBoard(b, -1); RefreshBoardList(CurrentBoard); }, "", i > 0));
        m.Items.Add(Ui.Item("Move down in the list", () => { BoardStore.MoveBoard(b, 1); RefreshBoardList(CurrentBoard); }, "", i < BoardStore.Boards.Count - 1));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Delete board…", () => DeleteBoard(b), ""));
        return m;
    }

    private ContextMenu ColumnMenu(Board b, Column col)
    {
        var m = new ContextMenu();
        m.Items.Add(Ui.Item("Add a card", () => { _addingInColumnId = col.Id; RenderBoard(); }, ""));
        m.Items.Add(Ui.Item("Rename…", () => RenameColumn(b, col), ""));
        var color = Ui.Item("Status colour", null, "");
        foreach (var (name, hex) in BoardStore.Palette)
        {
            var it = Ui.Item(name, () => { col.Color = name; BoardStore.Touch(b); RenderBoard(); });
            it.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Ui.Brush(hex) };
            it.IsChecked = col.Color == name;
            color.Items.Add(it);
        }
        m.Items.Add(color);
        var done = Ui.Item("Counts as complete", () => { col.IsDone = !col.IsDone; BoardStore.Touch(b); RenderBoard(); });
        done.IsCheckable = false;
        done.IsChecked = col.IsDone;
        done.ToolTip = "Cards here get a tick and are never shown as overdue (for statuses like Complete or Cancelled).";
        m.Items.Add(done);
        m.Items.Add(Ui.Item(col.WipLimit > 0 ? $"Card limit ({col.WipLimit})…" : "Card limit…", () => SetLimit(b, col), ""));
        var sort = Ui.Item("Sort cards", null, "");
        sort.Items.Add(Ui.Item("By due date", () => SortColumn(b, col, c => c.Due ?? DateTime.MaxValue)));
        sort.Items.Add(Ui.Item("By priority", () => SortColumn(b, col, c => -(int)c.Priority)));
        sort.Items.Add(Ui.Item("By number", () => SortColumn(b, col, c => c.Number)));
        sort.Items.Add(Ui.Item("By title", () => SortColumn(b, col, c => c.Title.ToLowerInvariant())));
        m.Items.Add(sort);
        m.Items.Add(new Separator());
        int i = b.Columns.IndexOf(col);
        m.Items.Add(Ui.Item("Move left", () => { BoardStore.MoveColumn(b, col, -1); RenderBoard(); }, "", i > 0));
        m.Items.Add(Ui.Item("Move right", () => { BoardStore.MoveColumn(b, col, 1); RenderBoard(); }, "", i < b.Columns.Count - 1));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Archive all cards in this column", () => ArchiveColumn(b, col), "", col.ActiveCount > 0));
        m.Items.Add(Ui.Item("Delete column…", () => DeleteColumn(b, col), ""));
        return m;
    }

    private ContextMenu AddColumnMenu(Board b)
    {
        var m = new ContextMenu();
        foreach (var p in BoardStore.StatusPresets)
        {
            var it = Ui.Item(p.Title, () => AddColumn(b, p.Title));
            it.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Ui.Brush(BoardStore.Hex(p.Color)) };
            if (b.Columns.Any(c => c.Title.Equals(p.Title, StringComparison.OrdinalIgnoreCase))) it.ToolTip = "This board already has a column with this name";
            m.Items.Add(it);
        }
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Other name…", () =>
        {
            var name = Prompt("Add a column", "Column (status) name:", "", "For example Waiting on client, Testing or Shipped.");
            if (name != null) AddColumn(b, name);
        }, ""));
        return m;
    }

    private ContextMenu CardMenu(Board b, Card card)
    {
        var m = new ContextMenu();
        m.Items.Add(Ui.Item("Open", () => OpenCard(b, card), ""));
        var move = Ui.Item("Move to", null, "");
        var from = b.ColumnOf(card);
        foreach (var col in b.Columns)
        {
            var c = col;
            var it = Ui.Item(col.Title, () => MoveCard(b, card, c, -1), null, col != from);
            it.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Ui.Brush(BoardStore.Hex(col.Color)) };
            move.Items.Add(it);
        }
        var others = BoardStore.Boards.Where(x => x != b && x.Columns.Count > 0).ToList();
        if (others.Count > 0)
        {
            move.Items.Add(new Separator());
            var other = Ui.Item("Another board", null, "");
            foreach (var ob in others)
            {
                var target = ob;
                other.Items.Add(Ui.Item(ob.Name, () => { BoardStore.MoveCardToBoard(b, card, target); RenderBoard(); }));
            }
            move.Items.Add(other);
        }
        m.Items.Add(move);
        if (b.Labels.Count > 0)
        {
            var labels = Ui.Item("Labels", null, "");
            foreach (var l in b.Labels)
            {
                var label = l;
                var it = Ui.Item(l.Name, () =>
                {
                    if (!card.LabelIds.Remove(label.Id)) card.LabelIds.Add(label.Id);
                    card.Modified = DateTime.Now;
                    BoardStore.Touch(b);
                    RenderBoard();
                });
                it.IsChecked = card.LabelIds.Contains(l.Id);
                it.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = Ui.Brush(BoardStore.Hex(l.Color)) };
                labels.Items.Add(it);
            }
            m.Items.Add(labels);
        }
        var pri = Ui.Item("Priority", null, "");
        foreach (Priority p in Enum.GetValues(typeof(Priority)))
        {
            var pp = p;
            var it = Ui.Item(p == Priority.None ? "None" : p.ToString(), () => { card.Priority = pp; card.Modified = DateTime.Now; BoardStore.Touch(b); RenderBoard(); });
            it.IsChecked = card.Priority == p;
            pri.Items.Add(it);
        }
        m.Items.Add(pri);
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Duplicate", () => { BoardStore.DuplicateCard(b, card); RenderBoard(); }, ""));
        m.Items.Add(Ui.Item("Archive", () => { card.Archived = true; BoardStore.Touch(b); RenderBoard(); }, ""));
        m.Items.Add(Ui.Item("Delete…", () => DeleteCard(b, card), ""));
        return m;
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    private void NewBoard_Click(object sender, RoutedEventArgs e) => NewBoard();

    internal void NewBoard()
    {
        var d = new NewBoardDialog();
        if (d.ShowDialog() != true) return;
        var b = BoardStore.CreateBoard(d.BoardName, d.Template, d.BoardColor);
        RefreshBoardList(b);
    }

    private void RenameBoard(Board b)
    {
        var name = Prompt("Rename board", "Board name:", b.Name);
        if (name == null) return;
        b.Name = name;
        BoardStore.Touch(b);
        RefreshBoardList(CurrentBoard);
    }

    private void EditBoard(Board b)
    {
        if (new BoardDialog(b).ShowDialog() != true) return;
        RefreshBoardList(CurrentBoard);
        RebuildLabelFilter();
        RenderBoard();
    }

    private void DeleteBoard(Board b)
    {
        int cards = b.AllCards.Count();
        if (!Confirm("Delete board", $"Delete the board \"{b.Name}\" and its {cards} card{(cards == 1 ? "" : "s")}? This can't be undone.")) return;
        int i = BoardStore.Boards.IndexOf(b);
        BoardStore.DeleteBoard(b);
        var next = BoardStore.Boards.Count == 0 ? null : BoardStore.Boards[Math.Clamp(i, 0, BoardStore.Boards.Count - 1)];
        RefreshBoardList(b == CurrentBoard ? next : CurrentBoard);
    }

    private void ShowArchived(Board b)
    {
        new ArchivedDialog(b).ShowDialog();
        RenderBoard();
    }

    private void ExportMarkdown(Board b)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown (*.md)|*.md", FileName = Ui.SafeFileName(b.Name) + ".md" };
        if (dlg.ShowDialog(this) != true) return;
        try { System.IO.File.WriteAllText(dlg.FileName, BoardStore.ToMarkdown(b)); }
        catch (Exception ex) { MessageDialog.Show(this, "Export", "Couldn't save the file.\n\n" + ex.Message, MessageDialog.Kind.Error); }
    }

    private void ExportBoard(Board b)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Boardwalk board (*.json)|*.json", FileName = Ui.SafeFileName(b.Name) + ".json" };
        if (dlg.ShowDialog(this) != true) return;
        try { BoardStore.ExportBoard(b, dlg.FileName); }
        catch (Exception ex) { MessageDialog.Show(this, "Export", "Couldn't save the file.\n\n" + ex.Message, MessageDialog.Kind.Error); }
    }

    private void AddColumn_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentBoard != null) Ui.Open(AddColumnButton, AddColumnMenu(CurrentBoard));
    }

    internal Column AddColumn(Board b, string title)
    {
        var col = BoardStore.AddColumn(b, title);
        RenderBoard();
        Dispatcher.BeginInvoke(new Action(() => BoardScroll.ScrollToRightEnd()), DispatcherPriority.Loaded);
        return col;
    }

    private void RenameColumn(Board b, Column col)
    {
        var name = Prompt("Rename column", "Column (status) name:", col.Title);
        if (name == null) return;
        col.Title = name;
        BoardStore.Touch(b);
        RenderBoard();
    }

    private void SetLimit(Board b, Column col)
    {
        var text = Prompt("Card limit", $"Most cards allowed in \"{col.Title}\" at once:", col.WipLimit > 0 ? col.WipLimit.ToString() : "",
            "Leave empty or enter 0 for no limit. The count turns orange when there are more cards than this.", allowEmpty: true);
        if (text == null) return;
        col.WipLimit = int.TryParse(text, out int n) ? Math.Clamp(n, 0, 999) : 0;
        BoardStore.Touch(b);
        RenderBoard();
    }

    private void SortColumn<T>(Board b, Column col, Func<Card, T> key)
    {
        col.Cards = col.Cards.OrderBy(key).ToList();
        BoardStore.Touch(b);
        RenderBoard();
    }

    private void ArchiveColumn(Board b, Column col)
    {
        if (!Confirm("Archive cards", $"Archive all {col.ActiveCount} cards in \"{col.Title}\"? You can bring them back from Board options › Archived cards.")) return;
        BoardStore.ArchiveColumn(b, col);
        RenderBoard();
    }

    private void DeleteColumn(Board b, Column col)
    {
        int n = col.Cards.Count;
        if (n > 0 && !Confirm("Delete column", $"Delete \"{col.Title}\" and its {n} card{(n == 1 ? "" : "s")}? This can't be undone.")) return;
        BoardStore.DeleteColumn(b, col);
        RenderBoard();
    }

    internal void OpenCard(Board b, Card card)
    {
        new CardDialog(b, card).ShowDialog();
        RenderBoard();
    }

    private void DeleteCard(Board b, Card card)
    {
        if (!Confirm("Delete card", $"Delete \"{card.Title}\"? This can't be undone. (Archive keeps it out of the way instead.)")) return;
        BoardStore.DeleteCard(b, card);
        RenderBoard();
    }

    private bool Confirm(string title, string message) =>
        !SettingsService.Current.ConfirmDeletes || MessageDialog.Ask(this, title, message);

    private string Prompt(string title, string label, string initial, string hint = null, bool allowEmpty = false)
    {
        var d = new TextPromptDialog(title, label, initial, hint) { AllowEmpty = allowEmpty };
        return d.ShowDialog() == true ? d.Value : null;
    }

    // =====================================================================
    //  Header: title, search, label filter, board menu
    // =====================================================================

    private void BoardTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && CurrentBoard != null) RenameBoard(CurrentBoard);
    }

    private void BoardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentBoard != null) Ui.Open(BoardMenuButton, BoardMenu(CurrentBoard));
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderBoard();
    }

    private void RebuildLabelFilter()
    {
        _loadingFilter = true;
        LabelFilter.Items.Clear();
        LabelFilter.Items.Add("All labels");
        if (CurrentBoard != null)
            foreach (var l in CurrentBoard.Labels) LabelFilter.Items.Add(l.Name);
        int i = _labelFilter == null ? 0 : LabelFilter.Items.IndexOf(_labelFilter);
        if (i < 0) { i = 0; _labelFilter = null; }
        LabelFilter.SelectedIndex = i;
        _loadingFilter = false;
    }

    private void LabelFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingFilter) return;
        _labelFilter = LabelFilter.SelectedIndex > 0 ? LabelFilter.SelectedItem as string : null;
        RenderBoard();
    }

    private void BoardScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
        BoardScroll.ScrollToHorizontalOffset(BoardScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // =====================================================================
    //  Settings, theme and keyboard
    // =====================================================================

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(this).ShowDialog();

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Current.Theme = ThemeManager.IsDark ? ThemeMode.Light : ThemeMode.Dark;
        SettingsService.Commit();
    }

    private void UpdateThemeToggle()
    {
        if (ThemeToggle == null) return;
        ThemeToggle.Content = ThemeManager.IsDark ? "" : "";
        ThemeToggle.ToolTip = ThemeManager.IsDark ? "Switch to Light mode" : "Switch to Dark mode";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        if (ctrl && shift && key == Key.N) { e.Handled = true; NewBoard(); }
        else if (ctrl && !shift && key == Key.N && CurrentBoard?.Columns.Count > 0)
        {
            e.Handled = true;
            _addingInColumnId = (CurrentBoard.Columns.FirstOrDefault(c => !c.IsDone) ?? CurrentBoard.Columns[0]).Id;
            RenderBoard();
        }
        else if (ctrl && key == Key.F && CurrentBoard != null) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (ctrl && key == Key.OemComma) { e.Handled = true; new SettingsWindow(this).ShowDialog(); }
        else if (key == Key.F2 && CurrentBoard != null && Keyboard.FocusedElement is not TextBox) { e.Handled = true; RenameBoard(CurrentBoard); }
        else if (ctrl && key == Key.Tab && BoardStore.Boards.Count > 1)
        {
            e.Handled = true;
            int i = BoardStore.Boards.IndexOf(CurrentBoard) + (shift ? -1 : 1);
            var b = BoardStore.Boards[(i + BoardStore.Boards.Count) % BoardStore.Boards.Count];
            SelectInList(b);
            ShowBoard(b);
        }
        else if (key == Key.Escape && Keyboard.FocusedElement == SearchBox && SearchBox.Text.Length > 0) { e.Handled = true; SearchBox.Text = ""; }
    }

    // =====================================================================
    //  Window position
    // =====================================================================

    private void RestoreWindow()
    {
        var s = SettingsService.Current;
        if (App.IsSelfTest || !s.RememberWindow) { WindowStartupLocation = WindowStartupLocation.CenterScreen; return; }
        Width = s.WinWidth;
        Height = s.WinHeight;
        if (!double.IsNaN(s.WinLeft) && !double.IsNaN(s.WinTop) &&
            s.WinLeft > SystemParameters.VirtualScreenLeft - 50 && s.WinTop > SystemParameters.VirtualScreenTop - 50 &&
            s.WinLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
            s.WinTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = s.WinLeft;
            Top = s.WinTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (s.WinMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindow()
    {
        if (App.IsSelfTest) return;
        var s = SettingsService.Current;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty) { s.WinLeft = r.Left; s.WinTop = r.Top; s.WinWidth = r.Width; s.WinHeight = r.Height; }
        s.WinMaximized = WindowState == WindowState.Maximized;
        SettingsService.Save();
        BoardStore.Save();
    }
}
