# Windows workspace repair — approved Design R1

Krishna approved Design R1 in the project conversation on 9 November 2026 and requested implementation through a GitHub pull request. Approval is for the design and PR, not a merge to main.

## Navigation and interaction contract

| Context | Selected view | Primary action | Visible controls |
|---|---|---|---|
| Lists overview | Lists | New list | List search, A–Z/Recent sort; no task composer or Active/Done |
| Inside a named list or Unsorted | Breadcrumb shows current list | New task | Back, Tasks > Lists > name, scoped search and Active/Done; no root All tasks switch or New list |
| All tasks | All tasks | New task | Root view switch, scoped status counts, task search and explicit quick-add destination; no New list |

One click on a list name/row opens its contents. One click on a task name/pencil opens its editor. Checkbox completion and overflow menus are independent controls and never trigger parent navigation or editing. Buttons support keyboard activation and visible focus. Alt+Left goes back, Ctrl+F searches the current scope, and Ctrl+N follows the current context, including the open list.

Back restores each view's query, Active/Done filter, scroll and unsubmitted quick-add text. Clicking a list badge from filtered All tasks and then Back restores All tasks rather than discarding its filter. Navigation state is local to the desktop page, never cloud-synced. Switching desktop designs recreates page controls and is not claimed to persist navigation history across application restarts.

The four existing designs retain their task layouts: Fluent compact full-width rows, Day Board date columns, Command Dark table, Today Hub day tiles. All receive the same navigation and single-click rules. Empty lists show Empty list rather than an ambiguous zero-progress line. Search misses say No matching tasks/lists, distinct from empty collections.

## Editing and data safety

Task/list editors protect unsaved drafts on Cancel, Escape and window close. Save, discard and keep-editing choices are explicit. Invalid saves leave the editor open. Existing task/list changes made after opening an editor are detected before overwriting its snapshot. Creating a task outside a specific list requires a destination or explicit Unsorted choice. Existing unknown list IDs are preserved until metadata arrives; known-deleted lists cannot be selected for a new save.

The editor retains notes, dates, priority, personal flags, reminders and all recurrence options. Task status uses the existing completion/revival rules rather than directly setting done. Reminder and repeat controls are grouped in an expander.

List removal is behind an overflow menu with confirmation. Tasks are kept in Unsorted and a tombstone is retained. Undo restores only the deleted list and memberships unchanged since that removal; it does not roll back the entire application snapshot. If synchronized settings remove the currently open list, navigation returns to the overview without deleting any task.

## Implementation

- TaskNavigation.cs: pure page state, Back memory and quick-add eligibility.
- TaskListNavigation.cs: context-aware toolbar, list rows, breadcrumbs, destination controls and safe list removal.
- ItemsView.cs: single-click title buttons in all designs, isolated completion controls, list badges and scoped empty states.
- WorkspaceUi.cs: opt-in accessible workspace buttons and search hints; the global Ui.Card double-click helper is not changed.
- MainWindow.TaskNavigation.cs: scoped keyboard handling before the old generic Ctrl+N handler.
- ItemEditor.cs / EditorWindow.cs: task destination/status editing, stale-edit checks and opt-in dirty-draft protection.

Android sources, Firestore transport/schema/rules, OAuth settings, signing, release workflow and macOS are unchanged. The existing unified pipeline determines the patch beta version and publishes one Remindly_<version>.zip only after its gates pass.

## Verification

TaskNavigationTests covers navigation modes, Back memory, explicit destinations, stale list IDs and empty/search states. TaskWorkspaceSmoke is called by the existing Windows smoke test and exercises native WPF button Click events, editor save/cancel/discard, checkbox isolation, scope visibility, badges and scroll restoration across all four designs using the isolated smoke-test data directory. These are component/UI automation checks, not a human physical-mouse test or a live Firebase test.

Before merge, review the latest commit's complete CI run. On a real Windows machine, also verify mouse hit targets, screen-reader announcements, long names, 100/125/150% scaling, a narrow window, keyboard tab order, dirty editor closure and navigation during incoming sync. No device data reset or reinstallation is required for verification.
