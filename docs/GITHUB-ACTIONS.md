# Building Remindly on GitHub Actions

The unified workflow builds Android and Windows together; macOS is reserved.

GitHub builds, tests, signs and publishes the APK. The repository holds **no** signing material or keys:
everything confidential lives in encrypted repository secrets. The runner only sees them while the job runs, and they are masked in logs.

## 1 · One-time setup: add 6 repository secrets

GitHub → repository → **Settings → Secrets and variables → Actions → New repository secret**

| SrNo. | Secret name | Value |
|---|---|---|
| 1 | `REMINDLY_KEYSTORE_BASE64` | the keystore file as one base64 line (commands below) |
| 2 | `REMINDLY_STORE_PASSWORD` | keystore password (`storePassword` in your `keystore.properties`) |
| 3 | `REMINDLY_KEY_ALIAS` | `remindly` |
| 4 | `REMINDLY_KEY_PASSWORD` | key password (`keyPassword` in your `keystore.properties`) |
| 5 | `MAPS_API_KEY` | the Android-restricted Google Maps SDK key (Key A) |
| 6 | `GOOGLE_SERVICES_JSON` | the whole contents of `app/google-services.json` (paste as-is; base64 also accepted) |

Base64 of the keystore:

```bash
base64 -w0 remindly.keystore            # Linux
base64 -i remindly.keystore | tr -d '\n' # macOS
```
```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("remindly.keystore"))   # Windows PowerShell
```

**Always use the same keystore.** Android installs an update only when it carries the same signature. The workflow checks the
certificate fingerprint (`a3b944da…ff645c55`) and refuses to publish an APK signed with any other key.

## 2 · Running the unified workflow

Workflow: `.github/workflows/remindly.yml` — **Remindly build and release**.

Pull requests run Windows tests, UI and installer checks, Android tests and the signed APK build. After all checks pass, the workflow publishes a beta prerelease and a single `Remindly_<version>.zip`. Merging to main publishes the stable release and updates the stable Android feed. Manual runs build only unless Publish is selected on main.

Versions are calculated automatically from published stable versions and conventional commit messages or release labels. See [Release policy](RELEASE-POLICY.md) for classification and channel rules.

## 3 · Package and feeds

The ZIP contains `portable/`, `windows-x64/`, `Android/`, and an empty `macOS/` folder. macOS development is deferred. GitHub stores the ZIP directly as a downloadable artifact and release asset. The signed APK and version manifest are also release assets for Android updates.

Stable publishing updates `VERSION` and `releases/version.json` only after the verified assets exist. Beta publishing updates its separate beta feed and leaves the stable feed unchanged. Re-running the same published commit keeps its original version and manifest.

## 4 · Troubleshooting

| SrNo. | Message | Fix |
|---|---|---|
| 1 | `Missing repository secrets: …` | add the named secrets (section 1) |
| 2 | `Keystore was tampered with, or password was incorrect` | the base64 was cut or the password is wrong: re-create secret 1/2/4 |
| 3 | `APK is not signed with the permanent Remindly key` | secret 1 holds a different keystore |
| 4 | `GOOGLE_SERVICES_JSON is not a valid google-services.json` | paste the entire file, including the outer `{ }` |
| 5 | A published version already exists | A rerun retains that version; new changes are versioned automatically. |

`publish-release.yml` is a fallback kept for an APK built locally and committed under `releases/`. It does nothing when the APK is not committed.

The build runs on one Windows runner with Android tooling. Its Artifacts box contains only the final versioned ZIP. Intermediate payloads and test reports stay local to the runner; validation output remains in job logs. Beta runs are serialized to keep their update manifest in order.
