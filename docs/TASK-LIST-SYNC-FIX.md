# Android–Windows task-list synchronization repair

## Diagnosis

The reported v2.16.0 screenshots show Android's list overview (nine lists, three active tasks) and Windows' flat task page (three active tasks). Equal counts do not prove that the task records match, but the code confirms why the lists do not match: Windows lacked a TaskList model, ignored taskLists in the settings/app cloud document, merged only shopping-list metadata and had no task-list navigation or editor field.

This is a Windows data-model, sync and presentation gap, not evidence that the user chose the wrong Google account. The Android TaskList wire format already exists; the fix does not require a new Firebase collection or new security rules. It does not change Android authentication or listener-retry behavior.

## Implementation

| Location | Repair |
|---|---|
| windows/src/Remindly.Core/Models.cs and TaskLists.cs | Model task lists separately from shopping lists; retain IDs, empty lists, icons, pins and tombstones. Preserve unknown task-list fields. |
| windows/src/Remindly.Core/Storage.cs | Merge and persist task-list metadata, including Android data-only backup aliases. Do not clear an item's explicit list ID when metadata has not arrived yet. |
| windows/src/Remindly.Core/Sync/CloudListSettings.cs | Read taskLists and patch newer local list changes into the existing Android settings JSON, keeping unrelated preferences intact. |
| windows/src/Remindly.Core/Sync/DesktopCloudSync.cs | Reconcile list settings; make manual Sync now a full read; record actual full-read times per collection; report invalid cloud records. |
| windows/src/Remindly.App/Views/TaskListNavigation.cs, ItemsView.cs and ItemEditor.cs | Display list cards, open list-scoped tasks, create/edit/remove task lists and select task membership while preserving the four existing designs. |

Removing a task list retains a deletion marker and moves its tasks to Unsorted; it does not delete the tasks. Legacy group names are migration input, not a second authoritative list store. Existing explicit membership is retained while list metadata is unavailable.

## Important limits

Automatic 15-second delta polling still uses client updatedAt timestamps. An offline write arriving with a timestamp below the collection watermark can still be absent from a delta response. This patch provides recovery through manual Sync now, the initial full read and the corrected daily full read; it does not implement a server-timestamp cursor or claim immediate delivery of every late offline write.

A cloud settings document missing while local list metadata exists produces an explicit initialization error. The desktop does not manufacture a partial current-schema settings document that could reset Android preferences. Android must initialize its existing settings document first.

Cities, chains and product catalogues remain local-only as documented in CLOUD-SYNC-WINDOWS.md. The UpToDate state confirms a successful supported sync cycle, not a comparison against another physical device. Backend permissions, actual account identity and real-device convergence remain separate acceptance checks.

## Automated coverage

TaskListSyncTests covers nine lists with three active tasks, empty-list persistence, Android backup import, membership, task/shopping separation, rename safety, deletion ties, unknown fields and full-refresh scheduling. TaskListSyncCycleTests exercises the real DesktopCloudSync loop and FirestoreTransport with a synthetic HTTP handler: pull Android-shaped lists, upload a Windows list edit, preserve phone preferences and recover a late record through manual and daily full reads. These tests are not live Firebase tests.

## Device acceptance checklist

Keep recoverable exports from both devices before testing a beta. Do not clear app data or reinstall as a troubleshooting step.

1. Use the PR build on Windows with the existing Google account and explicit sync opt-in; keep Android cloud sync enabled. Choose Windows Sync now.
2. Compare the named task-list overview on both devices, including empty lists. Then open the SAME list and compare task titles and Active/Done states, not only the total counts.
3. Create and rename a list on Android; verify Windows. Repeat Windows to Android. Confirm icons, pins and stable membership.
4. Create or edit a task inside a list on each device, complete it and move it to another list. Confirm the other device matches.
5. Remove a list in Windows and verify its tasks remain in Unsorted; reconnect the phone and confirm the removed list does not reappear.
6. Make a change while offline, reconnect and run Windows Sync now. Check the late change and existing newer records both survive.
7. Restart Windows, then verify lists, task membership and saved desktop appearance. Exercise Fluent, Day Board, Command Dark and Today Hub navigation.
8. Confirm malformed-record or initialization errors are visible and do not silently erase local data.

Do not mark live two-device verification complete or close issue #16 based solely on green automated tests. PR #24 is not authorization to merge.
