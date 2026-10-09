# Remindly project and chat instructions

For work in this repository, read and apply the user-supplied policies in:
- [.github/instructions/software app.md](.github/instructions/software%20app.md)
- [.github/instructions/versions_management.md](.github/instructions/versions_management.md)
- [.github/instructions/workflows.md](.github/instructions/workflows.md)

These are project requirements adopted by the user on 9 October 2026, not additional requests to merge, publish unrelated changes, or modify other applications. Follow direct user instructions when they override a document. In particular, macOS stays an empty reserved folder until the user requests its implementation. Do not merge a PR without direct authorization.

For Windows and macOS software only (not Android APKs), show an update button at the top right only after GitHub provides a newer eligible version, labelled `Update to vx.y.z` (including a beta suffix for beta builds). Show the installed version in the main app window. Preserve existing navigation and unsaved-settings guards.

Versioning and release behavior are implemented and explained in docs/RELEASE-POLICY.md. Android uses its required system installation approval; supported Windows updates install silently.

Use exactly one downloadable artifact per workflow run: `Remindly_<version>.zip`. Build and test within the same job; do not upload intermediate payloads or test reports as extra artifacts. Missing platforms may be skipped; Remindly keeps macOS empty as requested.
