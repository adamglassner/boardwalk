using System.Windows;
using System.Windows.Controls;
using Boardwalk.Services;

namespace Boardwalk.Dialogs;

/// <summary>Asks for one line of text.</summary>
public sealed class TextPromptDialog : DialogBase
{
    private readonly TextBox _box;
    public string Value => _box.Text.Trim();
    public bool AllowEmpty { get; set; }

    public TextPromptDialog(string title, string label, string initial, string hint = null) : base(title, 420)
    {
        Body.Children.Add(Label(label));
        _box = new TextBox { Text = initial ?? "", Width = 380, HorizontalAlignment = HorizontalAlignment.Left };
        Body.Children.Add(_box);
        if (hint != null)
        {
            var h = Hint(hint);
            h.MaxWidth = 380;
            Body.Children.Add(h);
        }
        else _box.Margin = new Thickness(0, 0, 0, 12);
        AddButton("OK", primary: true, onClick: () => { if (AllowEmpty || Value.Length > 0) DialogResult = true; });
        AddButton("Cancel", cancel: true);
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}

/// <summary>Name, starting columns and colour for a new board.</summary>
public sealed class NewBoardDialog : DialogBase
{
    private readonly TextBox _name;
    public string BoardName => _name.Text.Trim();
    public BoardTemplate Template { get; private set; } = BoardTemplate.Standard;
    public string BoardColor { get; private set; } = "Blue";

    private static readonly (BoardTemplate T, string Name, string Text)[] Templates =
    {
        (BoardTemplate.Standard, "Standard", "New, In progress, On hold and Complete"),
        (BoardTemplate.SoftwareProject, "Software project", "Backlog, To do, In progress, On hold, Review and Complete, with Bug, Feature, Improvement, Design and Docs labels"),
        (BoardTemplate.Personal, "Personal", "Someday, This week, Today and Complete"),
        (BoardTemplate.Blank, "Blank", "No columns yet; add your own"),
    };

    public NewBoardDialog() : base("New board", 460)
    {
        Body.Children.Add(Label("Board name:"));
        _name = new TextBox { Width = 420, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
        Body.Children.Add(_name);
        Body.Children.Add(Hint("For example the name of a project. You can make as many boards as you like."));

        Body.Children.Add(Header("Start with"));
        foreach (var (t, name, text) in Templates)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
            var hint = Hint(text);
            hint.Margin = new Thickness(0, 1, 0, 0);
            hint.MaxWidth = 380;
            sp.Children.Add(hint);
            var rb = new RadioButton { Content = sp, GroupName = "tpl", IsChecked = t == BoardTemplate.Standard, Margin = new Thickness(0, 0, 0, 8) };
            var tt = t;
            rb.Checked += (_, _) => Template = tt;
            Body.Children.Add(rb);
        }

        Body.Children.Add(Header("Colour"));
        Body.Children.Add(Ui.Swatches(BoardColor, c => BoardColor = c));

        AddButton("Create board", primary: true, onClick: () =>
        {
            if (BoardName.Length == 0) { _name.Focus(); return; }
            DialogResult = true;
        });
        AddButton("Cancel", cancel: true);
        Loaded += (_, _) => _name.Focus();
    }
}

/// <summary>Cards that were archived on a board, with Restore and Delete.</summary>
public sealed class ArchivedDialog : DialogBase
{
    private readonly Board _board;
    private readonly StackPanel _list = new();

    public ArchivedDialog(Board board) : base("Archived cards", 520)
    {
        _board = board;
        Body.Children.Add(Hint("Archived cards are hidden from the board but kept. Restore puts a card back in its column."));
        Body.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 8) });
        Build();
        AddButton("Close", primary: true);
    }

    internal int Count => _board.AllCards.Count(c => c.Archived);

    private void Build()
    {
        _list.Children.Clear();
        foreach (var col in _board.Columns)
            foreach (var card in col.Cards.Where(c => c.Archived).ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var del = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0), MinWidth = 0 };
                var restore = new Button { Content = "Restore", MinWidth = 0 };
                DockPanel.SetDock(del, Dock.Right);
                DockPanel.SetDock(restore, Dock.Right);
                row.Children.Add(del);
                row.Children.Add(restore);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = $"#{card.Number}  {card.Title}", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 });
                var h = Hint("in " + col.Title);
                h.Margin = new Thickness(0);
                text.Children.Add(h);
                row.Children.Add(text);
                var c = card;
                restore.Click += (_, _) => { c.Archived = false; BoardStore.Touch(_board); Build(); };
                del.Click += (_, _) =>
                {
                    if (SettingsService.Current.ConfirmDeletes && !MessageDialog.Ask(this, "Delete card", $"Delete \"{c.Title}\" for good?")) return;
                    BoardStore.DeleteCard(_board, c);
                    Build();
                };
                _list.Children.Add(row);
            }
        if (_list.Children.Count == 0)
            _list.Children.Add(new TextBlock { Text = "No archived cards on this board.", Margin = new Thickness(0, 6, 0, 6) });
    }
}
