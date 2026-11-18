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

    /// <summary>True while "My week" is showing instead of a board.</summary>
    internal bool WeekShowing { get; private set; }

    /// <summary>Column id (plus "|lane" in swimlanes) → the panel that shows its cards (also used by the self-test).</summary>
    internal readonly Dictionary<string, StackPanel> CardPanels = new();

    private bool _loadingList, _loadingFilter, _exiting;
    private string _search = "";
    private string _labelFilter;          // label name, or null for all
    private string _addingKey;            // "columnId|lane" whose "Add a card" box is open
    private Point _dragStart;
    private Card _pressedCard;
    private bool _dragging;
    private readonly Border _dropMarker;
    private StackPanel _markerPanel;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };
    private readonly Dictionary<string, DateTime> _gitHubRefreshed = new();

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        _dropMarker = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(2, 0, 2, 8), IsHitTestVisible = false };
        _dropMarker.SetResourceReference(Border.BackgroundProperty, "Accent");
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastBar.Visibility = Visibility.Collapsed; };
        RestoreWindow();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        ThemeManager.ThemeChanged += () => { UpdateThemeToggle(); Render(); };
        BoardStore.CardCompleted += OnCardCompleted;
        UpdateThemeToggle();
    }

    private bool _loadedOnce;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        var last = BoardStore.Boards.FirstOrDefault(b => b.Id == SettingsService.Current.LastBoardId);
        RefreshBoardList(last);
        if (App.IsSelfTest)
        {
            Dispatcher.BeginInvoke(new Action(async () => await RunSelfTest()), DispatcherPriority.ApplicationIdle);
            return;
        }
        if (SettingsService.Current.ShowMyWeek && BoardStore.Boards.Count > 0) ShowWeek();
        if (BoardStore.LoadWarning != null)
            MessageDialog.Show(this, "Boards restored", BoardStore.LoadWarning, MessageDialog.Kind.Warning);
    }

    /// <summary>Shows the window from the notification area (or a second start), optionally on My week.</summary>
    internal void BringToFront(bool week)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        if (week && BoardStore.Boards.Count > 0) ShowWeek();
    }

    internal void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindow();
        if (SettingsService.Current.CloseToTray && !_exiting && !App.IsSelfTest)
        {
            e.Cancel = true;
            Hide();
        }
    }

    internal void Render()
    {
        if (WeekShowing) RenderWeek(); else RenderBoard();
    }

    internal void Toast(string message)
    {
        ToastText.Text = message;
        ToastBar.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // =====================================================================
    //  Sidebar: boards and My week
    // =====================================================================

    internal void RefreshBoardList(Board select = null, bool keepWeek = false)
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
            if (b == select && !(keepWeek && WeekShowing)) item.IsSelected = true;
        }
        _loadingList = false;
        if (keepWeek && WeekShowing && BoardStore.Boards.Count > 0)
        {
            CurrentBoard = select;
            RenderWeek();
        }
        else ShowBoard(select);
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

    private void UpdateSidebar()
    {
        foreach (var o in BoardList.Items)
            if (o is ListBoxItem { Tag: Board b } item) item.Content = BoardRow(b);
        int due = ReminderService.DueCards(DateTime.Today).Count;
        MyWeekBadge.Visibility = due > 0 ? Visibility.Visible : Visibility.Collapsed;
        MyWeekCount.Text = due.ToString();
        MyWeekBadge.ToolTip = $"{due} card{(due == 1 ? "" : "s")} due today or overdue";
        if (WeekShowing) MyWeekButton.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
        else MyWeekButton.ClearValue(Control.BackgroundProperty);
        UndoButton.IsEnabled = BoardStore.CanUndo;
        RedoButton.IsEnabled = BoardStore.CanRedo;
    }

    private void BoardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingList) return;
        if (BoardList.SelectedItem is ListBoxItem { Tag: Board b } && (b != CurrentBoard || WeekShowing)) ShowBoard(b);
    }

    internal void ShowBoard(Board b)
    {
        WeekShowing = false;
        CurrentBoard = b;
        _addingKey = null;
        _labelFilter = null;
        var s = SettingsService.Current;
        if (s.LastBoardId != (b?.Id ?? "") || s.ShowMyWeek)
        {
            s.LastBoardId = b?.Id ?? "";
            s.ShowMyWeek = false;
            SettingsService.Save();
        }
        RebuildLabelFilter();
        RenderBoard();
        BoardScroll.ScrollToLeftEnd();
        BoardScroll.ScrollToTop();
        if (b != null) _ = RefreshGitHub(b, auto: true);
    }

    internal void ShowWeek()
    {
        WeekShowing = true;
        SelectInList(null);
        if (!SettingsService.Current.ShowMyWeek) { SettingsService.Current.ShowMyWeek = true; SettingsService.Save(); }
        RenderWeek();
        WeekView.ScrollToTop();
    }

    private void MyWeek_Click(object sender, RoutedEventArgs e) => ShowWeek();

    private void SelectInList(Board b)
    {
        _loadingList = true;
        foreach (var o in BoardList.Items)
            if (o is ListBoxItem item) item.IsSelected = item.Tag == b;
        _loadingList = false;
    }

    // =====================================================================
    //  My week
    // =====================================================================

    internal void RenderWeek()
    {
        if (WeekPanel == null) return;
        Title = "My week - Boardwalk";
        EmptyState.Visibility = Visibility.Collapsed;
        BoardHeader.Visibility = BoardScroll.Visibility = Visibility.Collapsed;
        WeekView.Visibility = Visibility.Visible;
        WeekPanel.Children.Clear();

        var title = new TextBlock { Text = "My week", FontSize = 22, FontWeight = FontWeights.SemiBold };
        WeekPanel.Children.Add(title);
        var sub = new TextBlock { Text = "Cards due in the next seven days on all your boards. Click one to open it.", Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 2, 0, 14) };
        WeekPanel.Children.Add(sub);

        var all = ReminderService.DueCards(DateTime.Today.AddDays(7));
        var groups = new List<(string Name, Func<DateTime, bool> Has, string Brush)>
        {
            ("Overdue", new Func<DateTime, bool>(d => d < DateTime.Today), "DangerText"),
            ("Today", new Func<DateTime, bool>(d => d == DateTime.Today), "WarnText"),
            ("Tomorrow", new Func<DateTime, bool>(d => d == DateTime.Today.AddDays(1)), "Text"),
            ("Later this week", new Func<DateTime, bool>(d => d > DateTime.Today.AddDays(1)), "Text"),
        };
        foreach (var (name, has, key) in groups)
        {
            var cards = all.Where(x => has(x.Card.Due.Value.Date)).ToList();
            if (cards.Count == 0) continue;
            var h = new TextBlock { Text = $"{name}  ·  {cards.Count}", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 8) };
            h.SetResourceReference(TextBlock.ForegroundProperty, key);
            WeekPanel.Children.Add(h);
            foreach (var x in cards) WeekPanel.Children.Add(WeekRow(x));
        }
        if (all.Count == 0)
        {
            var none = new TextBlock { Text = "Nothing is due this week. Cards with a due date in the next seven days show up here.", FontSize = 14, Margin = new Thickness(0, 20, 0, 0), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            WeekPanel.Children.Add(none);
        }
        UpdateSidebar();
    }

    private Border WeekRow(DueCard x)
    {
        var row = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Tag = x.Card };
        row.SetResourceReference(Border.BackgroundProperty, "CardBg");
        row.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BorderBrushProperty, "CardHoverBorder");
        row.MouseLeave += (_, _) => row.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        var dock = new DockPanel();
        row.Child = dock;
        var done = x.Board.FirstDoneColumn;
        if (done != null)
        {
            var complete = new Button { Content = Ui.IconText("", "Complete"), VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Move to {done.Title} on {x.Board.Name}", Margin = new Thickness(10, 0, 0, 0) };
            complete.Click += (_, e) =>
            {
                e.Handled = true;
                BoardStore.MoveCard(x.Board, x.Card, done, -1);
                Toast($"#{x.Card.Number} moved to {done.Title}");
                RenderWeek();
            };
            DockPanel.SetDock(complete, Dock.Right);
            dock.Children.Add(complete);
        }
        var sp = new StackPanel();
        var titleRow = new WrapPanel();
        foreach (var l in x.Card.LabelIds.Select(x.Board.Label).Where(l => l != null)) { var p = Ui.LabelPill(l); p.Margin = new Thickness(0, 1, 6, 2); titleRow.Children.Add(p); }
        titleRow.Children.Add(new TextBlock { Text = x.Card.Title, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(titleRow);
        var meta = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var boardTag = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        boardTag.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = Ui.Brush(BoardStore.Hex(x.Board.Color)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var bn = new TextBlock { Text = $"{x.Board.Name} · {x.Column.Title} · #{x.Card.Number}", FontSize = 12 };
        bn.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        boardTag.Children.Add(bn);
        meta.Children.Add(boardTag);
        int days = (x.Card.Due.Value.Date - DateTime.Today).Days;
        meta.Children.Add(Ui.Meta("", Ui.FriendlyDate(x.Card.Due.Value), days < 0 ? "DangerText" : days == 0 ? "WarnText" : "TextSecondary"));
        if (x.Card.Priority != Priority.None) meta.Children.Add(Ui.PriorityBadge(x.Card.Priority));
        if (x.Board.OpenBlockers(x.Card).Count > 0) meta.Children.Add(Ui.Meta("", "Blocked", "DangerText"));
        sp.Children.Add(meta);
        dock.Children.Add(sp);
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            new CardDialog(x.Board, x.Card) { Owner = this }.ShowDialog();
            RenderWeek();
        };
        return row;
    }

    private static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // =====================================================================
    //  The board
    // =====================================================================

    internal void RenderBoard()
    {
        if (ColumnsPanel == null) return;
        var b = CurrentBoard;
        bool has = b != null;
        WeekView.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        BoardHeader.Visibility = BoardScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ColumnsPanel.Children.Clear();
        CardPanels.Clear();
        _markerPanel = null;
        if (!has) { Title = "Boardwalk"; BoardScroll.Content = ColumnsPanel; UpdateSidebar(); return; }

        Title = $"{b.Name} - Boardwalk";
        BoardTitle.Text = b.Name;
        BoardDot.Background = Ui.Brush(BoardStore.Hex(b.Color));
        int cards = b.ActiveCardCount;
        var sub = $"{cards} card{(cards == 1 ? "" : "s")} · {b.Columns.Count} column{(b.Columns.Count == 1 ? "" : "s")}";
        if (b.NextVersion.Trim().Length > 0) sub += " · working on " + b.NextVersion.Trim();
        if (b.Lanes != LaneMode.None) sub += " · swimlanes by " + b.Lanes.ToString().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(b.Description))
            sub = b.Description.Trim().Replace("\r", " ").Replace("\n", " ") + "   ·   " + sub;
        BoardSubtitle.Text = sub;

        if (b.Lanes == LaneMode.None || b.Columns.Count == 0)
        {
            BoardScroll.Content = ColumnsPanel;
            BoardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            foreach (var col in b.Columns) ColumnsPanel.Children.Add(BuildColumn(b, col));
            ColumnsPanel.Children.Add(BuildAddColumnTile(b));
        }
        else
        {
            BoardScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            BoardScroll.Content = BuildLaneGrid(b);
        }
        UpdateSidebar();
    }

    private FrameworkElement BuildColumnHeader(Board b, Column col)
    {
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
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
        return header;
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

        var header = BuildColumnHeader(b, col);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var footer = BuildAddCard(b, col, null);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);

        var panel = BuildCardPanel(b, col, null, col.Cards);
        var scroll = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, Padding = new Thickness(0, 0, 2, 0),
        };
        dock.Children.Add(scroll);
        if (_addingKey == Key(col, null)) scroll.Loaded += (_, _) => scroll.ScrollToBottom();

        root.DragOver += (_, e) => OnCellDragOver(panel, e);
        root.Drop += (_, e) => OnCellDrop(b, col, null, panel, e);
        return root;
    }

    private static string Key(Column col, string lane) => col.Id + (lane == null ? "" : "|" + lane);

    private StackPanel BuildCardPanel(Board b, Column col, string lane, IEnumerable<Card> source)
    {
        var panel = new StackPanel { Background = Brushes.Transparent, MinHeight = 40, Tag = col };
        CardPanels[Key(col, lane)] = panel;
        foreach (var card in source)
        {
            if (card.Archived || !Matches(b, card)) continue;
            panel.Children.Add(BuildCard(b, col, card));
        }
        if (panel.Children.Count == 0 && lane == null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Filtering ? "No matching cards" : "No cards yet. Drag one here or add one below.",
                Style = (Style)FindResource("Hint"), TextAlignment = TextAlignment.Center, Margin = new Thickness(8, 14, 8, 14),
            });
        }
        return panel;
    }

    // ---------------- swimlanes ----------------

    internal static List<(string Key, string Title, string Color)> LanesOf(Board b) => b.Lanes switch
    {
        LaneMode.Priority => new()
        {
            ("Urgent", "Urgent", "Red"), ("High", "High", "Orange"), ("Medium", "Medium", "Yellow"),
            ("Low", "Low", "Gray"), ("None", "No priority", "Gray"),
        },
        LaneMode.Label => b.Labels.Select(l => (Key: l.Id, Title: l.Name, Color: l.Color)).Append((Key: "", Title: "No label", Color: "Gray")).ToList(),
        _ => new(),
    };

    internal static string LaneOf(Board b, Card c) => b.Lanes == LaneMode.Priority
        ? c.Priority.ToString()
        : b.Labels.FirstOrDefault(l => c.LabelIds.Contains(l.Id))?.Id ?? "";

    internal static void ApplyLane(Board b, Card c, string lane)
    {
        if (lane == null) return;
        if (b.Lanes == LaneMode.Priority)
        {
            if (Enum.TryParse<Priority>(lane, out var p)) c.Priority = p;
        }
        else if (b.Lanes == LaneMode.Label)
        {
            var current = LaneOf(b, c);
            if (current == lane) return;
            if (current != "") c.LabelIds.Remove(current);
            if (lane != "" && !c.LabelIds.Contains(lane)) c.LabelIds.Insert(0, lane);
        }
    }

    private Grid BuildLaneGrid(Board b)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        double w = SettingsService.Current.ColumnWidth + 12;
        foreach (var _ in b.Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < b.Columns.Count; i++)
        {
            var col = b.Columns[i];
            var head = BuildColumnHeader(b, col);
            head.Margin = new Thickness(0);
            var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 8, 8), Margin = new Thickness(0, 0, 12, 4), Child = head, BorderThickness = new Thickness(1) };
            box.SetResourceReference(Border.BackgroundProperty, "ColumnBg");
            box.SetResourceReference(Border.BorderBrushProperty, "ColumnBorder");
            Grid.SetColumn(box, i);
            grid.Children.Add(box);
        }
        int row = 1;
        foreach (var (key, title, color) in LanesOf(b))
        {
            var laneCards = b.AllCards.Where(c => !c.Archived && LaneOf(b, c) == key).ToList();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var laneHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 10, 0, 6) };
            laneHead.Children.Add(new Border { Width = 4, Height = 16, CornerRadius = new CornerRadius(2), Background = Ui.Brush(BoardStore.Hex(color)), Margin = new Thickness(0, 0, 8, 0) });
            laneHead.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14 });
            var cnt = new TextBlock { Text = "  " + laneCards.Count, FontSize = 13 };
            cnt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            laneHead.Children.Add(cnt);
            Grid.SetRow(laneHead, row);
            Grid.SetColumnSpan(laneHead, Math.Max(1, b.Columns.Count));
            grid.Children.Add(laneHead);
            row++;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < b.Columns.Count; i++)
            {
                var col = b.Columns[i];
                var cell = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(8, 8, 8, 4), Margin = new Thickness(0, 0, 12, 4), AllowDrop = true, MinHeight = 64, Tag = col };
                cell.SetResourceReference(Border.BackgroundProperty, "ColumnBg");
                cell.SetResourceReference(Border.BorderBrushProperty, "ColumnBorder");
                var dock = new DockPanel();
                var footer = BuildAddCard(b, col, key);
                DockPanel.SetDock(footer, Dock.Bottom);
                dock.Children.Add(footer);
                var panel = BuildCardPanel(b, col, key, col.Cards.Where(c => LaneOf(b, c) == key));
                panel.MinHeight = 20;
                dock.Children.Add(panel);
                cell.Child = dock;
                var lane = key;
                cell.DragOver += (_, e) => OnCellDragOver(panel, e);
                cell.Drop += (_, e) => OnCellDrop(b, col, lane, panel, e);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, i);
                grid.Children.Add(cell);
            }
            row++;
        }
        return grid;
    }

    // ---------------- adding cards ----------------

    private FrameworkElement BuildAddCard(Board b, Column col, string lane)
    {
        var host = new ContentControl { Margin = new Thickness(0, 4, 2, 0), Focusable = false };
        var key = Key(col, lane);

        void ShowButton()
        {
            var row = new DockPanel();
            if (b.Templates.Count > 0)
            {
                var tpl = new Button { Style = (Style)FindResource("IconButton"), Content = "", Width = 30, Height = 30, FontSize = 13, ToolTip = "Add from a template" };
                tpl.Click += (_, _) =>
                {
                    var m = new ContextMenu();
                    foreach (var t in b.Templates)
                    {
                        var template = t;
                        m.Items.Add(Ui.Item(t.Name, () =>
                        {
                            var card = BoardStore.AddFromTemplate(b, col, template);
                            ApplyLane(b, card, lane);
                            BoardStore.Touch(b);
                            Render();
                            OpenCard(b, card);
                        }, ""));
                    }
                    Ui.Open(tpl, m);
                };
                DockPanel.SetDock(tpl, Dock.Right);
                row.Children.Add(tpl);
            }
            var btn = new Button
            {
                Style = (Style)FindResource("FlatBase"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6),
                Content = Ui.IconText("", lane == null ? "Add a card" : "Add"), ToolTip = "Add a card to " + col.Title,
            };
            btn.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
            btn.Click += (_, _) => { _addingKey = key; ShowEditor(); };
            row.Children.Add(btn);
            host.Content = row;
        }

        void ShowEditor()
        {
            var box = new TextBox
            {
                TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MinHeight = 56,
                VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = "Type a title and press Enter. Add #label, !high and a date like friday or @11/20 at the end. Esc to stop.",
            };
            var preview = new TextBlock { FontSize = 12, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            preview.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var tip = new TextBlock { Text = "Tip: #label  !high  friday", FontSize = 11.5, Margin = new Thickness(2, 4, 0, 0) };
            tip.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            box.TextChanged += (_, _) =>
            {
                var r = QuickAdd.Parse(b, box.Text);
                preview.Text = QuickAdd.Describe(b, r);
                preview.Visibility = preview.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                tip.Visibility = preview.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            };
            preview.Visibility = Visibility.Collapsed;
            var add = new Button { Content = "Add card", Style = (Style)FindResource("AccentButton") };
            var cancel = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "Stop adding (Esc)", Margin = new Thickness(4, 0, 0, 0) };
            void Commit()
            {
                if (box.Text.Trim().Length == 0) return;
                AddCardQuick(b, col, box.Text, lane);
                Render();   // keeps the box open (_addingKey) for the next card
            }
            void Stop() { _addingKey = null; ShowButton(); }
            add.Click += (_, _) => Commit();
            cancel.Click += (_, _) => Stop();
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Commit(); }
                else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Stop(); }
            };
            box.Loaded += (_, _) => { box.Focus(); Keyboard.Focus(box); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            buttons.Children.Add(add);
            buttons.Children.Add(cancel);
            var sp = new StackPanel();
            sp.Children.Add(box);
            sp.Children.Add(preview);
            sp.Children.Add(tip);
            sp.Children.Add(buttons);
            host.Content = sp;
        }

        if (_addingKey == key) ShowEditor(); else ShowButton();
        return host;
    }

    /// <summary>Adds a card from quick-add text ("Fix crash #bug !high friday").</summary>
    internal Card AddCardQuick(Board b, Column col, string text, string lane = null)
    {
        var r = QuickAdd.Parse(b, text);
        var card = BoardStore.AddCard(b, col, r.Title, -1, save: false);
        card.LabelIds.AddRange(r.LabelIds);
        card.Priority = r.Priority;
        card.Due = r.Due;
        ApplyLane(b, card, lane);
        BoardStore.Touch(b);
        return card;
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

    // ---------------- a card ----------------

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

        var parent = b.CardById(card.ParentId);
        if (parent != null && !s.CompactCards)
        {
            var p = new TextBlock { Text = $"Part of #{parent.Number} {parent.Title}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            p.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            sp.Children.Add(p);
        }

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

        // Number, blocked, priority, due date, checklist, sub-tasks, version, link, attachments
        var meta = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        meta.Children.Add(Ui.Meta(null, "#" + card.Number, "TextSecondary"));
        var blockers = col.IsDone ? new List<Card>() : b.OpenBlockers(card);
        if (blockers.Count > 0)
        {
            var m = Ui.Meta("", "Blocked by " + string.Join(", ", blockers.Select(x => "#" + x.Number)), "DangerText");
            m.ToolTip = "Waiting on: " + string.Join("; ", blockers.Select(x => $"#{x.Number} {x.Title}"));
            meta.Children.Add(m);
        }
        if (card.Priority != Priority.None) meta.Children.Add(Ui.PriorityBadge(card.Priority));
        if (card.Due is DateTime due)
        {
            int days = (due.Date - DateTime.Today).Days;
            string key = col.IsDone ? "TextSecondary" : days < 0 ? "DangerText" : days <= 1 ? "WarnText" : "TextSecondary";
            var m = Ui.Meta("", Ui.FriendlyDate(due), key);
            m.ToolTip = (days < 0 && !col.IsDone ? "Overdue · was due " : "Due ") + due.ToString("D");
            meta.Children.Add(m);
        }
        if (card.Repeat != RepeatKind.None)
        {
            var m = Ui.Meta("", "", "TextSecondary");
            m.ToolTip = "Repeats " + card.Repeat.ToString().ToLowerInvariant();
            meta.Children.Add(m);
        }
        if (card.Checklist.Count > 0)
        {
            int done = card.Checklist.Count(i => i.Done);
            var m = Ui.Meta("", $"{done}/{card.Checklist.Count}", done == card.Checklist.Count ? "Accent" : "TextSecondary");
            m.ToolTip = "Checklist";
            meta.Children.Add(m);
        }
        var children = b.Children(card).ToList();
        if (children.Count > 0)
        {
            int done = children.Count(c => b.IsComplete(c));
            var m = Ui.Meta("", $"{done}/{children.Count}", done == children.Count ? "Accent" : "TextSecondary");
            m.ToolTip = "Sub-tasks completed";
            meta.Children.Add(m);
        }
        if (card.Version.Length > 0)
        {
            var m = Ui.Meta("", card.Version, "TextSecondary");
            m.ToolTip = "Version " + card.Version;
            meta.Children.Add(m);
        }
        if (!string.IsNullOrWhiteSpace(card.Link))
        {
            var gh = GitHubService.ParseLink(card.Link);
            StackPanel m;
            if (gh != null && card.LinkState.Length > 0) m = Ui.GitHubBadge(gh, card.LinkState);
            else m = Ui.Meta("", gh != null ? $"#{gh.Number}" : "", "TextSecondary");
            m.ToolTip = card.Link;
            meta.Children.Add(m);
        }
        if (card.Attachments.Count > 0)
        {
            var m = Ui.Meta("", card.Attachments.Count.ToString(), "TextSecondary");
            m.ToolTip = string.Join("\n", card.Attachments.Select(BoardStore.AttachmentName));
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
        root.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; OpenCard(b, card); } };
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
            || card.Version.Equals(q, StringComparison.CurrentCultureIgnoreCase)
            || card.Checklist.Any(i => i.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            || card.LabelIds.Select(b.Label).Any(l => l != null && l.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    // =====================================================================
    //  Drag and drop
    // =====================================================================

    private void OnCellDragOver(StackPanel panel, DragEventArgs e)
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

    private void OnCellDrop(Board b, Column col, string lane, StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is not string id) return;
        var card = b.AllCards.FirstOrDefault(c => c.Id == id);
        if (card == null) return;
        var y = e.GetPosition(panel).Y;
        int index = -1;
        Card after = null;
        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement { Tag: Card other } fe && fe != _dropMarker)
            {
                var top = fe.TranslatePoint(new Point(0, 0), panel).Y;
                // The marker sits above the card it was shown before, so measure without it.
                if (_markerPanel == panel && panel.Children.IndexOf(_dropMarker) < panel.Children.IndexOf(fe)) top -= _dropMarker.ActualHeight + _dropMarker.Margin.Bottom;
                if (y < top + fe.ActualHeight / 2) { index = col.Cards.IndexOf(other); break; }
                after = other;
            }
        }
        // In a swimlane cell, dropping below the last card goes just after it.
        if (index < 0 && lane != null && after != null) index = col.Cards.IndexOf(after) + 1;
        RemoveDropMarker();
        MoveCard(b, card, col, index, lane);
    }

    /// <summary>Moves a card (and into a swimlane) and redraws. Used by drag and drop, menus and the self-test.</summary>
    internal void MoveCard(Board b, Card card, Column target, int index, string lane = null)
    {
        if (lane != null) ApplyLane(b, card, lane);
        BoardStore.MoveCard(b, card, target, index);
        Render();
    }

    private void OnCardCompleted(Board b, Card card)
    {
        if (card.History.LastOrDefault()?.Text.StartsWith("Next repeat") == true)
        {
            var next = b.AllCards.FirstOrDefault(c => card.History[^1].Text.EndsWith("#" + c.Number));
            Toast(next != null ? $"Repeats: made #{next.Number}, due {Ui.FriendlyDate(next.Due ?? DateTime.Today)}" : "Made the next repeat");
        }
        var gh = GitHubService.ParseLink(card.Link);
        if (gh == null || gh.IsPull || !b.CloseIssuesOnComplete || !GitHubService.HasToken || card.LinkState == "closed" || App.IsSelfTest) return;
        _ = CloseIssue(b, card, gh);
    }

    private async Task CloseIssue(Board b, Card card, GitHubRef gh)
    {
        try
        {
            await GitHubService.CloseIssue(gh);
            card.LinkState = "closed";
            card.LinkChecked = DateTime.Now;
            BoardStore.SaveQuietly();
            Toast($"Closed GitHub issue #{gh.Number}");
            Render();
        }
        catch (Exception ex) { Toast("Couldn't close the GitHub issue: " + ex.Message); }
    }

    /// <summary>Updates the open/closed/merged state of a board's GitHub links (automatically at most every 30 minutes).</summary>
    internal async Task RefreshGitHub(Board b, bool auto)
    {
        if (App.IsSelfTest) return;
        if (!b.AllCards.Any(c => GitHubService.ParseLink(c.Link) != null)) { if (!auto) Toast("No cards on this board link to GitHub issues or pull requests"); return; }
        if (auto && _gitHubRefreshed.TryGetValue(b.Id, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(30)) return;
        _gitHubRefreshed[b.Id] = DateTime.Now;
        try
        {
            if (!auto) Toast("Checking GitHub…");
            int changed = await GitHubService.RefreshBoard(b);
            BoardStore.SaveQuietly();
            if (!auto) Toast(changed == 0 ? "GitHub links are up to date" : $"Updated {changed} GitHub link{(changed == 1 ? "" : "s")}");
            if (CurrentBoard == b && !WeekShowing) RenderBoard();
        }
        catch (Exception ex) { if (!auto) Toast(ex.Message); }
    }

    // =====================================================================
    //  Menus
    // =====================================================================

    private ContextMenu BoardMenu(Board b, bool forSidebar = false)
    {
        var m = new ContextMenu();
        if (forSidebar) m.Items.Add(Ui.Item("Open", () => { SelectInList(b); ShowBoard(b); }, ""));
        m.Items.Add(Ui.Item("Rename…", () => RenameBoard(b), ""));
        m.Items.Add(Ui.Item("Board settings, labels and templates…", () => EditBoard(b), ""));
        if (!forSidebar)
        {
            var lanes = Ui.Item("Swimlanes", null, "");
            foreach (var (mode, text) in new[] { (LaneMode.None, "None"), (LaneMode.Priority, "By priority"), (LaneMode.Label, "By label") })
            {
                var md = mode;
                var it = Ui.Item(text, () => { b.Lanes = md; BoardStore.Touch(b); RenderBoard(); });
                it.IsChecked = b.Lanes == mode;
                lanes.Items.Add(it);
            }
            m.Items.Add(lanes);
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Item("Write changelog…", () => new ChangelogDialog(b) { Owner = this }.ShowDialog(), ""));
            m.Items.Add(Ui.Item("Board stats…", () => new StatsDialog(b) { Owner = this }.ShowDialog(), ""));
            m.Items.Add(Ui.Item("Archived cards…", () => ShowArchived(b), ""));
            var gh = Ui.Item("GitHub", null, "");
            gh.Items.Add(Ui.Item("Import issues…", () => _ = ImportIssues(b)));
            gh.Items.Add(Ui.Item("Check linked issues and pull requests", () => _ = RefreshGitHub(b, auto: false)));
            m.Items.Add(gh);
            m.Items.Add(new Separator());
            m.Items.Add(Ui.Item("Copy as Markdown", () => { Clipboard.SetText(BoardStore.ToMarkdown(b)); Toast("Copied as Markdown"); }, ""));
            m.Items.Add(Ui.Item("Export as Markdown…", () => ExportMarkdown(b), ""));
            m.Items.Add(Ui.Item("Export board…", () => ExportBoard(b), ""));
        }
        m.Items.Add(new Separator());
        var dup = Ui.Item("Duplicate", null, "");
        dup.Items.Add(Ui.Item("With its cards", () => RefreshBoardList(BoardStore.DuplicateBoard(b, true))));
        dup.Items.Add(Ui.Item("Columns, labels and templates only", () => RefreshBoardList(BoardStore.DuplicateBoard(b, false))));
        m.Items.Add(dup);
        int i = BoardStore.Boards.IndexOf(b);
        m.Items.Add(Ui.Item("Move up in the list", () => { BoardStore.MoveBoard(b, -1); RefreshBoardList(CurrentBoard, keepWeek: true); }, "", i > 0));
        m.Items.Add(Ui.Item("Move down in the list", () => { BoardStore.MoveBoard(b, 1); RefreshBoardList(CurrentBoard, keepWeek: true); }, "", i < BoardStore.Boards.Count - 1));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Delete board…", () => DeleteBoard(b), ""));
        return m;
    }

    private ContextMenu ColumnMenu(Board b, Column col)
    {
        var m = new ContextMenu();
        m.Items.Add(Ui.Item("Add a card", () => { _addingKey = Key(col, b.Lanes == LaneMode.None ? null : LanesOf(b).Last().Key); Render(); }, ""));
        m.Items.Add(Ui.Item("Rename…", () => RenameColumn(b, col), ""));
        var color = Ui.Item("Status colour", null, "");
        foreach (var (name, hex) in BoardStore.Palette)
        {
            var it = Ui.Item(name, () => { col.Color = name; BoardStore.Touch(b); Render(); });
            it.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Ui.Brush(hex) };
            it.IsChecked = col.Color == name;
            color.Items.Add(it);
        }
        m.Items.Add(color);
        var done = Ui.Item("Counts as complete", () => { col.IsDone = !col.IsDone; BoardStore.Touch(b); Render(); });
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
        m.Items.Add(Ui.Item("Move left", () => { BoardStore.MoveColumn(b, col, -1); Render(); }, "", i > 0));
        m.Items.Add(Ui.Item("Move right", () => { BoardStore.MoveColumn(b, col, 1); Render(); }, "", i < b.Columns.Count - 1));
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
                other.Items.Add(Ui.Item(ob.Name, () => { BoardStore.MoveCardToBoard(b, card, target); Render(); Toast($"Moved to {target.Name}"); }));
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
                    Render();
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
            var it = Ui.Item(p == Priority.None ? "None" : p.ToString(), () => { card.Priority = pp; card.Modified = DateTime.Now; BoardStore.Touch(b); Render(); });
            it.IsChecked = card.Priority == p;
            pri.Items.Add(it);
        }
        m.Items.Add(pri);
        var rep = Ui.Item("Repeat", null, "");
        foreach (RepeatKind r in Enum.GetValues(typeof(RepeatKind)))
        {
            var rr = r;
            var it = Ui.Item(r == RepeatKind.None ? "Doesn't repeat" : r.ToString(), () =>
            {
                card.Repeat = rr;
                if (rr != RepeatKind.None && card.Due == null) card.Due = DateTime.Today;
                BoardStore.Touch(b);
                Render();
            });
            it.IsChecked = card.Repeat == r;
            rep.Items.Add(it);
        }
        m.Items.Add(rep);
        m.Items.Add(Ui.Item("Add a sub-task…", () =>
        {
            var t = Prompt("Add a sub-task", $"Sub-task of #{card.Number} {card.Title}:", "");
            if (t == null || t.Length == 0) return;
            var sub = BoardStore.AddSubTask(b, card, t);
            Render();
            Toast($"Added #{sub.Number}");
        }, ""));
        m.Items.Add(new Separator());
        m.Items.Add(Ui.Item("Duplicate", () => { BoardStore.DuplicateCard(b, card); Render(); }, ""));
        m.Items.Add(Ui.Item("Archive", () => { card.Archived = true; BoardStore.Log(card, "Archived"); BoardStore.Touch(b); Render(); }, ""));
        m.Items.Add(Ui.Item("Delete…", () => DeleteCard(b, card), ""));
        return m;
    }

    // =====================================================================
    //  Actions
    // =====================================================================

    private void NewBoard_Click(object sender, RoutedEventArgs e) => NewBoard();

    internal void NewBoard()
    {
        var d = new NewBoardDialog { Owner = this };
        if (d.ShowDialog() != true) return;
        var b = BoardStore.CreateBoard(d.BoardName, d.ChosenTemplate, d.BoardColor);
        RefreshBoardList(b);
    }

    private void RenameBoard(Board b)
    {
        var name = Prompt("Rename board", "Board name:", b.Name);
        if (name == null || name.Length == 0) return;
        b.Name = name;
        BoardStore.Touch(b);
        RefreshBoardList(CurrentBoard, keepWeek: true);
    }

    private void EditBoard(Board b)
    {
        if (new BoardDialog(b) { Owner = this }.ShowDialog() != true) return;
        RefreshBoardList(CurrentBoard, keepWeek: true);
        RebuildLabelFilter();
    }

    private void DeleteBoard(Board b)
    {
        int cards = b.AllCards.Count();
        if (!Confirm("Delete board", $"Delete the board \"{b.Name}\" and its {cards} card{(cards == 1 ? "" : "s")}? You can undo this with Ctrl+Z.")) return;
        int i = BoardStore.Boards.IndexOf(b);
        BoardStore.DeleteBoard(b);
        var next = BoardStore.Boards.Count == 0 ? null : BoardStore.Boards[Math.Clamp(i, 0, BoardStore.Boards.Count - 1)];
        if (BoardStore.Boards.Count == 0) WeekShowing = false;
        RefreshBoardList(b == CurrentBoard ? next : CurrentBoard, keepWeek: true);
    }

    private void ShowArchived(Board b)
    {
        new ArchivedDialog(b) { Owner = this }.ShowDialog();
        Render();
    }

    private async Task ImportIssues(Board b)
    {
        if (!GitHubService.TryParseRepo(b.GitHubRepo, out var owner, out var repo))
        {
            var text = Prompt("Import GitHub issues", "GitHub repository (owner/name or its web address):", b.GitHubRepo,
                "For example adamglassner/boardwalk. It's saved in Board settings for next time.");
            if (text == null) return;
            if (!GitHubService.TryParseRepo(text, out owner, out repo))
            {
                MessageDialog.Show(this, "Import GitHub issues", "That doesn't look like a GitHub repository. Use owner/name, like adamglassner/boardwalk.", MessageDialog.Kind.Warning);
                return;
            }
            b.GitHubRepo = $"{owner}/{repo}";
            BoardStore.Touch(b);
        }
        List<GitHubIssue> issues;
        try
        {
            Toast($"Getting issues from {owner}/{repo}…");
            Cursor = Cursors.Wait;
            issues = await GitHubService.GetOpenIssues(owner, repo);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, "Import GitHub issues", ex.Message, MessageDialog.Kind.Error);
            return;
        }
        finally { Cursor = null; }
        var d = new ImportIssuesDialog(b, $"{owner}/{repo}", issues) { Owner = this };
        if (d.ShowDialog() != true) return;
        int n = BoardStore.ImportIssues(b, d.Selected);
        Render();
        Toast(n == 0 ? "No new issues to add" : $"Added {n} card{(n == 1 ? "" : "s")} from GitHub");
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
        if (name == null || name.Length == 0) return;
        col.Title = name;
        BoardStore.Touch(b);
        Render();
    }

    private void SetLimit(Board b, Column col)
    {
        var text = Prompt("Card limit", $"Most cards allowed in \"{col.Title}\" at once:", col.WipLimit > 0 ? col.WipLimit.ToString() : "",
            "Leave empty or enter 0 for no limit. The count turns orange when there are more cards than this.", allowEmpty: true);
        if (text == null) return;
        col.WipLimit = int.TryParse(text, out int n) ? Math.Clamp(n, 0, 999) : 0;
        BoardStore.Touch(b);
        Render();
    }

    private void SortColumn<T>(Board b, Column col, Func<Card, T> key)
    {
        col.Cards = col.Cards.OrderBy(key).ToList();
        BoardStore.Touch(b);
        Render();
    }

    private void ArchiveColumn(Board b, Column col)
    {
        if (!Confirm("Archive cards", $"Archive all {col.ActiveCount} cards in \"{col.Title}\"? You can bring them back from Board options › Archived cards.")) return;
        BoardStore.ArchiveColumn(b, col);
        Render();
    }

    private void DeleteColumn(Board b, Column col)
    {
        int n = col.Cards.Count;
        if (n > 0 && !Confirm("Delete column", $"Delete \"{col.Title}\" and its {n} card{(n == 1 ? "" : "s")}? You can undo this with Ctrl+Z.")) return;
        BoardStore.DeleteColumn(b, col);
        Render();
    }

    internal void OpenCard(Board b, Card card)
    {
        new CardDialog(b, card) { Owner = this }.ShowDialog();
        Render();
    }

    private void DeleteCard(Board b, Card card)
    {
        if (!Confirm("Delete card", $"Delete \"{card.Title}\"? You can undo this with Ctrl+Z, or archive the card instead.")) return;
        BoardStore.DeleteCard(b, card);
        Render();
    }

    private bool Confirm(string title, string message) =>
        !SettingsService.Current.ConfirmDeletes || MessageDialog.Ask(this, title, message);

    private string Prompt(string title, string label, string initial, string hint = null, bool allowEmpty = false)
    {
        var d = new TextPromptDialog(title, label, initial, hint) { AllowEmpty = allowEmpty, Owner = this };
        return d.ShowDialog() == true ? d.Value : null;
    }

    // ---------------- undo ----------------

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoRedo(undo: true);
    private void Redo_Click(object sender, RoutedEventArgs e) => UndoRedo(undo: false);

    internal bool UndoRedo(bool undo)
    {
        var id = CurrentBoard?.Id;
        if (!(undo ? BoardStore.Undo() : BoardStore.Redo())) { Toast(undo ? "Nothing to undo" : "Nothing to redo"); return false; }
        var b = BoardStore.Boards.FirstOrDefault(x => x.Id == id) ?? BoardStore.Boards.FirstOrDefault();
        if (BoardStore.Boards.Count == 0) WeekShowing = false;
        _addingKey = null;
        RefreshBoardList(b, keepWeek: true);
        RebuildLabelFilter();
        Toast(undo ? "Undone" : "Redone");
        return true;
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

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(this) { Owner = this }.ShowDialog();

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
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        bool inText = Keyboard.FocusedElement is TextBox;
        if (ctrl && shift && key == System.Windows.Input.Key.N) { e.Handled = true; NewBoard(); }
        else if (ctrl && shift && key == System.Windows.Input.Key.W && BoardStore.Boards.Count > 0) { e.Handled = true; ShowWeek(); }
        else if (ctrl && !inText && (key == System.Windows.Input.Key.Z && !shift)) { e.Handled = true; UndoRedo(true); }
        else if (ctrl && !inText && (key == System.Windows.Input.Key.Y || (key == System.Windows.Input.Key.Z && shift))) { e.Handled = true; UndoRedo(false); }
        else if (ctrl && !shift && key == System.Windows.Input.Key.N && CurrentBoard?.Columns.Count > 0)
        {
            e.Handled = true;
            if (WeekShowing) ShowBoard(CurrentBoard);
            var col = CurrentBoard.FirstOpenColumn;
            _addingKey = Key(col, CurrentBoard.Lanes == LaneMode.None ? null : LanesOf(CurrentBoard).Last().Key);
            Render();
        }
        else if (ctrl && key == System.Windows.Input.Key.F && CurrentBoard != null)
        {
            e.Handled = true;
            if (WeekShowing) ShowBoard(CurrentBoard);
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
        else if (ctrl && key == System.Windows.Input.Key.OemComma) { e.Handled = true; new SettingsWindow(this) { Owner = this }.ShowDialog(); }
        else if (key == System.Windows.Input.Key.F2 && CurrentBoard != null && !inText && !WeekShowing) { e.Handled = true; RenameBoard(CurrentBoard); }
        else if (ctrl && key == System.Windows.Input.Key.Tab && BoardStore.Boards.Count > 1)
        {
            e.Handled = true;
            int i = BoardStore.Boards.IndexOf(CurrentBoard) + (shift ? -1 : 1);
            var b = BoardStore.Boards[(i + BoardStore.Boards.Count) % BoardStore.Boards.Count];
            SelectInList(b);
            ShowBoard(b);
        }
        else if (key == System.Windows.Input.Key.Escape && Keyboard.FocusedElement == SearchBox && SearchBox.Text.Length > 0) { e.Handled = true; SearchBox.Text = ""; }
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
        if (App.IsSelfTest || !IsLoaded) return;
        var s = SettingsService.Current;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty) { s.WinLeft = r.Left; s.WinTop = r.Top; s.WinWidth = r.Width; s.WinHeight = r.Height; }
        s.WinMaximized = WindowState == WindowState.Maximized;
        SettingsService.Save();
        BoardStore.Save();
    }
}
