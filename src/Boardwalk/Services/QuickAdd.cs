using System.Globalization;
using System.Text.RegularExpressions;

namespace Boardwalk.Services;

/// <summary>
/// Quick add: "Fix crash #bug !high friday" → title "Fix crash", label Bug, High priority, due Friday.
/// #label matches a board label (use - or _ for spaces), !low / !medium / !high / !urgent sets priority,
/// and a due date can end the text: today, tomorrow, next week, a weekday, or @ and a date (@11/20).
/// </summary>
public static class QuickAdd
{
    public sealed class Result
    {
        public string Title = "";
        public List<string> LabelIds = new();
        public Priority Priority = Priority.None;
        public DateTime? Due;

        public bool HasExtras => LabelIds.Count > 0 || Priority != Priority.None || Due != null;
    }

    private static readonly Dictionary<string, Priority> Priorities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = Priority.Low, ["med"] = Priority.Medium, ["medium"] = Priority.Medium, ["normal"] = Priority.Medium,
        ["high"] = Priority.High, ["urgent"] = Priority.Urgent, ["!"] = Priority.High, ["!!"] = Priority.Urgent,
    };

    public static Result Parse(Board b, string text)
    {
        var r = new Result();
        var words = Regex.Split((text ?? "").Trim(), @"\s+").Where(w => w.Length > 0).ToList();

        // A due date at the end ("next week", "tomorrow", "friday", "@11/20"), optionally after "due" or "by".
        if (words.Count >= 3 && words[^2].Equals("next", StringComparison.OrdinalIgnoreCase) && words[^1].Equals("week", StringComparison.OrdinalIgnoreCase))
        {
            r.Due = DateTime.Today.AddDays(7);
            words.RemoveRange(words.Count - 2, 2);
        }
        else if (words.Count >= 2 && TryDateWord(words[^1], out var d))
        {
            r.Due = d;
            words.RemoveAt(words.Count - 1);
        }
        if (r.Due != null && words.Count >= 2 && (words[^1].Equals("due", StringComparison.OrdinalIgnoreCase) || words[^1].Equals("by", StringComparison.OrdinalIgnoreCase)))
            words.RemoveAt(words.Count - 1);

        var title = new List<string>();
        foreach (var w in words)
        {
            if (w.Length > 1 && w[0] == '#' && !w[1..].All(char.IsDigit))
            {
                var name = w[1..].Replace('-', ' ').Replace('_', ' ');
                var label = b.Labels.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                         ?? b.Labels.FirstOrDefault(l => l.Name.Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                if (label != null) { if (!r.LabelIds.Contains(label.Id)) r.LabelIds.Add(label.Id); continue; }
            }
            if (w.Length > 1 && w[0] == '!' && Priorities.TryGetValue(w[1..], out var p)) { r.Priority = p; continue; }
            if (w == "!!!") { r.Priority = Priority.Urgent; continue; }
            title.Add(w);
        }
        r.Title = string.Join(" ", title);
        if (r.Title.Length == 0) r.Title = (text ?? "").Trim();   // never lose the whole text
        return r;
    }

    private static bool TryDateWord(string w, out DateTime due)
    {
        due = default;
        var t = w.ToLowerInvariant().TrimEnd('.', ',');
        if (t == "today") { due = DateTime.Today; return true; }
        if (t == "tomorrow" || t == "tmrw") { due = DateTime.Today.AddDays(1); return true; }
        foreach (DayOfWeek day in Enum.GetValues(typeof(DayOfWeek)))
        {
            var name = day.ToString().ToLowerInvariant();
            if (t == name || (t.StartsWith('@') && (t[1..] == name || t[1..] == name[..3])))
            {
                int add = ((int)day - (int)DateTime.Today.DayOfWeek + 7) % 7;
                due = DateTime.Today.AddDays(add == 0 ? 7 : add);
                return true;
            }
        }
        if (t.StartsWith('@') && t.Length > 1)
        {
            var s = t[1..];
            if (s == "today") { due = DateTime.Today; return true; }
            if (s == "tomorrow") { due = DateTime.Today.AddDays(1); return true; }
            if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var dt) ||
                DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                due = dt.Date;
                // "@1/5" in December means next January.
                if (due < DateTime.Today.AddDays(-60) && !s.Contains(DateTime.Today.Year.ToString())) due = due.AddYears(1);
                return true;
            }
        }
        return false;
    }

    /// <summary>A short summary of what was recognised, shown under the "Add a card" box.</summary>
    public static string Describe(Board b, Result r)
    {
        var parts = new List<string>();
        foreach (var id in r.LabelIds) parts.Add(b.Label(id)?.Name ?? "");
        if (r.Priority != Priority.None) parts.Add(r.Priority + " priority");
        if (r.Due is DateTime d) parts.Add("due " + d.ToString("ddd MMM d"));
        return parts.Count == 0 ? "" : "Adds: " + string.Join(" · ", parts);
    }
}
