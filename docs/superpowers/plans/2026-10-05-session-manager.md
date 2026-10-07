# Session Manager Implementation Plan

> Execution: native implementation in this chat, within the user's approved integration scope.

**Goal:** Make Clodex Manager the C# main window of CtxDeck and retain account actions in the tray.

**Architecture:** Embedded web assets in WinForms WebView2, private virtual resource/API interception, C# session services. Read-only source discovery; per-user SQLite search cache and conversion manifests.

**Tech Stack:** .NET 8 Windows Forms, Microsoft.Web.WebView2, Microsoft.Data.Sqlite, System.Text.Json.

**Spec:** ../specs/2026-10-05-session-manager-design.md

## Global constraints

- Preserve existing changes, tray account operations and CLI checks.
- No Python dependency, no listening server or port.
- Source journals/databases are read-only except newly created conversion output.
- en/ru/es UI, English fallback; self-contained session web assets.

## Review focus

- Incomplete/malformed JSONL records must not interrupt catalog or indexing.
- UTF-8 message offsets and pagination must resolve the correct complete record.
- Changing/deleted journals must update persistent search results.
- Desktop cards must preserve distinct Desktop and CLI IDs and block missing-log resume.
- Failed/empty conversion must not leave a resumable partial output.

## Tasks

- [x] Add independent session regression runner with temporary logs, stores and round trips; observe missing feature failure.
- [x] Add session models/parsers, Desktop discovery, cached catalog and read-only state database enrichment.
- [x] Add persistent background message search with offsets, progress and invalidation.
- [x] Add native Codex/Claude conversion, atomic outputs, full export and reusable manifest.
- [x] Copy web assets as embedded resources, retain fetch through private resource interception and integrate en/ru/es copy.
- [x] Add WinForms WebView2 main form and C# action dispatch; restrict actions to the private origin/token.
- [x] Connect startup, left tray click, hide-on-close and process exit; preserve right-click menu and preview.
- [x] Run session and existing credential tests; exercise real WebView2 list, search, pagination, themes and clipboard on fixtures.
- [x] Build/publish distributable to a new output directory, inspect resources and update README.

## Verification results

- 28 session checks and 30 credential refresh/persistence checks passed.
- Real WebView2 fixtures verified list/filter/search/paging/actions/fonts/themes, escaping, token checks, and hide/reopen in English, Russian and Spanish.
- Installed Codex app-server accepted C# output with `thread/read` and `thread/resume`; no model inference was started.
- Release publish completed; native dependencies and web assets are bundled in the EXE.
- The replaced EXE opened its main window and populated its search index from real local stores.
- Claude output was checked by round-trip parsing and native-format assertions; no paid Claude inference was run.
