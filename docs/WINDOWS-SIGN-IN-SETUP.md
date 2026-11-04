# Remindly for Windows — Google sign-in and Cloud sync setup (2.16.0+)

Remindly for Windows 2.16 signs in with Google and syncs live with the Android app through the same
Firestore database (`remindly-5c1e6`). Two one-time steps are needed in the consoles; after that you
only press **Settings → Account and sync → Sign in with Google** on the PC.

## Why a separate "Desktop app" client

The phone signs in through Google Play Services with an *Android* OAuth client. A Windows app cannot
use it: Google only allows the browser sign-in on a desktop for a client of type **Desktop app**.
It must be in the **same Google Cloud project** as Remindly's Firebase, so that the PC gets the
**same Firebase user id** as the phone — that is what makes both read and write the same
`/users/{uid}/…` data.

## One-time steps (Krishna)

| SrNo. | Where | What to do | Notes |
|---|---|---|---|
| 1 | [Google Cloud → APIs & Services → Credentials](https://console.cloud.google.com/apis/credentials?project=remindly-5c1e6) | Check the project selector shows **remindly-5c1e6** | The Android and Web clients Firebase created are already listed |
| 2 | **+ Create credentials → OAuth client ID** | Application type **Desktop app**, name `Remindly Windows` → **Create** | No redirect URI is asked: the app uses `http://127.0.0.1:<random port>/`, which Google allows for this type |
| 3 | The "OAuth client created" card | **Download JSON** (`client_secret_….json`) | Not a high-value secret for a Desktop client, but keep it out of the repository |
| 4 | [GitHub → remindly-app → Settings → Secrets and variables → Actions](https://github.com/krishnabhunia/remindly-app/settings/secrets/actions) | **New repository secret** `WINDOWS_OAUTH_CLIENT_JSON` = the whole content of that file | CI embeds it into `Remindly.exe` (with the existing `GOOGLE_SERVICES_JSON`) and deletes it after the build. Re-run the PR's workflow (or merge) so the next build has it |
| 5 | [Firebase → Firestore → Rules](https://console.firebase.google.com/project/remindly-5c1e6/firestore/rules) | Paste [`docs/FIRESTORE-RULES.rules`](FIRESTORE-RULES.rules) → **Publish** | Skip if the Android 2.9 rules are already published — they are the same |
| 6 | Phone: Remindly → Settings → **Google Account** | Signed in with your Gmail, **Cloud sync (beta)** switched on | The phone publishes its data and preferences the first time |
| 7 | PC: Remindly 2.16 → Settings → **Account and sync** | **Sign in with Google** → choose the **same Gmail** as the phone | The first sync runs by itself; a backup of the PC's data is saved first |

No build yet with the client built in? Step 7 also works with **Use a Google client file…** on the
Account and sync card: choose the downloaded `client_secret_….json` (stored encrypted for your
Windows account only).

## If something blocks it

| SrNo. | What you see | Fix |
|---|---|---|
| 1 | "Access blocked: … has not completed the Google verification process" / "app is in testing" | Google Cloud → APIs & Services → **OAuth consent screen → Test users → + Add users** → add your Gmail |
| 2 | "Error 400: redirect_uri_mismatch" | The client is not of type **Desktop app** — create one as in step 2 |
| 3 | "This app isn't verified" | Expected for a personal project: **Advanced → Go to Remindly** |
| 4 | Card **"The cloud refused your data"** (PERMISSION_DENIED) | Publish the rules (step 5), then **Try again**. Also check the PC is signed in with the same Gmail as the phone |
| 5 | Card **"Nothing from your phone yet"** | On the phone: same Gmail in Settings → Google Account, Cloud sync on, phone online with Remindly opened once → **Check again** |
| 6 | Sign-in page says "API key not valid" / "Requests from this client are blocked" | Google Cloud → Credentials → the Firebase **API key** has *Application restrictions = Android apps*. Set it to *None* (it is restricted to Firebase APIs already), or tell Claude to add a separate key for Windows |
| 7 | Card **"Sign in again"** | Google ended the PC's sign-in (password changed, access removed): press **Sign in with Google** |

Every card has **Copy details** (the exact error) and every sync step is written to
`%LOCALAPPDATA%\Remindly\remindly.log` with lines starting `SYNC` (**Sync log** button).

## What syncs, what stays on the PC

| SrNo. | Syncs both ways | Stays on this PC |
|---|---|---|
| 1 | Tasks, Learn and Buy items (with fields the PC does not show, kept untouched) | Design A/B/C/D, Start with Windows, notification area |
| 2 | Calls | Auto update, beta updates, version pin |
| 3 | Buy lists (merged per list — the newer copy of each wins) | The Cloud sync switch of this PC |
| 4 | Shops, cities, chains, products | Sign-in tokens (Windows DPAPI, never in the data file or backups) |
| 5 | Sharing format, default list, Buy groups | The phone's own preferences (look, gestures, Task lists) pass through untouched |

Not synced by the PC: the phone's product–shop price links (`productlinks`) — Windows does not use them.

## How it works (for maintainers)

| SrNo. | Piece | Detail |
|---|---|---|
| 1 | Sign-in | OAuth 2.0 in the system browser, loopback redirect to `127.0.0.1`, PKCE (S256), scopes `openid email profile`; the Google ID token goes to Identity Toolkit `accounts:signInWithIdp`; the Firebase refresh token is kept with DPAPI |
| 2 | Transport | Firestore gRPC `Listen` (one target per collection + `settings/app`) and `Commit`, with the Firebase ID token as bearer |
| 3 | Contract | Same as Android `Sync.kt`: doc `{json, updatedAt, schemaVer: 71}`; last writer wins on `updatedAt` (items and calls fall back to `createdAt`); deletes are `deletedAt` tombstones; an older writer is laid over the local copy |
| 4 | Safety | Nothing is sent before the first snapshot; then only records newer than (or missing from) the cloud. A cloud copy older than the PC's is answered with the PC's. A settings document is only written on top of the phone's last one |
| 5 | Delete forever | Removes the Firestore document too; a tombstone of it is never brought back |
| 6 | Tests | `windows/tests/Remindly.Core.Tests/SyncTests.cs` (contract) and `EmulatorSyncTests.cs` (end to end — run with `FIRESTORE_EMULATOR_HOST=127.0.0.1:8085` after `gcloud emulators firestore start --host-port=127.0.0.1:8085`) |
