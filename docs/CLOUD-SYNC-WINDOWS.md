# Android ↔ Windows cloud sync (beta)

Both apps must sign in to the **same Google account in the same Firebase project**. The
Windows app uses the system browser and a Google **Desktop application** OAuth client;
it does not accept your Google password or an Android/Web OAuth client.

## One-time Google Cloud setup

1. In [Google Cloud credentials](https://console.cloud.google.com/apis/credentials?project=remindly-5c1e6),
   select the Firebase project already used by the Android Remindly APK.
2. Create **OAuth client ID → Desktop app** (for example, `Remindly Windows`).
   If the OAuth consent screen is in Testing, add the intended Google account as a test user.
3. Download the `client_secret_*.json`. Do not commit it, share it or include it in bug reports.
4. Windows Remindly → **Sign in with Google** (top-right) → **Select Google Desktop client JSON**.
   Then select **Continue with Google** and complete sign-in in your system browser.
5. Confirm **Back up & enable two-way sync**. Windows saves a separate
   `%LOCALAPPDATA%\Remindly\backups\pre-cloud-sync-*.json` before its first cloud write.
6. Android Remindly → **Settings → Google Account** → sign in with the same account and
   enable **Cloud sync (beta)**. Both devices require network access.
7. Windows **Settings → Account & Sync** shows connection state, errors and **Sync now**.

The Firebase client configuration (`app/google-services.json`) is injected into
the Windows build by the existing `GOOGLE_SERVICES_JSON` GitHub Actions secret.
It is not stored in Git. A locally built Windows app without this configuration
shows a configuration error and retains the local-only mode.

## Data safety and scope

- Windows reads Firestore **before** writing, then merges records by stable ID and
  `updatedAt`. A newer phone record wins over an older PC record and vice versa.
- Writes use Firestore update-time preconditions to avoid clobbering concurrent phone edits;
  a failed precondition is retried on the next sync cycle.
- The `json/updatedAt/schemaVer` envelope matches Android schema 71.
  Unknown Android-only record fields are preserved on Windows writes.
- Soft-delete `deletedAt` tombstones are included. The local file and initial backup remain.
- Synchronization runs at startup and approximately every 15 seconds while signed in,
  opted in and online. After the initial full snapshot it queries only documents whose
  `updatedAt` advanced, with a full reconciliation at most once a day. It can be
  retried manually. Firestore use is subject to the Firebase project's quotas/billing.
- Current Android `Sync.kt` covers items (Tasks, Learn, Buy), calls, saved places, shops
  and the settings document (including shopping lists). Android's city, chain and product
  catalogues are still **local-only**; use manual backup/import for them until Android
  transport and Firebase rules are expanded. Do not label these as cloud-synced.
- Windows retains local appearance, updater, tray and other desktop-only settings.
- Sign-out stops syncing but does not delete local records or backups.

## Verification checklist (two physical devices and backend)

- [ ] Google browser sign-in establishes the same Firebase UID as Android.
- [ ] First connection makes a pre-sync backup; the phone's existing records survive.
- [ ] Create, edit, complete and delete an item on Android; verify it appears on Windows.
- [ ] Repeat Windows → Android, including a record made while offline and later reconnected.
- [ ] Repeat for calls, shops, saved places, and a renamed/deleted shopping list.
- [ ] Make conflicting edits; verify the newer `updatedAt` version wins.
- [ ] A deleted record must not reappear when the other device reconnects.
- [ ] With expired credentials / security-rule denial / offline network, verify the error
  appears in Windows Settings and local changes remain intact.
- [ ] Confirm sign-out stops cloud work, and re-sign-in resumes after explicit opt-in.
- [ ] Repeat navigation in Fluent, Day Board, Command Dark and Today Hub themes.

The automated suite exercises merge and wire helpers but **cannot prove** a live
Google sign-in or two-device convergence. Do not close issue #16 before these
real-device/backend checks pass.
