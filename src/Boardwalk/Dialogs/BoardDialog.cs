using System.Windows;
using System.Windows.Controls;
using Boardwalk.Services;

namespace Boardwalk.Dialogs;

/// <summary>A board's name, description, colour and labels.</summary>
public sealed class BoardDialog : DialogBase
{
    private readonly Board _board;
    private readonly TextBox _name, _desc;
    private string _color;
    private readonly List<LabelDef> _labels;
    private readonly StackPanel _labelRows = new();

    public BoardDialog(Board board) : base("Board settings", 500)
    {
        _board = board;
        _color = board.Color;
        // Edit copies so Cancel leaves the board untouched.
        _labels = board.Labels.Select(l => new LabelDef { Id = l.Id, Name = l.Name, Color = l.Color }).ToList();

        var panel = new StackPanel();
        panel.Children.Add(Label("Name:"));
        _name = new TextBox { Text = board.Name, Width = 440, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_name);

        panel.Children.Add(Label("Description (optional):"));
        _desc = new TextBox
        {
            Text = board.Description, Width = 440, Height = 64, HorizontalAlignment = HorizontalAlignment.Left,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        panel.Children.Add(_desc);

        panel.Children.Add(Header("Colour"));
        panel.Children.Add(Ui.Swatches(_color, c => _color = c));

        panel.Children.Add(Header("Labels"));
        panel.Children.Add(Hint("Labels tag cards on this board, for example Bug, Feature or Waiting. Click a colour to change it."));
        panel.Children.Add(_labelRows);
        var add = new Button { Content = Ui.IconText("", "Add label"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8) };
        add.Click += (_, _) =>
        {
            var used = _labels.Select(l => l.Color).ToHashSet();
            var color = BoardStore.Palette.Select(p => p.Name).FirstOrDefault(n => !used.Contains(n)) ?? "Blue";
            _labels.Add(new LabelDef { Name = "New label", Color = color });
            BuildLabels(focusLast: true);
        };
        panel.Children.Add(add);
        BuildLabels();

        Body.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 600, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });

        AddButton("Save", primary: true, onClick: Save);
        AddButton("Cancel", cancel: true);
        Loaded += (_, _) => _name.Focus();
    }

    private void BuildLabels(bool focusLast = false)
    {
        _labelRows.Children.Clear();
        TextBox last = null;
        foreach (var l in _labels)
        {
            var label = l;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), Width = 440, HorizontalAlignment = HorizontalAlignment.Left };
            var swatch = new Button
            {
                Style = (Style)FindResource("SwatchButton"), Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0),
                Background = Ui.Brush(BoardStore.Hex(l.Color)), BorderThickness = new Thickness(0), ToolTip = "Change colour",
            };
            swatch.Click += (_, _) =>
            {
                var m = new ContextMenu();
                foreach (var (name, hex) in BoardStore.Palette)
                {
                    var n = name;
                    var it = Ui.Item(name, () => { label.Color = n; BuildLabels(); });
                    it.Icon = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Ui.Brush(hex) };
                    m.Items.Add(it);
                }
                Ui.Open(swatch, m);
            };
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);
            var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", ToolTip = "Delete label (it's removed from cards too)" };
            del.Click += (_, _) => { _labels.Remove(label); BuildLabels(); };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            int uses = _board.AllCards.Count(c => c.LabelIds.Contains(l.Id));
            var count = new TextBlock { Text = uses == 1 ? "1 card" : $"{uses} cards", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), Style = (Style)FindResource("Hint") };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            var box = new TextBox { Text = l.Name };
            box.TextChanged += (_, _) => label.Name = box.Text;
            row.Children.Add(box);
            _labelRows.Children.Add(row);
            last = box;
        }
        if (focusLast && last != null) last.Loaded += (_, _) => { last.Focus(); last.SelectAll(); };
    }

    private void Save()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) { _name.Focus(); return; }
        _board.Name = name;
        _board.Description = _desc.Text.Trim();
        _board.Color = _color;
        foreach (var l in _labels) l.Name = string.IsNullOrWhiteSpace(l.Name) ? "Label" : l.Name.Trim();
        var keep = _labels.Select(l => l.Id).ToHashSet();
        foreach (var c in _board.AllCards) c.LabelIds.RemoveAll(id => !keep.Contains(id));
        _board.Labels = _labels;
        BoardStore.Touch(_board);
        DialogResult = true;
    }
}
