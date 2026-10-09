# Unified versions, builds and updates

Krishna approved applying the supplied `versions_management.md` and `workflows.md` rules across Remindly on 8 October 2026. macOS is explicitly reserved and empty for now; no macOS application or DMG is claimed.

One workflow, **Remindly build and release** (`.github/workflows/remindly.yml`), tests Android and Windows, checks the Windows UI and install/update/uninstall flow, signs and verifies the APK, then packages all supported platforms. The former independent workflows are retired.

## Version policy

`VERSION` supplies the local build version. GitHub calculates the next version from the latest published stable tag: breaking changes (`type!:` / `BREAKING CHANGE:`) bump `x`, `feat:` bumps `y`, and fixes/default changes bump `z`. A PR can explicitly select `release:major`, `release:minor` or `release:patch`. The first unified stable release is at least **2.12.0**, including the merged Tasks lists-first feature.

PRs build `x.y.z-beta.<PR number>.<workflow run>` and publish a verified prerelease after every gate passes. Merging to main builds and publishes stable `x.y.z`. Stable retries reuse the same release and its recorded asset hashes; they do not mint a new version or replace already-published binaries. On stable publication, automation updates `VERSION` and the Android update feed after the downloadable assets exist.

Android versionCodes encode the three version components and build channel. The stable build is newer than every beta of the same version. Minor/patch components must be below 100; beta build slots 1â€“9998 are available per version, with 9999 reserved for stable. The planner rejects Android integer overflow instead of building an APK that cannot be upgraded.

## Downloads

The artifact and release download is **Remindly_<full version>.zip**, containing:

```text
portable/
  Remindly_<full version>.exe       Windows portable app
windows-x64/
  Remindly_<full version>.exe       Windows installer
macOS/                            Empty, reserved as requested
Android/
  Remindly_<full version>.apk
```

GitHub's artifact upload uses direct-file mode so the download is this ZIP itself; one extraction reveals the folders. No outer ZIP is added. Release assets also include its SHA-256 and an APK/manifest for existing Android update clients.

## Update behavior

- Both apps check their selected channel at startup when automatic checking is enabled. Manual checks remain in Settings.
- **Include beta updates** is optional, off by default, and persisted independently on each device. Changing the channel clears cached/queued update offers. A response from the old channel arriving after a switch is discarded.
- Windows downloads and validates the release ZIP and checksum. Installed copies run the new installer silently; portable copies replace their executable. Automatic installation waits for a quiet moment so open editors/reminders are not interrupted. Legacy Windows release ZIPs remain supported.
- Android downloads and verifies the APK from Settings, then hands installation to Android. A normal Android app cannot silently approve the system installation dialog. Stable/beta feeds are separate; publishing a PR never changes the stable feed or commits to main.
- macOS is a packaging reservation, with no update or installation implementation until its app exists.

Update-channel controls are inherently global to an installation, rather than per task tab. A tab cannot install a different app version.

## Validation before release

Automated gates cover semantic bumps, PR/stable ordering, exact archive layout (including empty macOS), Android beta exclusion, Windows channel selection/promotion and legacy/unified ZIP extraction. The existing Android and desktop suites, Windows UI smoke test, and installed-app upgrade test remain required.

Device pass: change channel/automatic checking â†’ verify â†’ close/reopen â†’ verify â†’ reboot â†’ verify. Check a stable update, opt into a beta, turn beta off during a check, verify no beta is offered/queued, and verify a beta promotes to stable at the same version. Windows: test both installed and portable copies while an editor/reminder is open. Android: confirm the installer keeps existing tasks, lists, reminders and data. Also verify that one ZIP extraction gives exactly the displayed folders and that macOS is empty.

## Adopted app and chat policy — 9 October 2026

The three original supplied instruction documents are saved under `.github/instructions/`, with `AGENTS.md` applying them to future work in this repository. Direct user instructions override those documents; macOS remains empty. The desktop main app window shows its installed version. A top-right `Update to v<version>` action is visible only for a newer eligible update found from GitHub. Windows starts the verified download/install flow. The updated software app.md explicitly excludes Android APKs from these header rules; Android retains its existing UI.

## Compliance review � 9 October 2026

Main's desktop version display, conditional top-right `Update to v<version>` action, startup/manual update checks, optional beta channel, PR beta/main stable releases, semantic version automation, and exact ZIP folder layout satisfy the supplied instructions. Android's header is excluded; its installer still requires normal system confirmation. macOS remains empty under the user's explicit exception.

The gap was the Actions Artifacts box: main published release-plan, two build payloads and two test reports alongside the requested ZIP. The unified workflow now builds/tests/signs/packages on one Windows runner, retaining intermediate files locally and publishing exactly one direct ZIP artifact. Unit/UI/installer/signature/package gates remain required; test results are visible in job logs without additional downloadable report artifacts. Existing completed runs retain their historical artifacts; the new rule applies to runs using this workflow.

The source instruction copies now use the current names `software app.md`, `versions_management.md`, and `workflows.md`. AGENTS.md points to those copies. ZIP integrity and empty macOS are checked before upload. Release APK/checksum/manifest assets continue supporting app updates; those assets are separate from the Actions Artifacts group.
