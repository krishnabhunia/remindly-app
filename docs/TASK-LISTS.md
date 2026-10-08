# Tasks lists first

Approved by Krishna on 8 October 2026 after reviewing the interactive HTML designs. The selected design is **Shop-style cards**; the compact-row alternative remains in [the approved prototype](designs/task-lists-approved.html) for reference.

Tasks opens on a list overview. Create a named list, choose its icon and optional pin, then add tasks inside it. Cards show active/done counts and progress. Lists can be searched and sorted by recent activity or name. List detail keeps the existing task editor, reminders, priority, notes, search and Active/Done views.

Existing Task groups, including empty configured groups, become lists. Ungrouped tasks appear in Unsorted. Unsorted permits editing existing tasks but has no new-task composer. New tasks and duplicates require a live list when lists-first is enabled. Deleting a list keeps its tasks, reminders and completion status in Unsorted. Task and Buy list records are separate even if they share a name.

Global Settings → Lists → Lists before tasks defaults to On. Tasks settings → Tasks opening view offers Inherit / On / Off. Off retains the classic Tasks view without removing list data. No release/version bump is included.

## Persistence and mutation sweep

| Consumer | Disposition |
|---|---|
| `MainActivity.ListSection` | Routes Tasks through its own list overview/detail; Shop reopening and Buy Now remain scoped to Shop. |
| `ListScreens.ListPage` / `AddEditSheet` | Scopes Task items, prefills list membership and checks every new-task/duplicate save path. Existing Unsorted edits remain available. |
| `ListScreens.GroupMenuSheet` | Task group actions use Task list options, preventing legacy rename/delete from diverging from the registry. |
| `SettingsScreen` | Global and Tasks override; list management uses TaskListStore; combined backup conversion carries list records. |
| `TaskListStore` | Create/edit/pin/move/delete; deletion tombstones a list and clears membership without changing reminder fields. |
| `Stores.init` / `RemindlyApp` | Idempotent group migration; soft-deleted tasks do not seed new lists. |
| `SyncRepo` | Merges Task list data by record timestamp, including empty lists and tombstones, independent of preference-document age. |
| `Backup` | Data, settings and combined imports preserve the registry and reconcile legacy groups. |
| `keepListData` / settings reset | Discard/reset retain Task and Buy list data. |
| Sharing | Shared inbox remains available on Tasks home; pending Android shares wait for a real list before opening the task editor. |

Automated coverage lives in `TaskListsTest.kt`: migration/idempotency, Task/Shop separation, counts/sorting, save gating, real store create/rename/move/delete and reload, backup roundtrip/import, reset/discard and sync deletion convergence. Existing schema expectations advance from 43 to 44. The local machine has no Android SDK or Gradle, so the PR's existing GitHub Actions workflow is the build/test gate.

Physical-device validation and negative-control results are recorded separately in the PR and device checklist; no device pass is claimed by this document.
