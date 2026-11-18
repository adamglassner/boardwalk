using System.IO;
using System.Text;
using System.Text.Json;

namespace Boardwalk.Services;

public enum Priority { None, Low, Medium, High, Urgent }

/// <summary>How a card repeats. When a repeating card is completed, the next one is made automatically.</summary>
public enum RepeatKind { None, Daily, Weekly, Monthly, Yearly }

/// <summary>Swimlanes: rows across the board.</summary>
public enum LaneMode { None, Priority, Label }

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

public sealed class HistoryEntry
{
    public DateTime When { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    /// <summary>The status the card entered at this point, if it moved (used for time-in-status and stats).</summary>
    public string ColumnId { get; set; } = "";
}

/// <summary>A starting point for new cards, such as a bug report.</summary>
public sealed class CardTemplate
{
    public string Id { get; set; } = BoardStore.NewId();
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> LabelIds { get; set; } = new();
    public Priority Priority { get; set; }
    public List<string> Checklist { get; set; } = new();
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
    /// <summary>Last known state of a GitHub link: open, closed or merged ("" = unknown).</summary>
    public string LinkState { get; set; } = "";
    public DateTime? LinkChecked { get; set; }
    public List<string> LabelIds { get; set; } = new();
    public Priority Priority { get; set; } = Priority.None;
    public DateTime? Due { get; set; }
    public RepeatKind Repeat { get; set; } = RepeatKind.None;
    /// <summary>Release this card belongs to, e.g. 1.2.0.</summary>
    public string Version { get; set; } = "";
    public List<CheckItem> Checklist { get; set; } = new();
    /// <summary>Ids of cards that must be completed before this one.</summary>
    public List<string> BlockedBy { get; set; } = new();
    /// <summary>Id of the card this is a sub-task of ("" = none).</summary>
    public string ParentId { get; set; } = "";
    /// <summary>File names in the attachments folder.</summary>
    public List<string> Attachments { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
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
    public List<CardTemplate> Templates { get; set; } = new();
    public int NextCardNumber { get; set; } = 1;
    public LaneMode Lanes { get; set; } = LaneMode.None;
    /// <summary>GitHub repository as owner/name, for importing issues ("" = none).</summary>
    public string GitHubRepo { get; set; } = "";
    /// <summary>Close a card's GitHub issue when the card is completed (needs a token).</summary>
    public bool CloseIssuesOnComplete { get; set; } = true;
    /// <summary>The release being worked on, e.g. 1.2.0.</summary>
    public string NextVersion { get; set; } = "";
    /// <summary>Completed cards without a version get <see cref="NextVersion"/>.</summary>
    public bool AutoVersion { get; set; } = true;
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;

    public IEnumerable<Card> AllCards => Columns.SelectMany(c => c.Cards);
    public int ActiveCardCount => AllCards.Count(c => !c.Archived);

    public Column ColumnOf(Card card) => Columns.FirstOrDefault(c => c.Cards.Contains(card));
    public LabelDef Label(string id) => Labels.FirstOrDefault(l => l.Id == id);
    public Card CardById(string id) => string.IsNullOrEmpty(id) ? null : AllCards.FirstOrDefault(c => c.Id == id);
    public Card CardByNumber(int n) => AllCards.FirstOrDefault(c => c.Number == n);
    public bool IsComplete(Card c) => ColumnOf(c)?.IsDone == true;
    public IEnumerable<Card> Children(Card c) => AllCards.Where(x => x.ParentId == c.Id);
    /// <summary>Blockers that aren't completed yet.</summary>
    public List<Card> OpenBlockers(Card c) =>
        c.BlockedBy.Select(CardById).Where(x => x != null && !IsComplete(x) && !x.Archived).ToList();
    public Column FirstOpenColumn => Columns.FirstOrDefault(c => !c.IsDone) ?? Columns.FirstOrDefault();
    public Column FirstDoneColumn => Columns.FirstOrDefault(c => c.IsDone);
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
/// Every save is also an undo step.
/// </summary>
public static class BoardStore
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Boardwalk");

    public static string FilePath => Path.Combine(Folder, "boards.json");
    public static string BackupPath => FilePath + ".bak";
    public static string AttachmentsFolder => Path.Combine(Folder, "attachments");

    public static BoardFile Data { get; private set; } = new();
    public static List<Board> Boards => Data.Boards;

    /// <summary>Set when boards.json couldn't be read and the backup was used instead.</summary>
    public static string LoadWarning { get; private set; }

    /// <summary>Raised when a card moves into a "complete" status from one that isn't.</summary>
    public static event Action<Board, Card> CardCompleted;

    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Blue", "#2563EB"), ("Teal", "#0E9384"), ("Green", "#16A34A"), ("Yellow", "#CA8A04"),
        ("Orange", "#EA580C"), ("Red", "#DC2626"), ("Pink", "#DB2777"), ("Purple", "#7C3AED"),
        ("Gray", "#64748B"),
    };

