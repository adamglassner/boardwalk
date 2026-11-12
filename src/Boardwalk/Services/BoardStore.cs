using System.IO;
using System.Text;
using System.Text.Json;

namespace Boardwalk.Services;

public enum Priority { None, Low, Medium, High, Urgent }

public sealed class LabelDef
{
    public string Id { get; set; } = BoardStore.NewId();
    public string Name { get; set; } = "";
    /// <summary>One of <see cref="BoardStore.Palette"/>'s names.</summary>
    public string Color { get; set; } = "Blue";
}

public sealed class CheckItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

public sealed class Card
{
    public string Id { get; set; } = BoardStore.NewId();
    /// <summary>Board-wide number shown as #12, so a card can be referred to in commits and notes.</summary>
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>A web link (an issue, pull request, document...).</summary>
    public string Link { get; set; } = "";
    public List<string> LabelIds { get; set; } = new();
    public Priority Priority { get; set; } = Priority.None;
    public DateTime? Due { get; set; }
    public List<CheckItem> Checklist { get; set; } = new();
    public bool Archived { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    public Card Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsService.Options);
        var c = JsonSerializer.Deserialize<Card>(json, SettingsService.Options);
        c.Id = BoardStore.NewId();
        return c;
    }
}

public sealed class Column
{
    public string Id { get; set; } = BoardStore.NewId();
    public string Title { get; set; } = "";
    /// <summary>Work-in-progress limit; 0 = no limit.</summary>
    public int WipLimit { get; set; }
    /// <summary>Status colour, one of <see cref="BoardStore.Palette"/>'s names.</summary>
    public string Color { get; set; } = "Gray";
    /// <summary>A "finished" status (Complete, Done, Cancelled...): its cards get a tick and are never overdue.</summary>
    public bool IsDone { get; set; }
    public List<Card> Cards { get; set; } = new();

    public int ActiveCount => Cards.Count(c => !c.Archived);
    public bool OverLimit => WipLimit > 0 && ActiveCount > WipLimit;
}

public sealed class Board
{
    public string Id { get; set; } = BoardStore.NewId();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Color { get; set; } = "Blue";
    public List<Column> Columns { get; set; } = new();
    public List<LabelDef> Labels { get; set; } = new();
    public int NextCardNumber { get; set; } = 1;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    public IEnumerable<Card> AllCards => Columns.SelectMany(c => c.Cards);
    public int ActiveCardCount => AllCards.Count(c => !c.Archived);

    public Column ColumnOf(Card card) => Columns.FirstOrDefault(c => c.Cards.Contains(card));
    public LabelDef Label(string id) => Labels.FirstOrDefault(l => l.Id == id);
}

public sealed class BoardFile
{
    public int Version { get; set; } = 1;
    public List<Board> Boards { get; set; } = new();
}

public enum BoardTemplate { Standard, SoftwareProject, Personal, Blank }

/// <summary>A ready-made status for the "Add column" menu.</summary>
public sealed record StatusPreset(string Title, string Color, bool IsDone = false);

