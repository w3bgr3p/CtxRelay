# Hermes integration

User approved adding Hermes alongside Codex and Claude in the existing main window.

- Implement a C# adapter for native Hermes SQLite sessions/messages, including profile databases, message-ID pagination and search navigation.
- Resolve HERMES_HOME with the Windows LocalAppData default; preserve each session's source home for resume.
- Read source databases without writing or migrating them. Exclude inactive rewind records; preserve active historical tool content as text.
- Use per-session content fingerprints so WAL updates, same-length edits and deletion invalidate the index, while unrelated sessions do not duplicate conversions.
- Implement native Hermes session creation inside one SQLite transaction, using the installed schema. Initialize a missing target database through the installed Hermes CLI, never by fabricating or migrating its schema.
- Add Hermes ↔ Codex and Hermes ↔ Claude conversion with new IDs, complete exports and reusable manifests. No historical tools are executed.
- Add the third filter, localized actions for both other targets, and SQLite-aware file labels.
- Preserve the tray account menu and all existing behavior.

## Steps

- [x] Add failing Hermes fixtures for catalog, pages, search, updates/deletions, profile routing and all conversion directions.
- [x] Add Hermes reader/writer and integrate source dispatch with catalog, search and converter.
- [x] Integrate resume environment, main-window UI and localization.
- [x] Run regression tests and real WebView2 checks; verify imported history with native Hermes without model inference.
- [x] Publish, replace and restart the local EXE; update documentation.

## Verification

- 24 Hermes, 28 existing session and 30 credential checks passed.
- Real WebView2 verified three providers, Hermes pages/search/message-ID navigation, two conversion destinations, localization and refreshing search results after same-length SQLite/WAL edits.
- Installed Hermes initialized a temporary native database, exported a C# imported session with its full history, and supported a round trip back to Codex. No inference was run.
- Installed Codex accepted C# output via thread/read and thread/resume without inference.
- Read-only live discovery found 8 Hermes sessions and 1045 active messages across 2 profiles.
- Release publish completed; the local EXE was replaced and restarted after verifying its SHA-256 against the staged artifact.
