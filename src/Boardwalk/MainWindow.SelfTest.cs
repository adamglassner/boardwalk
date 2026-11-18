using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Boardwalk.Dialogs;
using Boardwalk.Services;

namespace Boardwalk;

/// <summary>
/// "Boardwalk.exe --self-test result.txt [screenshots]" — exercises boards, columns, cards, drag targets,
/// saving, recovery, import/export, dialogs and themes, writing PASS/FAIL lines. Used by the GitHub build.
/// </summary>
public partial class MainWindow
{
    private int _fails;

    private void Log(string line)
    {
        try { File.AppendAllText(App.SelfTestOutput, line + "\r\n"); } catch { }
    }

    private void Check(string name, bool ok, string detail = "")
    {
        Log((ok ? "PASS " : "FAIL ") + name + (!ok && detail.Length > 0 ? " — " + detail : ""));
        if (!ok) _fails++;
    }

    private async Task Settle()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        UpdateLayout();
        await Task.Delay(120);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private async Task Test(string name, Func<Task<bool>> body)
    {
        App.MainWindowTestName = name;
        try { Check(name, await body()); }
        catch (Exception ex) { Check(name, false, ex.GetType().Name + ": " + ex.Message); }
    }

    private Task Test(string name, Func<bool> body) => Test(name, () => Task.FromResult(body()));