    public static string Hex(string colorName) =>
        Palette.FirstOrDefault(p => p.Name == colorName).Hex ?? Palette[0].Hex;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    // =====================================================================
    //  Load, save, undo
    // =====================================================================

    private static string _lastJson;
    private static readonly List<string> _undo = new();
    private static readonly List<string> _redo = new();
    private const int MaxUndo = 100;

    public static bool CanUndo => _undo.Count > 0;
    public static bool CanRedo => _redo.Count > 0;

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
        _lastJson = Serialize();
        _undo.Clear();
        _redo.Clear();
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

    private static string Serialize() => JsonSerializer.Serialize(Data, SettingsService.Options);

    private static void Sanitize()
    {
        Data.Boards ??= new();
        Data.Boards.RemoveAll(b => b == null);
        foreach (var b in Data.Boards)
        {
            b.Columns ??= new();
            b.Labels ??= new();
            b.Templates ??= new();
            b.Name ??= "Board";
            b.Description ??= "";
            b.GitHubRepo ??= "";
            b.NextVersion ??= "";
            b.Columns.RemoveAll(c => c == null);
            b.Labels.RemoveAll(l => l == null);
            b.Templates.RemoveAll(t => t == null);
            foreach (var t in b.Templates) { t.LabelIds ??= new(); t.Checklist ??= new(); t.Name ??= ""; t.Title ??= ""; t.Notes ??= ""; }
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
                    c.BlockedBy ??= new();
                    c.Attachments ??= new();
                    c.History ??= new();
                    c.Title ??= "";
                    c.Notes ??= "";
                    c.Link ??= "";
                    c.LinkState ??= "";
                    c.Version ??= "";
                    c.ParentId ??= "";
                    max = Math.Max(max, c.Number);
                }
            }
            foreach (var c in b.AllCards.Where(c => c.Number <= 0)) c.Number = ++max;
            b.NextCardNumber = Math.Max(b.NextCardNumber, max + 1);
        }
    }

    public static void Save()
    {
        var json = Serialize();
        if (_lastJson != null && json != _lastJson)
        {
            _undo.Add(_lastJson);
            if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
            _redo.Clear();
        }
        _lastJson = json;
        Write(json);
    }

    /// <summary>Saves without making an undo step (for background updates like GitHub link states).</summary>
    public static void SaveQuietly()
    {
        var json = Serialize();
        if (json == _lastJson) return;
        // Fold the change into the latest undo step so undoing doesn't bring back stale states.
        _lastJson = json;
        Write(json);
    }

    private static void Write(string json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Folder, "error.log"), $"[{DateTime.Now:u}] Save failed: {ex}\r\n"); } catch { }
        }
    }

    /// <summary>Goes back one change. Board and card objects are replaced, so callers look them up again by id.</summary>
    public static bool Undo() => Step(_undo, _redo);

    public static bool Redo() => Step(_redo, _undo);

    private static bool Step(List<string> from, List<string> to)
    {
        if (from.Count == 0) return false;
        var json = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(_lastJson ?? Serialize());
        Data = JsonSerializer.Deserialize<BoardFile>(json, SettingsService.Options) ?? new BoardFile();
        Sanitize();
        _lastJson = json;
        Write(json);
        return true;
    }

    // =====================================================================
    //  Boards
    // =====================================================================

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
                b.NextVersion = "1.0.0";
                b.Templates.Add(new CardTemplate
                {
                    Name = "Bug report", Title = "Bug: ", LabelIds = { b.Labels[0].Id }, Priority = Priority.High,
                    Notes = "Steps to reproduce:\n1. \n\nWhat happened:\n\nWhat should happen:\n\nVersion and Windows version:\n",
                    Checklist = { "Reproduce it", "Fix it", "Test the fix" },
                });
                b.Templates.Add(new CardTemplate
                {
                    Name = "Feature", Title = "", LabelIds = { b.Labels[1].Id },
                    Notes = "Why:\n\nDone when:\n",
                    Checklist = { "Build it", "Test it", "Update the README and changelog" },
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

    /// <summary>Gives a board (and its columns and cards) new ids, keeping card links intact.</summary>
    private static void NewIds(Board b)
    {
        b.Id = NewId();
        var map = new Dictionary<string, string>();
        foreach (var col in b.Columns)
        {
            col.Id = NewId();
            foreach (var c in col.Cards) { var n = NewId(); map[c.Id] = n; c.Id = n; }
        }
        foreach (var c in b.AllCards)
        {
            c.BlockedBy = c.BlockedBy.Where(map.ContainsKey).Select(id => map[id]).ToList();
            c.ParentId = map.TryGetValue(c.ParentId ?? "", out var p) ? p : "";
            c.History.Clear();
            Log(c, "Copied with its board", b.ColumnOf(c));
        }
    }

    public static Board DuplicateBoard(Board src, bool includeCards)
    {
        var json = JsonSerializer.Serialize(src, SettingsService.Options);
        var b = JsonSerializer.Deserialize<Board>(json, SettingsService.Options);
        if (!includeCards) foreach (var col in b.Columns) col.Cards.Clear();
        NewIds(b);
        b.Name = src.Name + " (copy)";
        b.Created = b.Modified = DateTime.Now;
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

    // =====================================================================
    //  Cards
    // =====================================================================

    public static void Log(Card c, string text, Column entered = null)
    {
        c.History.Add(new HistoryEntry { Text = text, ColumnId = entered?.Id ?? "" });
        if (c.History.Count > 100) c.History.RemoveAt(0);
    }

    public static Card AddCard(Board b, Column col, string title, int index = -1, bool save = true)
    {
        var c = new Card { Title = title.Trim(), Number = b.NextCardNumber++ };
        Log(c, "Created in " + col.Title, col);
        if (index < 0 || index > col.Cards.Count) col.Cards.Add(c); else col.Cards.Insert(index, c);
        if (save) Touch(b);
        return c;
    }

    public static Card AddFromTemplate(Board b, Column col, CardTemplate t)
    {
        var c = AddCard(b, col, string.IsNullOrWhiteSpace(t.Title) ? t.Name : t.Title, -1, save: false);
        c.Notes = t.Notes;
        c.Priority = t.Priority;
        c.LabelIds = t.LabelIds.Where(id => b.Label(id) != null).ToList();
        c.Checklist = t.Checklist.Select(x => new CheckItem { Text = x }).ToList();
        Touch(b);
        return c;
    }

    public static CardTemplate TemplateFromCard(Board b, Card c, string name)
    {
        var t = new CardTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? c.Title : name.Trim(), Title = c.Title, Notes = c.Notes,
            Priority = c.Priority, LabelIds = c.LabelIds.ToList(), Checklist = c.Checklist.Select(i => i.Text).ToList(),
        };
        b.Templates.Add(t);
        Touch(b);
        return t;
    }

    /// <summary>Moves a card to <paramref name="target"/> at <paramref name="index"/> (an index among that column's cards before the move).</summary>
    public static void MoveCard(Board b, Card card, Column target, int index, bool save = true)
    {
        var from = b.ColumnOf(card);
        if (from == null) return;
        int old = from.Cards.IndexOf(card);
        from.Cards.RemoveAt(old);
        if (from == target && index > old) index--;
        index = Math.Clamp(index < 0 ? target.Cards.Count : index, 0, target.Cards.Count);
        target.Cards.Insert(index, card);
        bool completing = from != target && target.IsDone && !from.IsDone;
        if (from != target)
        {
            card.Modified = DateTime.Now;
            Log(card, completing ? $"Completed ({target.Title})" : $"Moved from {from.Title} to {target.Title}", target);
            if (completing) OnCompleted(b, card);
        }
        if (save) Touch(b);
        if (completing) CardCompleted?.Invoke(b, card);
    }

    private static void OnCompleted(Board b, Card card)
    {
        if (b.AutoVersion && card.Version.Length == 0 && b.NextVersion.Trim().Length > 0)
            card.Version = b.NextVersion.Trim();
        if (card.Repeat != RepeatKind.None)
        {
            var next = card.Clone();
            next.Number = b.NextCardNumber++;
            next.History = new();
            next.Created = next.Modified = DateTime.Now;
            next.Version = "";
            next.LinkState = "";
            next.Attachments = card.Attachments.ToList();
            foreach (var i in next.Checklist) i.Done = false;
            next.Due = card.Due is DateTime d ? NextDue(d, card.Repeat) : null;
            var col = b.FirstOpenColumn;
            Log(next, $"Repeats {card.Repeat.ToString().ToLowerInvariant()}: made when #{card.Number} was completed", col);
            col.Cards.Insert(0, next);
            card.Repeat = RepeatKind.None;
            Log(card, $"Next repeat is #{next.Number}");
        }
    }

    /// <summary>The next due date after <paramref name="due"/>, and never in the past.</summary>
    public static DateTime NextDue(DateTime due, RepeatKind r)
    {
        DateTime Step(DateTime d) => r switch
        {
            RepeatKind.Daily => d.AddDays(1),
            RepeatKind.Weekly => d.AddDays(7),
            RepeatKind.Monthly => d.AddMonths(1),
            RepeatKind.Yearly => d.AddYears(1),
            _ => d,
        };
        if (r == RepeatKind.None) return due;
        var n = Step(due);
        for (int i = 0; i < 1000 && n.Date < DateTime.Today; i++) n = Step(n);
        return n;
    }

    /// <summary>Moves a card to another board's first column (or a chosen one); it gets a number on the new board.</summary>
    public static void MoveCardToBoard(Board from, Card card, Board to, Column target = null)
    {
        target ??= to.FirstOpenColumn;
        if (target == null) return;
        Unlink(from, card);
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
        int oldNumber = card.Number;
        card.Number = to.NextCardNumber++;
        Log(card, $"Moved from board {from.Name} (was #{oldNumber}) to {target.Title}", target);
        target.Cards.Add(card);
        Touch(from);
        Touch(to);
    }

    /// <summary>Removes the links between a card and the rest of its board (blocked-by and sub-tasks).</summary>
    private static void Unlink(Board b, Card card)
    {
        foreach (var c in b.AllCards)
        {
            c.BlockedBy.Remove(card.Id);
            if (c.ParentId == card.Id) c.ParentId = "";
        }
        card.BlockedBy.Clear();
        card.ParentId = "";
    }

    public static void DeleteCard(Board b, Card card)
    {
        Unlink(b, card);
        b.ColumnOf(card)?.Cards.Remove(card);
        // Attachment files stay until the next start so Undo can bring them back.
        Touch(b);
    }

    public static Card DuplicateCard(Board b, Card card)
    {
        var col = b.ColumnOf(card);
        var c = card.Clone();
        c.Number = b.NextCardNumber++;
        c.Title = card.Title + " (copy)";
        c.Created = c.Modified = DateTime.Now;
        c.History = new();
        c.LinkState = "";
        Log(c, $"Copied from #{card.Number}", col);
        col.Cards.Insert(col.Cards.IndexOf(card) + 1, c);
        Touch(b);
        return c;
    }

    public static Card AddSubTask(Board b, Card parent, string title)
    {
        var col = b.FirstOpenColumn;
        var c = AddCard(b, col, title, -1, save: false);
        c.ParentId = parent.Id;
        c.LabelIds = parent.LabelIds.ToList();
        c.Version = parent.Version;
        Log(parent, $"Sub-task #{c.Number} added");
        Touch(b);
        return c;
    }

    /// <summary>Would making <paramref name="card"/> blocked by <paramref name="blocker"/> make a loop?</summary>
    public static bool WouldLoop(Board b, Card card, Card blocker)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<Card>();
        stack.Push(blocker);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            if (c == card) return true;
            if (!seen.Add(c.Id)) continue;
            foreach (var x in c.BlockedBy.Select(b.CardById).Where(x => x != null)) stack.Push(x);
        }
        return false;
    }

    public static int ArchiveColumn(Board b, Column col)
    {
        int n = 0;
        foreach (var c in col.Cards.Where(c => !c.Archived)) { c.Archived = true; Log(c, "Archived"); n++; }
        Touch(b);
        return n;
    }

    // =====================================================================
    //  Columns
    // =====================================================================

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
        foreach (var c in col.Cards.ToList()) Unlink(b, c);
        b.Columns.Remove(col);
        Touch(b);
    }

    public static void Touch(Board b)
    {
        b.Modified = DateTime.Now;
        Save();
    }

    // =====================================================================
    //  Attachments
    // =====================================================================

    public static string AttachmentPath(string fileName) => Path.Combine(AttachmentsFolder, fileName);

    /// <summary>The name a person gave the file (stored names start with a unique prefix).</summary>
    public static string AttachmentName(string fileName)
    {
        var parts = fileName.Split('_', 3);
        return parts.Length == 3 ? parts[2] : fileName;
    }

    /// <summary>A new, unique path in the attachments folder for a file called <paramref name="name"/>.</summary>
    public static string NewAttachment(Card c, string name, out string fileName)
    {
        Directory.CreateDirectory(AttachmentsFolder);
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        fileName = $"{c.Id}_{Guid.NewGuid().ToString("N")[..6]}_{name}";
        return AttachmentPath(fileName);
    }

    public static void AttachFile(Card c, string sourcePath)
    {
        var dest = NewAttachment(c, Path.GetFileName(sourcePath), out var fileName);
        File.Copy(sourcePath, dest);
        c.Attachments.Add(fileName);
    }

    /// <summary>Deletes attachment files no card refers to (run at start-up, so Undo during a session can still restore them).</summary>
    public static int CleanUpAttachments()
    {
        try
        {
            if (!Directory.Exists(AttachmentsFolder)) return 0;
            var used = Boards.SelectMany(b => b.AllCards).SelectMany(c => c.Attachments).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var f in Directory.GetFiles(AttachmentsFolder))
            {
                if (used.Contains(Path.GetFileName(f))) continue;
                try { File.Delete(f); n++; } catch { }
            }
            return n;
        }
        catch { return 0; }
    }

    // =====================================================================
    //  Releases and stats
    // =====================================================================

    /// <summary>The board's versions, newest first.</summary>
    public static List<string> Versions(Board b)
    {
        var list = b.AllCards.Select(c => c.Version).Append(b.NextVersion)
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList();
        return list.OrderByDescending(v => System.Version.TryParse(v.TrimStart('v', 'V'), out var p) ? p : new System.Version(0, 0))
                   .ThenByDescending(v => v).ToList();
    }

    /// <summary>Which changelog section a card goes in, from its labels.</summary>
    public static string ChangelogSection(Board b, Card c)
    {
        var names = c.LabelIds.Select(b.Label).Where(l => l != null).Select(l => l.Name.ToLowerInvariant()).ToList();
        if (names.Any(n => n.Contains("bug") || n.Contains("fix"))) return "Fixed";
        if (names.Any(n => n.Contains("feature") || n == "new")) return "Added";
        return "Changed";
    }

    /// <summary>A CHANGELOG.md section for the completed cards in <paramref name="version"/>.</summary>
    public static string Changelog(Board b, string version, DateTime date, bool numbers)
    {
        var cards = b.AllCards.Where(c => c.Version == version && b.IsComplete(c)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"## {version} - {date:yyyy-MM-dd}");
        foreach (var section in new[] { "Added", "Changed", "Fixed" })
        {
            var items = cards.Where(c => ChangelogSection(b, c) == section).OrderBy(c => c.Number).ToList();
            if (items.Count == 0) continue;
            sb.AppendLine().AppendLine("### " + section);
            foreach (var c in items) sb.AppendLine($"- {c.Title}{(numbers ? $" (#{c.Number})" : "")}");
        }
        if (cards.Count == 0) sb.AppendLine().AppendLine("_No completed cards have this version yet._");
        return sb.ToString();
    }

    /// <summary>When a card was completed (null if it isn't).</summary>
    public static DateTime? CompletedAt(Board b, Card c)
    {
        if (!b.IsComplete(c)) return null;
        var col = b.ColumnOf(c);
        var e = c.History.LastOrDefault(h => h.ColumnId == col.Id);
        return e?.When ?? c.Modified;
    }

    /// <summary>When the card entered its current status.</summary>
    public static DateTime InStatusSince(Board b, Card c)
    {
        var col = b.ColumnOf(c);
        var e = col == null ? null : c.History.LastOrDefault(h => h.ColumnId == col.Id);
        return e?.When ?? c.Created;
    }

    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 60 ? "less than an hour"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours} hour{((int)t.TotalHours == 1 ? "" : "s")}"
        : $"{(int)t.TotalDays} day{((int)t.TotalDays == 1 ? "" : "s")}";

    // =====================================================================
    //  Import / export
    // =====================================================================

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
                if (c.Version.Length > 0) extra.Add("v" + c.Version.TrimStart('v', 'V'));
                if (c.Checklist.Count > 0) extra.Add($"{c.Checklist.Count(i => i.Done)}/{c.Checklist.Count} done");
                var blockers = b.OpenBlockers(c);
                if (blockers.Count > 0) extra.Add("blocked by " + string.Join(", ", blockers.Select(x => "#" + x.Number)));
                var parent = b.CardById(c.ParentId);
                if (parent != null) extra.Add($"part of #{parent.Number}");
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
        var data = Data;
        foreach (var b in file?.Boards ?? new List<Board>())
        {
            if (b == null) continue;
            Data = new BoardFile { Boards = { b } };
            Sanitize();
            Data = data;
            if (Boards.Any(x => x.Id == b.Id)) NewIds(b);
            Boards.Add(b);
            added.Add(b);
        }
        Sanitize();
        Save();
        return added;
    }

    /// <summary>Adds GitHub issues as cards in the first status, skipping ones already on the board. Returns how many were added.</summary>
    public static int ImportIssues(Board b, IEnumerable<GitHubIssue> issues)
    {
        var col = b.FirstOpenColumn;
        if (col == null) return 0;
        var have = b.AllCards.Select(c => c.Link.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var i in issues)
        {
            if (have.Contains(i.Url.TrimEnd('/'))) continue;
            var c = AddCard(b, col, i.Title, -1, save: false);
            c.Link = i.Url;
            c.LinkState = i.State;
            c.LinkChecked = DateTime.Now;
            var body = (i.Body ?? "").Trim();
            c.Notes = body.Length > 4000 ? body[..4000] + "…" : body;
            foreach (var name in i.Labels)
            {
                var l = b.Labels.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (name.Equals("enhancement", StringComparison.OrdinalIgnoreCase) && x.Name.Equals("Feature", StringComparison.OrdinalIgnoreCase))
                    || (name.Equals("documentation", StringComparison.OrdinalIgnoreCase) && x.Name.Equals("Docs", StringComparison.OrdinalIgnoreCase)));
                if (l != null && !c.LabelIds.Contains(l.Id)) c.LabelIds.Add(l.Id);
            }
            c.History.Clear();
            Log(c, $"Imported from GitHub issue #{i.Number}", col);
            n++;
        }
        if (n > 0) Touch(b);
        return n;
    }
}
