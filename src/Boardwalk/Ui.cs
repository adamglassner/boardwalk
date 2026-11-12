using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Boardwalk.Services;

namespace Boardwalk;

/// <summary>Small builders shared by the main window and the dialogs.</summary>
internal static class Ui
{
    private static readonly Dictionary<string, SolidColorBrush> _brushes = new();

    public static SolidColorBrush Brush(string hex)
    {
        if (_brushes.TryGetValue(hex, out var b)) return b;
        b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        _brushes[hex] = b;
        return b;
    }

    public static MenuItem Item(string header, Action onClick, string glyph = null, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        if (glyph != null) m.Icon = glyph;
        if (onClick != null) m.Click += (_, e) => { e.Handled = true; onClick(); };
        return m;
    }

    public static void Open(FrameworkElement target, ContextMenu menu)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    public static StackPanel IconText(string glyph, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Icon"), Text = glyph, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
        });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    /// <summary>A small icon + text pair on a card (due date, checklist...), coloured by a theme brush key.</summary>
    public static StackPanel Meta(string glyph, string text, string brushKey)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
        if (glyph != null)
        {
            var icon = new TextBlock
            {
                Style = (Style)Application.Current.FindResource("Icon"), Text = glyph, FontSize = 12,
                Margin = new Thickness(0, 0, text.Length > 0 ? 4 : 0, 0),
            };
            icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            sp.Children.Add(icon);
        }
        if (text.Length > 0)
        {
            var t = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            sp.Children.Add(t);
        }
        return sp;
    }

    public static string PriorityColor(Priority p) => p switch
    {
        Priority.Urgent => "Red",
        Priority.High => "Orange",
        Priority.Medium => "Yellow",
        _ => "Gray",
    };

    public static StackPanel PriorityBadge(Priority p)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), ToolTip = p + " priority" };
        var hex = BoardStore.Hex(PriorityColor(p));
        sp.Children.Add(new TextBlock
        {
            Style = (Style)Application.Current.FindResource("Icon"), Text = "", FontSize = 11,
            Foreground = Brush(hex), Margin = new Thickness(0, 0, 4, 0),
        });
        sp.Children.Add(new TextBlock { Text = p.ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush(hex), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    public static Border LabelPill(LabelDef l, bool selected = true)
    {
        var hex = BoardStore.Hex(l.Color);
        var text = new TextBlock { Text = l.Name, FontSize = 11.5, FontWeight = FontWeights.SemiBold };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 4, 4),
            BorderThickness = new Thickness(1.5), BorderBrush = Brush(hex), Child = text,
        };
        if (selected) { pill.Background = Brush(hex); text.Foreground = Brushes.White; }
        else { pill.Background = Brushes.Transparent; text.Foreground = Brush(hex); }
        return pill;
    }

    public static string FriendlyDate(DateTime d)
    {
        int days = (d.Date - DateTime.Today).Days;
        return days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => d.ToString(d.Year == DateTime.Today.Year ? "MMM d" : "MMM d, yyyy"),
        };
    }

    public static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "Board" : name.Trim();
    }

    /// <summary>A row of colour swatches; the chosen one shows a tick.</summary>
    public static WrapPanel Swatches(string selected, Action<string> onPick)
    {
        var wp = new WrapPanel();
        void Build(string sel)
        {
            wp.Children.Clear();
            foreach (var (name, hex) in BoardStore.Palette)
            {
                var btn = new Button
                {
                    Style = (Style)Application.Current.FindResource("SwatchButton"), Width = 28, Height = 28,
                    Margin = new Thickness(0, 0, 8, 8), Background = Brush(hex), BorderThickness = new Thickness(0), ToolTip = name,
                };
                if (name == sel)
                    btn.Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), Foreground = Brushes.White, FontSize = 13 };
                var n = name;
                btn.Click += (_, _) => { Build(n); onPick(n); };
                wp.Children.Add(btn);
            }
        }
        Build(selected);
        return wp;
    }
}