    private static void Shot(Window w, string name)
    {
        if (App.SelfTestShots == null) return;
        try
        {
            Directory.CreateDirectory(App.SelfTestShots);
            w.UpdateLayout();
            if (w.Content is not FrameworkElement content || content.ActualWidth < 1 || content.ActualHeight < 1) return;
            double dpi = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            var size = new Size(content.ActualWidth, content.ActualHeight);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * dpi), (int)Math.Ceiling(size.Height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(w.Background, null, new Rect(size));
                dc.DrawRectangle(new VisualBrush(content), null, new Rect(size));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(App.SelfTestShots, name + ".png"));
            enc.Save(fs);
        }
        catch { }
    }

    private int VisibleCards(Column col) =>
        CardPanels.TryGetValue(col.Id, out var p) ? p.Children.OfType<FrameworkElement>().Count(fe => fe.Tag is Card) : -1;

    private static bool ContainsText(DependencyObject root, string text)
    {
        if (root is TextBlock tb && tb.Text == text) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d && ContainsText(d, text)) return true;
        return false;
    }

    private async Task ShowDialogForShot(Window d, string shot)
    {
        d.Owner = this;
        d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        d.Show();
        await Settle();
        Shot(d, shot);
        d.Close();
    }

    internal async Task RunSelfTest()
    {
        File.WriteAllText(App.SelfTestOutput, $"Boardwalk {App.Version} self-test {DateTime.Now:u}\r\n");
        SettingsService.Current.ConfirmDeletes = false;
        Width = 1360;
        Height = 820;
        await Settle();
        try { await RunTests(); }
        catch (Exception ex) { Check("self-test crashed", false, ex.ToString().Replace("\r\n", " | ")); }
        Log(_fails == 0 ? "ALL PASSED" : $"{_fails} FAILED");
        Application.Current.Shutdown(_fails == 0 ? 0 : 1);
    }

    private async Task RunTests()
    {
        // ---------------- first run ----------------
        await Test("First run shows the empty state and no boards", () =>
            BoardStore.Boards.Count == 0 && EmptyState.Visibility == Visibility.Visible && BoardScroll.Visibility != Visibility.Visible);
        Shot(this, "empty-state");

        // ---------------- boards and statuses ----------------
        Board std = null, sw = null;
        await Test("New board (Standard) has New / In progress / On hold / Complete", async () =>
        {
            std = BoardStore.CreateBoard("Website refresh", BoardTemplate.Standard, "Teal");
            RefreshBoardList(std);
            await Settle();
            return CurrentBoard == std && BoardList.Items.Count == 1
                && string.Join("|", std.Columns.Select(c => c.Title)) == "New|In progress|On hold|Complete"
                && std.Columns[3].IsDone && !std.Columns[0].IsDone
                && std.Columns[2].Color == "Orange" && std.Columns[3].Color == "Green"
                && ColumnsPanel.Children.Count == 5;   // 4 columns + "Add a column"
        });

        await Test("Software project template has its statuses, labels and a limit on In progress", () =>
        {
            sw = BoardStore.CreateBoard("App project", BoardTemplate.SoftwareProject, "Purple");
            RefreshBoardList(sw);
            return sw.Columns.Count == 6 && sw.Columns[2].Title == "In progress" && sw.Columns[2].WipLimit == 3
                && sw.Columns[^1].IsDone && sw.Labels.Select(l => l.Name).SequenceEqual(new[] { "Bug", "Feature", "Improvement", "Design", "Docs" })
                && BoardList.Items.Count == 2 && CurrentBoard == sw;
        });

        await Test("Personal and Blank templates", () =>
        {
            var p = BoardStore.CreateBoard("Home", BoardTemplate.Personal);
            var blank = BoardStore.CreateBoard("Blank", BoardTemplate.Blank);
            bool ok = p.Columns.Count == 4 && p.Columns[^1].IsDone && blank.Columns.Count == 0;
            BoardStore.DeleteBoard(p);
            BoardStore.DeleteBoard(blank);
            RefreshBoardList(sw);
            return ok && BoardStore.Boards.Count == 2;
        });

        await Test("Switching boards from the list", async () =>
        {
            ((ListBoxItem)BoardList.Items[0]).IsSelected = true;
            await Settle();
            bool first = CurrentBoard == std && BoardTitle.Text == "Website refresh";
            ((ListBoxItem)BoardList.Items[1]).IsSelected = true;
            await Settle();
            return first && CurrentBoard == sw && SettingsService.Current.LastBoardId == sw.Id;
        });

        // ---------------- cards ----------------
        var backlog = sw.Columns[0];
        var todo = sw.Columns[1];
        var doing = sw.Columns[2];
        var hold = sw.Columns[3];
        var review = sw.Columns[4];
        var done = sw.Columns[5];
        // Reloading from disk makes new objects, so look the columns up again afterwards.
        void Rebind()
        {
            std = BoardStore.Boards.First(b => b.Name.StartsWith("Website refresh"));
            sw = BoardStore.Boards.First(b => b.Name == "App project");
            backlog = sw.Columns[0]; todo = sw.Columns[1]; doing = sw.Columns[2]; hold = sw.Columns[3]; review = sw.Columns[4]; done = sw.Columns[5];
            RefreshBoardList(sw);
        }
        await Test("Adding cards numbers them #1, #2, …", async () =>
        {
            foreach (var t in new[] { "Sign-in screen", "Crash when saving", "Dark mode polish", "Write the README", "Settings page" })
                BoardStore.AddCard(sw, backlog, t);
            RenderBoard();
            await Settle();
            return backlog.Cards.Select(c => c.Number).SequenceEqual(new[] { 1, 2, 3, 4, 5 }) && VisibleCards(backlog) == 5;
        });

        await Test("The Add a card box adds a card and stays open", async () =>
        {
            _addingKey = todo.Id;
            RenderBoard();
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(b => b.Tag == todo);
            var box = FindChild<TextBox>(col);
            if (box == null) return false;
            box.Text = "Export to Markdown";
            box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, System.Windows.Input.Key.Enter)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            await Settle();
            var col2 = ColumnsPanel.Children.OfType<Border>().First(b => b.Tag == todo);
            bool stillOpen = FindChild<TextBox>(col2) != null;
            _addingKey = null;
            RenderBoard();
            return todo.Cards.Count == 1 && todo.Cards[0].Title == "Export to Markdown" && todo.Cards[0].Number == 6 && stillOpen;
        });

        await Test("Moving a card to another status", async () =>
        {
            var c = backlog.Cards[1];
            MoveCard(sw, c, doing, 0);
            await Settle();
            return doing.Cards.Count == 1 && doing.Cards[0] == c && backlog.Cards.Count == 4 && VisibleCards(doing) == 1 && VisibleCards(backlog) == 4;
        });

        await Test("Reordering cards within a status", () =>
        {
            var first = backlog.Cards[0];
            MoveCard(sw, first, backlog, backlog.Cards.Count);
            bool atEnd = backlog.Cards[^1] == first;
            MoveCard(sw, first, backlog, 0);
            return atEnd && backlog.Cards[0] == first;
        });

        await Test("Drop targets: every column accepts dropped cards", () =>
            ColumnsPanel.Children.OfType<Border>().Where(b => b.Tag is Column).All(b => b.AllowDrop)
            && sw.Columns.All(c => CardPanels.ContainsKey(c.Id)));

        await Test("Card limit warns when a status has too many cards", async () =>
        {
            doing.WipLimit = 1;
            MoveCard(sw, backlog.Cards[0], doing, -1);
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(b => b.Tag == doing);
            bool over = doing.OverLimit && ContainsText(col, "2/1");
            doing.WipLimit = 3;
            BoardStore.Touch(sw);
            RenderBoard();
            return over && !doing.OverLimit;
        });

        Card detailed = null;
        await Test("Card details show labels, priority, due date, checklist and link", async () =>
        {
            detailed = doing.Cards.First(c => c.Title == "Crash when saving");
            detailed.LabelIds.Add(sw.Labels[0].Id);
            detailed.Priority = Priority.Urgent;
            detailed.Due = DateTime.Today.AddDays(-1);
            detailed.Notes = "Happens when the disk is full.";
            detailed.Link = "https://github.com/example/app/issues/12";
            detailed.Checklist = new() { new CheckItem { Text = "Reproduce", Done = true }, new CheckItem { Text = "Fix" } };
            BoardStore.Touch(sw);
            RenderBoard();
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(b => b.Tag == doing);
            return ContainsText(col, "Bug") && ContainsText(col, "Urgent") && ContainsText(col, "Yesterday") && ContainsText(col, "1/2") && ContainsText(col, "#2");
        });

        await Test("Cards in a Complete status get a tick and aren't overdue", async () =>
        {
            MoveCard(sw, detailed, done, -1);
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(b => b.Tag == done);
            bool ok = ContainsText(col, "");
            MoveCard(sw, detailed, doing, 0);
            return ok;
        });

        await Test("Due dates understand words and dates", () =>
            CardDialog.TryParseDue("tomorrow", out var a) && a == DateTime.Today.AddDays(1)
            && CardDialog.TryParseDue("", out var b) && b == null
            && CardDialog.TryParseDue("2026-11-20", out var c) && c == new DateTime(2026, 11, 20)
            && CardDialog.TryParseDue("friday", out var d) && d?.DayOfWeek == DayOfWeek.Friday && d > DateTime.Today
            && !CardDialog.TryParseDue("not a date", out _));

        // ---------------- search and filter ----------------
        await Test("Search finds cards by title, notes and #number", async () =>
        {
            SearchBox.Text = "disk is full";
            await Settle();
            int byNotes = sw.Columns.Sum(VisibleCards);
            SearchBox.Text = "#4";
            await Settle();
            int byNumber = sw.Columns.Sum(VisibleCards);
            SearchBox.Text = "";
            await Settle();
            return byNotes == 1 && byNumber == 1 && sw.Columns.Sum(VisibleCards) == sw.ActiveCardCount;
        });

        await Test("Label filter shows only cards with that label", async () =>
        {
            LabelFilter.SelectedItem = "Bug";
            await Settle();
            int n = sw.Columns.Sum(VisibleCards);
            LabelFilter.SelectedIndex = 0;
            await Settle();
            return n == 1;
        });

        // ---------------- columns ----------------
        await Test("Adding a status from the presets gives it its colour", () =>
        {
            var blocked = AddColumn(sw, "Blocked");
            bool ok = blocked.Color == "Red" && !blocked.IsDone && sw.Columns[^1] == blocked;
            BoardStore.MoveColumn(sw, blocked, -1);
            ok &= sw.Columns[^2] == blocked;
            BoardStore.DeleteColumn(sw, blocked);
            RenderBoard();
            var custom = AddColumn(sw, "Waiting on client");
            ok &= custom.Color == "Blue" && !custom.IsDone;
            BoardStore.DeleteColumn(sw, custom);
            var cancelled = BoardStore.AddColumn(sw, "Cancelled");
            ok &= cancelled.IsDone;
            BoardStore.DeleteColumn(sw, cancelled);
            RenderBoard();
            return ok && sw.Columns.Count == 6;
        });

        await Test("Archiving a column's cards hides them and keeps them", async () =>
        {
            BoardStore.AddCard(sw, done, "Old release notes");
            BoardStore.AddCard(sw, done, "Ship 0.9");
            int n = BoardStore.ArchiveColumn(sw, done);
            RenderBoard();
            await Settle();
            var dlg = new ArchivedDialog(sw);
            bool ok = n == 2 && done.ActiveCount == 0 && VisibleCards(done) == 0 && dlg.Count == 2;
            await ShowDialogForShot(dlg, "dialog-archived");
            return ok;
        });

        // ---------------- other boards ----------------
        await Test("Moving a card to another board keeps its label and renumbers it", () =>
        {
            var card = doing.Cards.First(c => c.Title == "Crash when saving");
            BoardStore.MoveCardToBoard(sw, card, std);
            var moved = std.Columns[0].Cards.LastOrDefault();
            bool ok = moved == card && card.Number == 1 && std.Labels.Any(l => l.Name == "Bug" && card.LabelIds.Contains(l.Id))
                && !doing.Cards.Contains(card);
            BoardStore.MoveCardToBoard(std, card, sw, doing);
            RenderBoard();
            return ok && doing.Cards.Contains(card);
        });

        await Test("Duplicate board (columns and labels only) and delete it", () =>
        {
            var copy = BoardStore.DuplicateBoard(sw, false);
            bool ok = copy.Name == "App project (copy)" && copy.Columns.Count == 6 && copy.AllCards.Count() == 0 && copy.Labels.Count == 5 && copy.Id != sw.Id;
            BoardStore.DeleteBoard(copy);
            RefreshBoardList(sw);
            return ok && BoardStore.Boards.Count == 2 && BoardList.Items.Count == 2;
        });

        await Test("Duplicate card", () =>
        {
            var c = backlog.Cards[0];
            var d = BoardStore.DuplicateCard(sw, c);
            bool ok = backlog.Cards[1] == d && d.Id != c.Id && d.Number > c.Number;
            BoardStore.DeleteCard(sw, d);
            RenderBoard();
            return ok;
        });

        // ---------------- saving ----------------
        await Test("Boards are saved and load back the same", () =>
        {
            BoardStore.Save();
            var before = JsonSerializer.Serialize(BoardStore.Data, SettingsService.Options);
            BoardStore.Load();
            var after = JsonSerializer.Serialize(BoardStore.Data, SettingsService.Options);
            Rebind();
            return before == after && BoardStore.Boards.Count == 2 && BoardStore.LoadWarning == null;
        });

        await Test("A damaged boards file falls back to the backup", () =>
        {
            BoardStore.Save();   // makes boards.json.bak a full copy
            BoardStore.Save();
            File.WriteAllText(BoardStore.FilePath, "{ this is not json");
            BoardStore.Load();
            bool ok = BoardStore.LoadWarning != null && BoardStore.Boards.Count == 2 && File.Exists(BoardStore.FilePath + ".bad");
            BoardStore.Save();
            BoardStore.Load();
            Rebind();
            return ok && BoardStore.LoadWarning == null;
        });

        await Test("Export as Markdown", () =>
        {
            var md = BoardStore.ToMarkdown(sw);
            var crash = sw.AllCards.First(c => c.Title == "Crash when saving");
            return md.StartsWith("# App project") && md.Contains("## In progress") && md.Contains($"#{crash.Number} [Crash when saving](https://github.com/example/app/issues/12)") && md.Contains("Urgent priority");
        });

        await Test("Export a board and import it back as a new board", () =>
        {
            var path = Path.Combine(BoardStore.Folder, "export-test.json");
            BoardStore.ExportBoard(sw, path);
            var added = BoardStore.ImportBoards(path);
            bool ok = added.Count == 1 && added[0].Id != sw.Id && added[0].ActiveCardCount == sw.ActiveCardCount && BoardStore.Boards.Count == 3;
            BoardStore.DeleteBoard(added[0]);
            RefreshBoardList(sw);
            return ok && BoardStore.Boards.Count == 2;
        });

        await Test("Rename and recolour a board", () =>
        {
            std.Name = "Website refresh 2026";
            std.Color = "Orange";
            BoardStore.Touch(std);
            RefreshBoardList(sw);
            return ((ListBoxItem)BoardList.Items[0]).Content is DockPanel dp && ContainsText(dp, "Website refresh 2026");
        });


        // =====================================================================
        //  1.1 features
        // =====================================================================

        await Test("Undo and redo", async () =>
        {
            int before = sw.ActiveCardCount;
            BoardStore.AddCard(sw, backlog, "Card to undo");
            RenderBoard();
            bool added = sw.ActiveCardCount == before + 1 && UndoButton.IsEnabled;
            UndoRedo(true);
            Rebind();
            await Settle();
            bool undone = sw.ActiveCardCount == before && !sw.AllCards.Any(c => c.Title == "Card to undo") && CurrentBoard == sw && RedoButton.IsEnabled;
            UndoRedo(false);
            Rebind();
            bool redone = sw.AllCards.Any(c => c.Title == "Card to undo");
            var extra = sw.AllCards.First(c => c.Title == "Card to undo");
            BoardStore.DeleteCard(sw, extra);
            UndoRedo(true);
            Rebind();
            bool deleteUndone = sw.AllCards.Any(c => c.Title == "Card to undo");
            BoardStore.DeleteCard(sw, sw.AllCards.First(c => c.Title == "Card to undo"));
            RenderBoard();
            return added && undone && redone && deleteUndone;
        });

        await Test("Quick add reads #label, !priority and a due date", () =>
        {
            var r = QuickAdd.Parse(sw, "Fix crash #bug !high friday");
            var r2 = QuickAdd.Parse(sw, "Reply about #12 tomorrow");
            var r3 = QuickAdd.Parse(sw, "Release notes for 1.0");
            var r4 = QuickAdd.Parse(sw, "Ship it #feature !urgent due @2027-01-15");
            var r5 = QuickAdd.Parse(sw, "Friday");
            return r.Title == "Fix crash" && r.LabelIds.Count == 1 && sw.Label(r.LabelIds[0]).Name == "Bug" && r.Priority == Priority.High
                && r.Due?.DayOfWeek == DayOfWeek.Friday && r.Due > DateTime.Today
                && r2.Title == "Reply about #12" && r2.Due == DateTime.Today.AddDays(1) && r2.LabelIds.Count == 0
                && r3.Title == "Release notes for 1.0" && !r3.HasExtras
                && r4.Title == "Ship it" && r4.Priority == Priority.Urgent && r4.Due == new DateTime(2027, 1, 15)
                && r5.Title == "Friday" && r5.Due == null;
        });

        await Test("Quick add makes the card", () =>
        {
            var c = AddCardQuick(sw, todo, "Settings search #feature !medium tomorrow");
            bool ok = c.Title == "Settings search" && c.Priority == Priority.Medium && c.Due == DateTime.Today.AddDays(1) && c.LabelIds.Count == 1 && todo.Cards.Contains(c);
            BoardStore.DeleteCard(sw, c);
            RenderBoard();
            return ok;
        });

        await Test("Card templates", () =>
        {
            bool seeded = sw.Templates.Count == 2 && sw.Templates[0].Name == "Bug report";
            var c = BoardStore.AddFromTemplate(sw, backlog, sw.Templates[0]);
            bool fromTpl = c.Notes.StartsWith("Steps to reproduce") && c.Checklist.Count == 3 && c.Priority == Priority.High && sw.Label(c.LabelIds[0]).Name == "Bug";
            c.Title = "Bug: crash on start";
            var t = BoardStore.TemplateFromCard(sw, c, "Crash report");
            bool saved = sw.Templates.Count == 3 && t.Checklist.Count == 3;
            sw.Templates.Remove(t);
            BoardStore.DeleteCard(sw, c);
            RenderBoard();
            return seeded && fromTpl && saved;
        });

        await Test("Blocked by another card", async () =>
        {
            var a = BoardStore.AddCard(sw, todo, "Write the docs");
            var blocker = BoardStore.AddCard(sw, doing, "Finish the API");
            a.BlockedBy.Add(blocker.Id);
            BoardStore.Touch(sw);
            RenderBoard();
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(x => x.Tag == todo);
            bool shown = ContainsText(col, "Blocked by #" + blocker.Number) && sw.OpenBlockers(a).Count == 1;
            bool loop = BoardStore.WouldLoop(sw, blocker, a);
            MoveCard(sw, blocker, done, -1);
            bool cleared = sw.OpenBlockers(a).Count == 0;
            BoardStore.DeleteCard(sw, blocker);
            bool unlinked = a.BlockedBy.Count == 0;
            BoardStore.DeleteCard(sw, a);
            RenderBoard();
            return shown && loop && cleared && unlinked;
        });

        await Test("Sub-tasks", async () =>
        {
            var parent = BoardStore.AddCard(sw, todo, "Settings window");
            var k1 = BoardStore.AddSubTask(sw, parent, "Theme picker");
            var k2 = BoardStore.AddSubTask(sw, parent, "Token box");
            RenderBoard();
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(x => x.Tag == todo);
            bool counted = ContainsText(col, "0/2") && k1.ParentId == parent.Id && sw.ColumnOf(k1) == sw.FirstOpenColumn;
            MoveCard(sw, k1, done, -1);
            await Settle();
            col = ColumnsPanel.Children.OfType<Border>().First(x => x.Tag == todo);
            bool progress = ContainsText(col, "1/2");
            BoardStore.DeleteCard(sw, parent);
            bool orphaned = k2.ParentId == "";
            BoardStore.DeleteCard(sw, k1);
            BoardStore.DeleteCard(sw, k2);
            RenderBoard();
            return counted && progress && orphaned;
        });

        await Test("Repeating cards make the next one when completed", () =>
        {
            var c = BoardStore.AddCard(sw, todo, "Update dependencies");
            c.Due = DateTime.Today;
            c.Repeat = RepeatKind.Weekly;
            c.Checklist.Add(new CheckItem { Text = "Run the update", Done = true });
            MoveCard(sw, c, done, -1);
            var next = sw.FirstOpenColumn.Cards.FirstOrDefault(x => x.Title == "Update dependencies");
            bool ok = next != null && next != c && next.Due == DateTime.Today.AddDays(7) && next.Repeat == RepeatKind.Weekly
                && c.Repeat == RepeatKind.None && !next.Checklist[0].Done && next.Number > c.Number;
            bool dueLater = BoardStore.NextDue(DateTime.Today.AddDays(-40), RepeatKind.Weekly) >= DateTime.Today
                && BoardStore.NextDue(new DateTime(2026, 1, 31), RepeatKind.Monthly).Month == 2;
            if (next != null) BoardStore.DeleteCard(sw, next);
            BoardStore.DeleteCard(sw, c);
            RenderBoard();
            return ok && dueLater;
        });

        await Test("Card history and time in status", () =>
        {
            var c = BoardStore.AddCard(sw, todo, "History check");
            MoveCard(sw, c, doing, -1);
            bool ok = c.History.Count == 2 && c.History[0].Text == "Created in To do" && c.History[1].Text == "Moved from To do to In progress"
                && c.History[1].ColumnId == doing.Id && (DateTime.Now - BoardStore.InStatusSince(sw, c)).TotalMinutes < 5;
            MoveCard(sw, c, done, -1);
            ok &= c.History[^1].Text.StartsWith("Completed") && BoardStore.CompletedAt(sw, c) != null;
            BoardStore.DeleteCard(sw, c);
            RenderBoard();
            return ok;
        });

        await Test("Completed cards get the version, and the changelog is written from them", () =>
        {
            sw.NextVersion = "1.2.0";
            var bug = BoardStore.AddCard(sw, doing, "Crash when the disk is full");
            bug.LabelIds.Add(sw.Labels.First(l => l.Name == "Bug").Id);
            var feat = BoardStore.AddCard(sw, doing, "Dark mode");
            feat.LabelIds.Add(sw.Labels.First(l => l.Name == "Feature").Id);
            var other = BoardStore.AddCard(sw, doing, "Faster loading");
            MoveCard(sw, bug, done, -1);
            MoveCard(sw, feat, done, -1);
            MoveCard(sw, other, done, -1);
            var log = BoardStore.Changelog(sw, "1.2.0", new DateTime(2026, 12, 1), numbers: true);
            bool ok = bug.Version == "1.2.0" && BoardStore.Versions(sw)[0] == "1.2.0"
                && log.StartsWith("## 1.2.0 - 2026-12-01")
                && log.Contains("### Added" + Environment.NewLine + "- Dark mode (#" + feat.Number + ")")
                && log.Contains("### Fixed" + Environment.NewLine + "- Crash when the disk is full")
                && log.Contains("### Changed" + Environment.NewLine + "- Faster loading")
                && log.IndexOf("### Added") < log.IndexOf("### Changed") && log.IndexOf("### Changed") < log.IndexOf("### Fixed")
                && ChangelogDialog.Bump("1.2.0") == "1.3.0" && ChangelogDialog.Bump("v2.9") == "v2.10";
            RenderBoard();
            return ok;
        });

        await Test("GitHub addresses are understood", () =>
        {
            bool repo = GitHubService.TryParseRepo("adamglassner/boardwalk", out var o1, out var r1) && o1 == "adamglassner" && r1 == "boardwalk"
                && GitHubService.TryParseRepo("https://github.com/adamglassner/boardwalk.git", out _, out var r2) && r2 == "boardwalk"
                && GitHubService.TryParseRepo("github.com/a-b/c.d/issues", out var o3, out var r3) && o3 == "a-b" && r3 == "c.d"
                && !GitHubService.TryParseRepo("not a repo", out _, out _);
            var i = GitHubService.ParseLink("https://github.com/adamglassner/boardwalk/issues/34");
            var p = GitHubService.ParseLink("https://github.com/adamglassner/boardwalk/pull/7#discussion");
            return repo && i is { IsPull: false, Number: 34, Repo: "boardwalk" } && p is { IsPull: true, Number: 7 }
                && GitHubService.ParseLink("https://example.com/issues/3") == null;
        });

        await Test("Importing GitHub issues makes cards once", async () =>
        {
            var issues = new List<GitHubIssue>
            {
                new() { Number = 41, Title = "Crash on start", Body = "It crashes.", Url = "https://github.com/example/app/issues/41", Labels = { "bug" } },
                new() { Number = 42, Title = "Add export", Url = "https://github.com/example/app/issues/42", Labels = { "enhancement" } },
            };
            int n = BoardStore.ImportIssues(sw, issues);
            int again = BoardStore.ImportIssues(sw, issues);
            var c = sw.AllCards.First(x => x.Link.EndsWith("/41"));
            var c2 = sw.AllCards.First(x => x.Link.EndsWith("/42"));
            bool labels = sw.Label(c.LabelIds[0]).Name == "Bug" && sw.Label(c2.LabelIds[0]).Name == "Feature" && c.Notes == "It crashes." && c.LinkState == "open";
            var dlg = new ImportIssuesDialog(sw, "example/app", issues.Append(new GitHubIssue { Number = 43, Title = "New one", Url = "https://github.com/example/app/issues/43" }).ToList());
            bool picks = dlg.Selected.Count == 1 && dlg.Selected[0].Number == 43;
            await ShowDialogForShot(dlg, "dialog-import-issues");
            c2.Link = "https://github.com/example/app/pull/9";
            c2.LinkState = "merged";
            RenderBoard();
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(x => x.Tag == sw.ColumnOf(c2));
            bool badge = ContainsText(col, "#9 Merged") && ContainsText(col, "#41 Open");
            BoardStore.DeleteCard(sw, c);
            BoardStore.DeleteCard(sw, c2);
            RenderBoard();
            return n == 2 && again == 0 && labels && picks && badge;
        });

        await Test("The GitHub token is stored encrypted", () =>
        {
            SettingsService.GitHubToken = "github_pat_test123";
            bool ok = SettingsService.GitHubToken == "github_pat_test123" && !SettingsService.Current.GitHubTokenProtected.Contains("test123") && GitHubService.HasToken;
            SettingsService.GitHubToken = "";
            return ok && !GitHubService.HasToken;
        });

        await Test("Attachments: pasted images are saved with the card", async () =>
        {
            var c = BoardStore.AddCard(sw, todo, "Screenshot of the bug");
            var dlg = new CardDialog(sw, c);
            var bmp = new WriteableBitmap(64, 40, 96, 96, PixelFormats.Bgra32, null);
            var px = Enumerable.Repeat(unchecked((int)0xFF2563EB), 64 * 40).ToArray();
            bmp.WritePixels(new Int32Rect(0, 0, 64, 40), px, 64 * 4, 0);
            dlg.AttachBitmap(bmp, "shot.png");
            var file = Path.Combine(BoardStore.Folder, "notes.txt");
            File.WriteAllText(file, "hello");
            dlg.Owner = this;
            dlg.Show();
            await Settle();
            bool saved = dlg.Save(close: false);
            dlg.Close();
            BoardStore.AttachFile(c, file);
            BoardStore.Touch(sw);
            bool ok = saved && c.Attachments.Count == 2 && c.Attachments.All(a => File.Exists(BoardStore.AttachmentPath(a)))
                && BoardStore.AttachmentName(c.Attachments[1]) == "notes.txt" && BoardStore.AttachmentName(c.Attachments[0]) == "shot.png";
            var dlg2 = new CardDialog(sw, c);
            await ShowDialogForShot(dlg2, "dialog-card-attachments");
            RenderBoard();
            await Settle();
            var col = ColumnsPanel.Children.OfType<Border>().First(x => x.Tag == todo);
            ok &= ContainsText(col, "2");
            var paths = c.Attachments.Select(BoardStore.AttachmentPath).ToList();
            BoardStore.DeleteCard(sw, c);
            bool keptForUndo = paths.All(File.Exists);
            int removed = BoardStore.CleanUpAttachments();
            RenderBoard();
            return ok && keptForUndo && removed == 2 && !paths.Any(File.Exists);
        });

        await Test("Swimlanes by priority and by label", async () =>
        {
            var c = BoardStore.AddCard(sw, todo, "Lane test");
            sw.Lanes = LaneMode.Priority;
            BoardStore.Touch(sw);
            RenderBoard();
            await Settle();
            bool cells = CardPanels.ContainsKey(todo.Id + "|Urgent") && CardPanels.ContainsKey(done.Id + "|None") && CardPanels.Count == sw.Columns.Count * 5;
            bool inNone = CardPanels[todo.Id + "|None"].Children.OfType<FrameworkElement>().Any(x => x.Tag == c);
            MoveCard(sw, c, doing, -1, "Urgent");
            await Settle();
            bool moved = c.Priority == Priority.Urgent && sw.ColumnOf(c) == doing && CardPanels[doing.Id + "|Urgent"].Children.OfType<FrameworkElement>().Any(x => x.Tag == c);
            sw.Lanes = LaneMode.Label;
            var bugId = sw.Labels.First(l => l.Name == "Bug").Id;
            MoveCard(sw, c, doing, -1, bugId);
            bool labelled = c.LabelIds.Contains(bugId) && CardPanels.ContainsKey(doing.Id + "|" + bugId);
            MoveCard(sw, c, doing, -1, "");
            bool unlabelled = !c.LabelIds.Contains(bugId);
            BoardStore.DeleteCard(sw, c);
            sw.Lanes = LaneMode.None;
            BoardStore.Touch(sw);
            RenderBoard();
            return cells && inNone && moved && labelled && unlabelled && BoardScroll.Content == ColumnsPanel;
        });

        await Test("My week lists cards due soon on every board", async () =>
        {
            var c1 = BoardStore.AddCard(sw, todo, "Due today card");
            c1.Due = DateTime.Today;
            var c2 = BoardStore.AddCard(std, std.Columns[0], "Overdue on the other board");
            c2.Due = DateTime.Today.AddDays(-2);
            var c3 = BoardStore.AddCard(sw, todo, "Next month card");
            c3.Due = DateTime.Today.AddDays(30);
            BoardStore.Touch(sw);
            ShowWeek();
            await Settle();
            bool listed = WeekShowing && WeekView.Visibility == Visibility.Visible && ContainsText(WeekPanel, "Due today card")
                && ContainsText(WeekPanel, "Overdue on the other board") && !ContainsText(WeekPanel, "Next month card")
                && MyWeekBadge.Visibility == Visibility.Visible;
            var text = ReminderService.ReminderText(out var title);
            bool reminder = text != null && title.Contains("due today") && title.Contains("overdue") && text.Contains("Due today card");
            BoardStore.MoveCard(std, c2, std.FirstDoneColumn, -1);
            RenderWeek();
            await Settle();
            bool completed = !ContainsText(WeekPanel, "Overdue on the other board");
            BoardStore.DeleteCard(std, c2);
            BoardStore.DeleteCard(sw, c1);
            BoardStore.DeleteCard(sw, c3);
            SelectInList(sw);
            ShowBoard(sw);
            return listed && reminder && completed && !WeekShowing;
        });

        // ---------------- demo content for the screenshots ----------------
        BuildDemoCards(sw);
        RefreshBoardList(sw);

        await Test("Screenshots of swimlanes and My week", async () =>
        {
            SettingsService.Current.Theme = ThemeMode.Light;
            SettingsService.Commit();
            sw.Lanes = LaneMode.Priority;
            RenderBoard();
            await Settle();
            Shot(this, "board-swimlanes");
            sw.Lanes = LaneMode.None;
            BoardStore.Touch(sw);
            ShowWeek();
            await Settle();
            Shot(this, "my-week");
            SelectInList(sw);
            ShowBoard(sw);
            return true;
        });

        // ---------------- themes ----------------
        await Test("Light mode", async () =>
        {
            SettingsService.Current.Theme = ThemeMode.Light;
            SettingsService.Commit();
            await Settle();
            Shot(this, "board-light");
            return !ThemeManager.IsDark && IsLight("BoardBg") && !IsLight("Text");
        });

        await Test("Dark mode", async () =>
        {
            SettingsService.Current.Theme = ThemeMode.Dark;
            SettingsService.Commit();
            await Settle();
            Shot(this, "board-dark");
            return ThemeManager.IsDark && !IsLight("BoardBg") && IsLight("Text") && (string)ThemeToggle.Content == "";
        });

        await Test("The sidebar button switches between Light and Dark", async () =>
        {
            ThemeToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Settle();
            bool light = !ThemeManager.IsDark && SettingsService.Current.Theme == ThemeMode.Light;
            ThemeToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Settle();
            return light && ThemeManager.IsDark;
        });

        await Test("Follow Windows mode", async () =>
        {
            SettingsService.Current.Theme = ThemeMode.System;
            SettingsService.Commit();
            await Settle();
            return ThemeManager.IsDark == ThemeManager.SystemUsesDark();
        });

        await Test("Theme choice is saved", () =>
        {
            SettingsService.Current.Theme = ThemeMode.Dark;
            SettingsService.Commit();
            SettingsService.Load();
            bool ok = SettingsService.Current.Theme == ThemeMode.Dark;
            SettingsService.Current.Theme = ThemeMode.Light;
            SettingsService.Commit();
            return ok;
        });

        await Test("Compact cards", async () =>
        {
            SettingsService.Current.CompactCards = true;
            SettingsService.Commit();
            await Settle();
            Shot(this, "board-compact");
            SettingsService.Current.CompactCards = false;
            SettingsService.Commit();
            await Settle();
            return sw.Columns.Sum(VisibleCards) == sw.ActiveCardCount;
        });

        // ---------------- dialogs ----------------
        foreach (var dark in new[] { false, true })
        {
            SettingsService.Current.Theme = dark ? ThemeMode.Dark : ThemeMode.Light;
            SettingsService.Commit();
            await Settle();
            var suffix = dark ? "-dark" : "";
            await Test("Card dialog opens" + suffix, async () => { await ShowDialogForShot(new CardDialog(sw, doing.Cards.First(c => c.Title == "Crash when saving")), "dialog-card" + suffix); return true; });
            await Test("New board dialog opens" + suffix, async () => { await ShowDialogForShot(new NewBoardDialog(), "dialog-new-board" + suffix); return true; });
            await Test("Board settings dialog opens" + suffix, async () => { await ShowDialogForShot(new BoardDialog(sw), "dialog-board" + suffix); return true; });
            await Test("Settings opens" + suffix, async () => { await ShowDialogForShot(new SettingsWindow(this), "dialog-settings" + suffix); return true; });
            await Test("Changelog opens" + suffix, async () => { await ShowDialogForShot(new ChangelogDialog(sw), "dialog-changelog" + suffix); return true; });
            await Test("Stats open" + suffix, async () => { await ShowDialogForShot(new StatsDialog(sw), "dialog-stats" + suffix); return true; });
            await Test("Text prompt opens" + suffix, async () => { await ShowDialogForShot(new TextPromptDialog("Rename column", "Column (status) name:", "In progress"), "dialog-prompt" + suffix); return true; });
        }

        await Test("Sync is shown as coming soon", () => ContainsText(SyncCard, "Coming soon") && SettingsWindow.WhatsNew().Length > 0);

        await Test("Deleting the last boards shows the empty state again", async () =>
        {
            foreach (var b in BoardStore.Boards.ToList()) BoardStore.DeleteBoard(b);
            RefreshBoardList();
            await Settle();
            return CurrentBoard == null && EmptyState.Visibility == Visibility.Visible && BoardList.Items.Count == 0;
        });

        await Test("Undo brings deleted boards back", () =>
        {
            UndoRedo(true);
            return BoardStore.Boards.Count == 1 && CurrentBoard != null && BoardList.Items.Count == 1;
        });
    }

    private bool IsLight(string brushKey)
    {
        var c = ((SolidColorBrush)FindResource(brushKey)).Color;
        return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 128;
    }

    private static T FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        foreach (var child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject d && FindChild<T>(d) is T found) return found;
        return null;
    }

    /// <summary>Fills the test board so the screenshots show a realistic board.</summary>
    private void BuildDemoCards(Board b)
    {
        Card Add(int col, string title, string label = null, Priority p = Priority.None, int? dueIn = null, int items = 0, int itemsDone = 0, string notes = "")
        {
            var c = BoardStore.AddCard(b, b.Columns[col], title);
            if (label != null) c.LabelIds.Add(b.Labels.First(l => l.Name == label).Id);
            c.Priority = p;
            if (dueIn is int d) c.Due = DateTime.Today.AddDays(d);
            for (int i = 0; i < items; i++) c.Checklist.Add(new CheckItem { Text = "Step " + (i + 1), Done = i < itemsDone });
            c.Notes = notes;
            return c;
        }
        Add(0, "Keyboard shortcuts page", "Docs");
        Add(0, "Import from CSV", "Feature", Priority.Low);
        Add(1, "Tidy up the toolbar icons", "Design", Priority.Medium, 5);
        Add(1, "Faster start-up", "Improvement", Priority.High, 2, 4, 1, "Measure first, then cache the board list.");
        Add(2, "Drag and drop between columns", "Feature", Priority.High, 1, 3, 2);
        Add(3, "Sync between PCs", "Feature", Priority.None, null, 0, 0, "Waiting on the sync server design.");
        Add(4, "Release notes for 1.0", "Docs", Priority.Medium, 3, 2, 2);
        var setup = Add(5, "Project set-up", "Improvement");
        setup.Version = "1.0.0";
        var api = Add(2, "Settings window", "Feature", Priority.Medium, 4);
        var sub1 = BoardStore.AddSubTask(b, api, "Theme picker");
        sub1.Due = DateTime.Today;
        BoardStore.AddSubTask(b, api, "GitHub token box");
        var docs = b.AllCards.First(c => c.Title == "Keyboard shortcuts page");
        docs.BlockedBy.Add(api.Id);
        var gh = Add(4, "Crash when opening an old board", "Bug", Priority.Urgent, 0);
        gh.Link = "https://github.com/example/app/issues/12";
        gh.LinkState = "open";
        var weekly = Add(1, "Update dependencies", null, Priority.None, 6);
        weekly.Repeat = RepeatKind.Weekly;
        BoardStore.Touch(b);
    }
}
