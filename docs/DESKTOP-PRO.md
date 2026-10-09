# Desktop Pro — approved Windows preview

Krishna approved Desktop Pro and a new PR on 9 October 2026. Keep this PR unmerged until Krishna approves merging after practical testing. Android keeps its existing design; the macOS folder remains empty.

## Behavior changes announced before building

The mode picker moves to the far left. The Windows app opens on Overview, with vertical navigation, neutral blue-grey task styling and compact corner radii. Tasks opens on task lists by default; existing ungrouped tasks stay in Unsorted. A global lists-first setting and Tasks Inherit / On / Off override preserve the classic all-tasks opening view when requested. The minimum window width is 1040 logical pixels for the detailed desktop controls.

## Real pages and controls

Overview shows real counts, upcoming tasks, quick navigation and call backs. Reminders filters Upcoming / Overdue / Repeating / Snoozed with existing edit, complete, snooze and delete actions. Tasks supports creating, pinning, renaming, deleting and searching lists, selecting a list before quick-add, Active / Done views and moving a task between lists in the editor. Deleting a list keeps its tasks and schedules in Unsorted; Undo restores the list.

Settings has nine categories: General, Appearance, Reminders, Tasks, Learn, Calls, Shopping, Data & backup and Updates. Existing startup, tray, snooze, time, sharing, import/export, Bin and update controls remain functional. Compact rows is a saved Windows appearance preference applying to the whole desktop workspace, so it is inherently global. Learn and Calls provide routes to their actual editors and shared reminder defaults. Proposed HTML controls without a Windows implementation (cloud sync, automatic call-log detection, custom themes, session targets) are not presented as working switches.

The installed version remains in the header. A top-right `Update to vx.y.z` button appears only when a newer eligible GitHub release exists. Stable updates remain the default; beta is optional.

## Data and compatibility

Task lists use Android's `taskLists` record schema, with numeric `tasksListsFirst` (-1 inherit, 0 off, 1 on). Task and Shop list identities are scoped by item tab. Legacy task groups migrate once; an explicit missing/deleted task list maps to Unsorted. Import merges list records by newest `updatedAt`, including deletion tombstones, and keeps Windows-only preferences. Save/reload preserves list assignments and reminders.

## Verification

- 83 Windows unit tests passed, including five new list/import/persistence regressions.
- Five release-policy/ZIP tests passed locally.
- Each new regression was run with its corresponding missing behavior: deleted-list filtering removed, task-list import removed, newest-wins reversed, opening-view rule disabled and group migration removed. All five negative controls failed as expected; restored implementation passed.
- Actual WPF smoke tests open all nine workspace pages, all nine settings categories, task/list/learning/shopping/call/catalog editors and reminder alerts. They click quick-add and list create/rename/delete controls, verify Undo, save/reload and capture minimum-width layout. A second process verifies persisted preferences and task-list assignment before reseeding.
- Screenshots inspected for Overview, Tasks and Settings. User data is untouched: local checks use an isolated data directory.

## Class sweep

| Mechanism / consumers | Disposition |
| --- | --- |
| Mode picker, sidebar navigation, tray task entry and reminder navigation (`MainWindow`) | Fixed: left picker, tab visibility, list selection and list-first tray route |
| Task list identities: `TaskLists`, `Storage.Heal` / `MergeInto`, `TaskListsView`, `ItemsView`, `ItemEditor` | Fixed: scoped IDs, migration, import, selected-list add/edit/delete, retained schedules |
| Shopping `ListId` consumers (`ShopLists`, `BuyView`, `ListDialogs`, Shop editor and reminders) | Safe: separate Shop list rules; existing smoke flows retained |
| Settings startup/tray/reminders/sharing/backup/Bin/update actions | Safe: same actions moved into category pages |
| Update banner / startup coordinator / beta policy | Safe: eligibility and version logic retained; no-update banner assertion retained |
| Cached task list item view | Fixed: preserve search and Active / Done view during mutations |

## Practical device checklist before merge

1. Download the PR beta ZIP and run `portable/Remindly_<version>.exe`. Check the version, left mode picker, Overview and every sidebar page.
2. Create Work and Personal lists; add a dated repeating task; edit its list, notes and priority; complete and undo it. Search lists and tasks, and check Active / Done.
3. Rename and delete a list. Verify its tasks and reminders remain in Unsorted, then Undo. Import an Android task-list backup twice and check for duplicate lists.
4. Change compact rows, Tasks opening-view override, snooze and default reminder hour. Verify effects, close and reopen, then reboot and verify again. Reboot is a user device check; the automated run does not reboot the computer.
5. Switch Shop mode and check Buy lists, Shops, Products and sharing. Return to Task mode.
6. Export a backup, restore a deleted item from Bin and check optional beta updates. The update button should be absent when no newer eligible release exists.
7. Record any layout/behavior feedback on the PR. Do not merge automatically.