/// <summary>
/// All boards live in one file, %APPDATA%\Boardwalk\boards.json, saved after every change.
/// Saves are crash-safe (write a temp file, then swap) and keep the previous version as boards.json.bak.
/// </summary>
public static class BoardStore
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Boardwalk");

    public static string FilePath => Path.Combine(Folder, "boards.json");
    public static string BackupPath => FilePath + ".bak";

    public static BoardFile Data { get; private set; } = new();
    public static List<Board> Boards => Data.Boards;

    /// <summary>Set when boards.json couldn't be read and the backup was used instead.</summary>
    public static string LoadWarning { get; private set; }

    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Blue", "#2563EB"), ("Teal", "#0E9384"), ("Green", "#16A34A"), ("Yellow", "#CA8A04"),
        ("Orange", "#EA580C"), ("Red", "#DC2626"), ("Pink", "#DB2777"), ("Purple", "#7C3AED"),
        ("Gray", "#64748B"),
    };

    public static string Hex(string colorName) =>
        Palette.FirstOrDefault(p => p.Name == colorName).Hex ?? Palette[0].Hex;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static void Load()
    {
        LoadWarning = null;
        Data = TryRead(FilePath);
        if (Data == null)
        {
            var hadFile = File.Exists(FilePath);
            Data = TryRead(BackupPath);
            if (hadFile)
            {
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
                LoadWarning = Data != null
                    ? "Your boards file couldn't be read, so Boardwalk opened the last backup. The damaged file was kept as boards.json.bad."
                    : "Your boards file couldn't be read. It was kept as boards.json.bad and Boardwalk started with no boards.";
            }
            Data ??= new BoardFile();
        }
        Sanitize();
    }

    private static BoardFile TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BoardFile>(File.ReadAllText(path), SettingsService.Options);
        }
        catch { return null; }
    }

    private static void Sanitize()
    {
        Data.Boards ??= new();
        Data.Boards.RemoveAll(b => b == null);
        foreach (var b in Data.Boards)
        {
            b.Columns ??= new();
            b.Labels ??= new();
            b.Name ??= "Board";
            b.Description ??= "";
            b.Columns.RemoveAll(c => c == null);
            int max = 0;
            foreach (var col in b.Columns)
            {
                col.Cards ??= new();
                col.Cards.RemoveAll(c => c == null);
                col.Title ??= "";
                col.Color ??= "Gray";
                foreach (var c in col.Cards)
                {
                    c.LabelIds ??= new();
                    c.Checklist ??= new();
                    c.Title ??= "";
                    c.Notes ??= "";
                    c.Link ??= "";
                    max = Math.Max(max, c.Number);
                }
            }
            foreach (var c in b.AllCards.Where(c => c.Number <= 0)) c.Number = ++max;
            b.NextCardNumber = Math.Max(b.NextCardNumber, max + 1);
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, SettingsService.Options));
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Folder, "error.log"), $"[{DateTime.Now:u}] Save failed: {ex}\r\n"); } catch { }
        }
    }

    // ---------------- boards ----------------

    public static Board CreateBoard(string name, BoardTemplate template, string color = "Blue")
    {
        var b = new Board { Name = string.IsNullOrWhiteSpace(name) ? "New board" : name.Trim(), Color = color };
        switch (template)
        {
            case BoardTemplate.Standard:
                AddStatuses(b, "New", "In progress", "On hold", "Complete");
                break;
            case BoardTemplate.SoftwareProject:
                AddStatuses(b, "Backlog", "To do", "In progress", "On hold", "Review", "Complete");
                b.Columns[2].WipLimit = 3;
                b.Labels.AddRange(new[]
                {
                    new LabelDef { Name = "Bug", Color = "Red" },
                    new LabelDef { Name = "Feature", Color = "Blue" },
                    new LabelDef { Name = "Improvement", Color = "Teal" },
                    new LabelDef { Name = "Design", Color = "Purple" },
                    new LabelDef { Name = "Docs", Color = "Gray" },
                });
                break;
            case BoardTemplate.Personal:
                AddStatuses(b, "Someday", "This week", "Today", "Complete");
                break;
        }
        if (b.Labels.Count == 0)
        {
            b.Labels.AddRange(new[]
            {
                new LabelDef { Name = "Important", Color = "Red" },
                new LabelDef { Name = "Idea", Color = "Yellow" },
                new LabelDef { Name = "Waiting", Color = "Gray" },
            });
        }
        Boards.Add(b);
        Save();
        return b;
    }

    /// <summary>Statuses offered by "Add column". Any other name can be typed too.</summary>
    public static readonly StatusPreset[] StatusPresets =
    {
        new("New", "Gray"), new("Backlog", "Gray"), new("To do", "Purple"), new("In progress", "Blue"),
        new("On hold", "Orange"), new("Blocked", "Red"), new("Review", "Pink"), new("Testing", "Teal"),
        new("Complete", "Green", true), new("Cancelled", "Gray", true),
    };

    public static StatusPreset PresetFor(string title) =>
        StatusPresets.FirstOrDefault(p => p.Title.Equals(title?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static void AddStatuses(Board b, params string[] titles)
    {
        foreach (var t in titles)
        {
            var p = PresetFor(t);
            b.Columns.Add(new Column { Title = t, Color = p?.Color ?? "Blue", IsDone = p?.IsDone ?? false });
        }
    }

    public static Board DuplicateBoard(Board src, bool includeCards)
    {
        var json = JsonSerializer.Serialize(src, SettingsService.Options);
        var b = JsonSerializer.Deserialize<Board>(json, SettingsService.Options);
        b.Id = NewId();
        b.Name = src.Name + " (copy)";
        b.Created = b.Modified = DateTime.Now;
        foreach (var col in b.Columns)
        {
            col.Id = NewId();
            if (!includeCards) col.Cards.Clear();
            foreach (var c in col.Cards) c.Id = NewId();
        }
        if (!includeCards) b.NextCardNumber = 1;
        Boards.Insert(Boards.IndexOf(src) + 1, b);
        Save();
        return b;
    }

    public static void DeleteBoard(Board b)
    {
        Boards.Remove(b);
        Save();
    }

    public static void MoveBoard(Board b, int delta)
    {
        int i = Boards.IndexOf(b), j = i + delta;
        if (i < 0 || j < 0 || j >= Boards.Count) return;
        Boards.RemoveAt(i);
        Boards.Insert(j, b);
        Save();
    }

    // ---------------- cards ----------------

    public static Card AddCard(Board b, Column col, string title, int index = -1)
    {
        var c = new Card { Title = title.Trim(), Number = b.NextCardNumber++ };
        if (index < 0 || index > col.Cards.Count) col.Cards.Add(c); else col.Cards.Insert(index, c);
        Touch(b);
        return c;
    }

    /// <summary>Moves a card to <paramref name="target"/> at <paramref name="index"/> (an index among that column's cards before the move).</summary>
    public static void MoveCard(Board b, Card card, Column target, int index)
    {
        var from = b.ColumnOf(card);
        if (from == null) return;
        int old = from.Cards.IndexOf(card);
        from.Cards.RemoveAt(old);
        if (from == target && index > old) index--;
        index = Math.Clamp(index < 0 ? target.Cards.Count : index, 0, target.Cards.Count);
        target.Cards.Insert(index, card);
        card.Modified = DateTime.Now;
        Touch(b);
    }

    /// <summary>Moves a card to another board's first column (or a chosen one); it gets a number on the new board.</summary>
    public static void MoveCardToBoard(Board from, Card card, Board to, Column target = null)
    {
        target ??= to.Columns.FirstOrDefault();
        if (target == null) return;
        from.ColumnOf(card)?.Cards.Remove(card);
        // Keep labels that exist on the new board by name; add the rest.
        var ids = new List<string>();
        foreach (var id in card.LabelIds)
        {
            var l = from.Label(id);
            if (l == null) continue;
            var match = to.Labels.FirstOrDefault(x => x.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase));
            if (match == null) { match = new LabelDef { Name = l.Name, Color = l.Color }; to.Labels.Add(match); }
            ids.Add(match.Id);
        }
        card.LabelIds = ids;
        card.Number = to.NextCardNumber++;
        target.Cards.Add(card);
        Touch(from);
        Touch(to);
    }

    public static void DeleteCard(Board b, Card card)
    {
        b.ColumnOf(card)?.Cards.Remove(card);
        Touch(b);
    }

    public static Card DuplicateCard(Board b, Card card)
    {
        var col = b.ColumnOf(card);
        var c = card.Clone();
        c.Number = b.NextCardNumber++;
        c.Title = card.Title + " (copy)";
        c.Created = c.Modified = DateTime.Now;
        col.Cards.Insert(col.Cards.IndexOf(card) + 1, c);
        Touch(b);
        return c;
    }

    public static int ArchiveColumn(Board b, Column col)
    {
        int n = 0;
        foreach (var c in col.Cards.Where(c => !c.Archived)) { c.Archived = true; n++; }
        Touch(b);
        return n;
    }

    // ---------------- columns ----------------

    public static Column AddColumn(Board b, string title)
    {
        title = string.IsNullOrWhiteSpace(title) ? "New column" : title.Trim();
        var p = PresetFor(title);
        var col = new Column { Title = title, Color = p?.Color ?? "Blue", IsDone = p?.IsDone ?? false };
        b.Columns.Add(col);
        Touch(b);
        return col;
    }

    public static void MoveColumn(Board b, Column col, int delta)
    {
        int i = b.Columns.IndexOf(col), j = i + delta;
        if (i < 0 || j < 0 || j >= b.Columns.Count) return;
        b.Columns.RemoveAt(i);
        b.Columns.Insert(j, col);
        Touch(b);
    }

    public static void DeleteColumn(Board b, Column col)
    {
        b.Columns.Remove(col);
        Touch(b);
    }

    public static void Touch(Board b)
    {
        b.Modified = DateTime.Now;
        Save();
    }

    // ---------------- import / export ----------------

    public static string ToMarkdown(Board b)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {b.Name}");
        if (!string.IsNullOrWhiteSpace(b.Description)) sb.AppendLine().AppendLine(b.Description.Trim());
        foreach (var col in b.Columns)
        {
            sb.AppendLine().AppendLine($"## {col.Title}").AppendLine();
            var cards = col.Cards.Where(c => !c.Archived).ToList();
            if (cards.Count == 0) { sb.AppendLine("_No cards_"); continue; }
            foreach (var c in cards)
            {
                var extra = new List<string>();
                var labels = c.LabelIds.Select(b.Label).Where(l => l != null).Select(l => l.Name).ToList();
                if (labels.Count > 0) extra.Add(string.Join(", ", labels));
                if (c.Priority != Priority.None) extra.Add(c.Priority + " priority");
                if (c.Due is DateTime d) extra.Add("due " + d.ToString("yyyy-MM-dd"));
                if (c.Checklist.Count > 0) extra.Add($"{c.Checklist.Count(i => i.Done)}/{c.Checklist.Count} done");
                var title = string.IsNullOrWhiteSpace(c.Link) ? c.Title : $"[{c.Title}]({c.Link})";
                sb.Append($"- [{(col.IsDone ? "x" : " ")}] #{c.Number} {title}");
                if (extra.Count > 0) sb.Append($" ({string.Join("; ", extra)})");
                sb.AppendLine();
                foreach (var item in c.Checklist) sb.AppendLine($"  - [{(item.Done ? "x" : " ")}] {item.Text}");
            }
        }
        return sb.ToString();
    }

    public static void ExportBoard(Board b, string path)
    {
        var json = JsonSerializer.Serialize(new BoardFile { Boards = { b } }, SettingsService.Options);
        File.WriteAllText(path, json);
    }

    /// <summary>Imports boards from a Boardwalk .json export or backup. Returns the boards added.</summary>
    public static List<Board> ImportBoards(string path)
    {
        var file = JsonSerializer.Deserialize<BoardFile>(File.ReadAllText(path), SettingsService.Options);
        var added = new List<Board>();
        foreach (var b in file?.Boards ?? new List<Board>())
        {
            if (b == null) continue;
            if (Boards.Any(x => x.Id == b.Id)) b.Id = NewId();
            Boards.Add(b);
            added.Add(b);
        }
        Sanitize();
        Save();
        return added;
    }
}
