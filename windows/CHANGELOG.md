# Remindly for Windows — changelog

Each `## X.Y.Z` section becomes the notes of GitHub Release `win-vX.Y.Z`. The CI refuses to build a
version that has no section here.

## Next minor release — four designs to choose from (09-Oct-2026)

**Settings → Appearance** now offers four designs for the Windows app. Design **A** is the default; the
choice is kept on this PC only (Android is unchanged, and importing a backup never changes it).

| SrNo. | Design | Layout | Look |
|---|---|---|---|
| 1 | **A · Fluent** (default) | left menu with the Task/Shop switch and counts; your list with a **details pane** beside it | Windows 11: Segoe UI, light grey ground, indigo / green |
| 2 | **B · Day Board** | top bar with a centred Task/Shop pill and section tabs; Tasks/Learn as **Overdue · Today · Tomorrow · Later columns** (with "Move all to today" and "+ Add to today"); Buy shows your **lists side by side** | Manrope, blue / orange |
| 3 | **C · Command Dark** | dark sidebar with **quick views** (Today, Overdue, Next 7 days, Repeating, Personal) and your lists; a **Ctrl+K command bar** that adds to the open section; dense table rows; Buy opens on **everything to buy, grouped by shop** | dark, IBM Plex, violet / mint |
| 4 | **D · Today Hub** | new **Today** home (due today with overdue, call-backs with Call/WhatsApp, next 7 days, learning progress, today's shopping) and a Shop **Overview** (list progress rings, Buy now by store, cheapest shop seen, next trip) | rounded tiles, Plus Jakarta Sans, violet / green |

- Every design keeps the installed version in the main window and the top-right **Update to vX.Y.Z** button,
  which appears only when GitHub offers a newer eligible version.
- New keyboard shortcuts in every design: **Ctrl+1…4** open the menu entries, **Ctrl+M** switches Task / Shop.
- Right-click a task (board cards, rows, tiles) for Edit · Done · Snooze · **Move to today** · Open link · Delete.
- The UI smoke test now opens every screen in every design.
- Fonts bundled under the SIL Open Font License 1.1: Manrope, IBM Plex Sans / Mono, Plus Jakarta Sans.

## 2.11.0 — first Windows release, in step with Android 2.11 (29-Sep-2026)

**Remindly now runs on Windows 10 and 11**, with the same records as the phone.

- **Task mode** — Tasks · Learn · Calls. Quick add, Active / Done, grouped Overdue · Today · Tomorrow ·
  later days, priorities, repeats (daily, weekly on days, monthly on days or "second Monday / last
  Friday", quarterly, yearly, every N, spaced revision), end after N times, snooze, Personal items.
  Completing a recurring item moves it to Done and it comes back on its next day — exactly like Android.
- **Shop mode** — Buy · Shops · Products. **The Buy tab opens on your Lists (Android 2.11):** each card
  shows what is left to buy, a progress bar, where the items are bought, an estimated total and the
  shopping day. Sort by Recent, A–Z or your own order; pin favourites. New list: name, icon, usual
  shop, shopping day (reminder at 9 AM), Private.
- **List menu** — send to WhatsApp, share (copy), preview what is shared, pin, edit, make default,
  duplicate, restart, mark all bought, merge into another list, delete (keep the items in Unsorted,
  move them, or delete them) — with Undo.
- **Inside a list** — everything you add belongs to it; group by date, shop, category or priority;
  "recently bought in this list" and product suggestions while typing; a warning when the item is
  already on another list. **Buy Now** shows every open item from every list, optionally for one shop,
  with "Bought everything here".
- **Sharing** uses the Android format: `Groceries:-`, a blank line, then `1. Milk - 2 / L - Urgent - Bought`.
- **Reminders** pop up bottom-right (Done · Snooze · Open; Call · WhatsApp for call-backs); Ring keeps
  sounding until you act. Remindly keeps running in the notification area and can start with Windows.
- **Import your phone's data** — Settings → Backup → Import reads the Android backup file
  (`remindly-data-*.json`); records merge by id, the newer copy wins, importing twice changes nothing.
- **Version on the home window** — the header always shows "Version X.Y.Z · Installed / Portable".
- **Auto update** (Settings → Updates, on by default) — checks GitHub once a day, the first time on the
  next day, and installs a new version in the background. An installed copy runs the new Setup
  silently; a portable copy swaps its exe; either way Remindly restarts by itself when you are not
  using it. The download is verified (SHA-256) before anything is installed.
- **Setup detects a running Remindly** — it closes it gracefully, updates it and starts it again.
- **Download** — one zip with two folders: `installer/` (Setup) and `portable/` (Remindly.exe).

Stays on the phone: geofenced shop arrivals, call-log detection of missed calls, maps, the home-screen
widgets and cloud sync.
