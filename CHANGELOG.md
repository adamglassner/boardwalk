# Changelog

All notable changes to Boardwalk. Newest first.

## 1.1.0 - 2026-11-17

### Added
- **Undo and redo** (Ctrl+Z, Ctrl+Y, and buttons next to search) for every change since Boardwalk started, including deleted cards, columns and boards.
- **Quick add:** type "Fix crash #bug !high friday" in Add a card to set the label, priority and due date as you go. A due date can be today, tomorrow, next week, a weekday, or @ and a date (@11/20).
- **My week:** cards due in the next seven days on all your boards in one list (Overdue, Today, Tomorrow, Later this week), with a Complete button. The sidebar shows how many are due today or overdue.
- **Reminders:** a Windows notification once a day for cards due today or overdue. Boardwalk can keep running in the notification area when its window is closed, and start with Windows.
- **Releases and changelog:** give cards a version, set the version you're working on so completed cards get it automatically, and use Board options › Write changelog to turn a version's completed cards into a CHANGELOG section (Added, Changed, Fixed).
- **GitHub link-up:** import a repository's open issues as cards, see whether a linked issue or pull request is open, closed or merged, and close the issue automatically when its card is completed (needs a token, stored encrypted).
- **Blocked by:** mark a card as waiting on other cards; it shows "Blocked by #12" until they're completed.
- **Sub-tasks:** split a card into smaller cards. The card shows how many are completed, and each sub-task shows what it's part of.
- **Attachments:** paste screenshots (Ctrl+V), add files or drag them onto a card. Images show as thumbnails.
- **Card templates:** new cards can start from a template. Software project boards come with Bug report and Feature templates, and any card can be saved as a template.
- **Repeating cards:** daily, weekly, monthly or yearly. Completing one makes the next with the next due date.
- **Card history:** when a card was made, moved, completed or archived, and how long it has been in its current status.
- **Board stats:** open, done this week, overdue, blocked, average days to finish, cards per status and cards completed each week.
- **Swimlanes:** rows across the board by priority or by label. Dragging a card into a row sets its priority or label.

### Changed
- The board menu has the new tools: Swimlanes, Write changelog, Board stats and GitHub.
- Board settings also cover the version you're working on, the GitHub repository and card templates.

## 1.0.1 - 2026-11-12

### Fixed
- Switching boards now starts the new board at its first column instead of where the last board was scrolled to.

## 1.0.0 - 2026-11-12

The first version of Boardwalk, a kanban board app for Windows 10 and 11 by Bytehawk Studio.

### Added
- **Multiple boards:** make a board for each project and switch between them in the sidebar. Rename, recolour, duplicate (with or without the cards), reorder and delete boards.
- **Statuses as columns:** new boards start with New, In progress, On hold and Complete. Add more from a list (Backlog, To do, Blocked, Review, Testing, Cancelled…) or type your own. Each status has a colour, and "Counts as complete" statuses tick off their cards.
- **Templates:** Standard, Software project (Backlog, To do, In progress, On hold, Review, Complete with Bug, Feature, Improvement, Design and Docs labels), Personal and Blank.
- **Cards** with a number (#12), labels, priority (Low to Urgent), due date (type a date or words like "tomorrow" or "friday"), a link, notes and a checklist.
- **Drag and drop** cards between statuses and to reorder them, or right-click a card to move it, even to another board.
- **Card limits** per status: the count turns orange when a status has too many cards.
- **Search** by title, notes, checklist, label or #number, and filter by label.
- **Archive** cards to hide them without deleting them, and bring them back later.
- **Export** a board as Markdown or copy it to the clipboard, export and import boards as files, and back up all boards at once.
- **Light and Dark mode** (or follow Windows), seven accent colours, a quick Light/Dark button in the sidebar, compact cards and three column widths.
- Boards are saved on this PC after every change, with a backup of the previous version. Sync between devices is coming soon.
