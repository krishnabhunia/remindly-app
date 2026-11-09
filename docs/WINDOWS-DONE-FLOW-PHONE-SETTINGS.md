# Windows: Done animation and the phone's settings (2.20.0)

Krishna approved the "Remindly Done Animation & Phone Settings" canvas on 9 November 2026 with the default answers: Windows follows the phone's settings read-only, the phone's ring time and snooze replace the PC-only ones, no optional items, shipped after #27.

## Done flow (M1–M5)

| SrNo. | Step | Timing | Code |
|---|---|---|---|
| 1 | Tick: checkbox stays ticked, the name is struck through, a "Moving to Done in Ns · UNDO" chip with a draining bar | the phone's per-tab delay (`delayFor`) | `DoneFlow.Complete`, `DoneFlow.Strip` |
| 2 | UNDO or untick during the countdown: nothing was saved | — | `DoneFlow.Cancel` |
| 3 | Green layer and popping tick over the card; card scales to 90 % and fades | 380 ms | `DoneFlow.Celebrate` (adorner) |
| 4 | The gap closes, cards below slide up; then the item is saved as done | 280 ms | `DoneFlow.Collapse` |
| 5 | Delay Off: steps 3–4 at once, then "Moved to Done · Undo" | 2.6 s snackbar | `ItemsView.Toggle` |
| 6 | Untick in Done: slide right and fade, close up, save, glow when shown in Active | 340 ms + 280 ms, glow 1.1 s | `DoneFlow.Revive`, `DoneFlow.Track` |
| 7 | Windows "Animation effects" off: fade only | 150 ms | `SystemParameters.ClientAreaAnimation` |

The view does not switch to Active after an untick (Android does not either); the Active count pops.

## Phone settings (read-only on the PC)

`PhonePrefs` (Remindly.Core) holds the phone's AppSettings keys with the Kotlin names and defaults. It is filled from the synced settings document (`CloudListSettings.Reconcile`) or an Android backup (`DataStore.Parse`), kept in the PC data file as `settings.pcPhonePrefs`, and never written to the cloud. The newer copy wins on merge.

| SrNo. | Setting | Android resolver | PC effect |
|---|---|---|---|
| 1 | Move-to-Done delay | `delayFor` | Countdown length per tab and Calls |
| 2 | Auto-clear Done items | `clearFor` | Done records older than N days (by `doneAt`) go to the Bin in the reminder sweep; returning / unexpired Buy items are kept. The phone hard-deletes; the PC uses the Bin so it syncs and can be undone |
| 3 | Delete style | `gesturesFor(...).deleteStyle` (`tabGestureOv`) | Confirm before Delete, or the Undo snackbar |
| 4 | Card details, reminder-type chip | `cardFieldsFor`, `cardAlertStyleFor`, `cardAlertColorFor` | Hides checkbox / date / priority / repeat on cards in all designs; shows the Alarm / Ring chip |
| 5 | Medium tag | `showPriorityTag` | Medium chip shown or hidden |
| 6 | Sort | `sortOf` | DATE (PC day groups), GROUP, PRIORITY, NONE; inside a task list GROUP is DATE; the Day Board keeps its columns |
| 7 | Visible tabs | `isTabVisible` | Menu entries and the start tab |
| 8 | Count badges | `badges` | Menu counts |
| 9 | Call card details | CallsScreen | Number, missed-at, missed count, note, label, next |
| 10 | Time format | `timeFormatterFor` | `Clock.FormatTime` |
| 11 | Date-only ring time | `defaultDueMinutes` | Editor default time, Move to today |
| 12 | Snooze length | `snoozeMinutes` | Cards, menus, reminder pop-ups |
| 13 | New item's due date and schedule | `defaultNewDue`, `schedDefaultFor` | Quick add, Buy add, New task editor |
| 14 | Alerts per tab | `alertsEnabledFor` | Pop-ups for that tab are dropped (marked shown), as on the phone |

Not applied: the overdue reminder (`*OverdueNagHours` is stored on the phone but never used there), "Show Delete on Done items" (on the phone it hides only the bulk clear icon on Done headers, which the PC does not have), gestures, fonts, theme, geofence, calendar, call log.

## Tests

`PhonePrefsTests` (18) cover defaults, malformed values, every inherit rule, new-item dates, sync and backup reading, muted alerts, auto-clear and sort grouping. `TaskWorkspaceSmoke` checks the countdown, UNDO, completion after the countdown in all four designs, revive, delay Off, the Calls countdown, hidden checkbox, quick-add date, hidden tab and the Settings section.
