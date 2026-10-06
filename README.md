# CdxSwapper

A Windows session manager for Codex, Claude, Hermes and Gemini (Antigravity IDE/application) with a tray utility for switching saved Codex accounts and monitoring their remaining usage limits. Session management runs entirely in C#; the optional Python companion remains available for account operations.

## Download and run

1. Install [.NET 8 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Select **.NET Desktop Runtime → Windows → x64** on the download page.
2. Download `CdxSwapper.exe` from [Releases](https://github.com/w3bgr3p/cdxSwapper/releases/latest).
3. Install [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) if it is not already installed.
4. Run the executable. The session library opens and the account monitor appears in the Windows notification area.

The release is a single executable; no installer or Python installation is required. The main window and tray automatically follow the Windows display language: English, Russian, or Spanish. English is the default and fallback for unsupported languages. Restart CdxSwapper after changing the Windows display language. Dates and times follow your regional settings.

## Features

- Large tray digits show the remaining limit of the active account: the lower of the five-hour and weekly windows.
- Green digits indicate more than 35% remaining, amber 11–35%, and red 10% or less. A gray `?` means unavailable data or a request error. The tooltip shows both windows and their reset times.
- Right-click the tray icon to view saved accounts, switch accounts, refresh limits, open the account folder, toggle startup with Windows, or exit.
- Switching asks for confirmation, saves the current credentials, backs up the active authentication file, replaces it, and restarts the running `ChatGPT.exe` desktop process.
- Left-click the tray icon to open or restore the session library. Closing the main window hides it; monitoring continues in the tray. Use the tray's Exit command to quit, or launch with `--tray` to start without opening the main window.
- Active credentials are synchronized every 15 seconds; limits are refreshed every five minutes and when opening the menu if the data is more than a minute old.
- Access tokens are automatically renewed when expired or within five minutes of expiry, before switching accounts, and once after a usage request returns HTTP 401. Rotated refresh tokens and `last_refresh` are saved alongside the updated access token.
- New accounts are discovered automatically after signing into Codex. Folder names are derived from the account email.
- The executable includes a multi-resolution application icon generated from the root `icon.png`.

## Session library

- Browse Codex, Claude Code, Claude Desktop and Hermes sessions, including archives and subagents. Filter by client/status, sort, and search titles, IDs, paths, models or complete message contents.
- Inspect metadata and conversations; load older records and jump directly to search matches beyond the latest fragment. Malformed/incomplete JSONL records are skipped; long messages display up to 60,000 characters.
- Copy IDs, paths and PowerShell resume commands, reveal the journal in Explorer, open its file/project, or resume in a new PowerShell window. A matching installed CLI and an existing project directory are required. Subagents resume through their parent session.
- All Codex sessions, including imported legacy journals, open their thread in the desktop application using `codex://threads/<id>`. Both opening an existing session and opening after conversion use the application directly; neither launches PowerShell or `codex resume`.
- Continue a session in either of the other two clients: Codex ↔ Claude, Codex ↔ Hermes, or Claude ↔ Hermes. This creates a new native CLI session, complete text export and conversion manifest; repeating an unchanged conversion reuses its session. Historical tools are preserved as text without executing them, and source history is unchanged. Binary attachments are represented as text markers.
- Claude content blocks are split into prose, tool calls and tool results. Codex imports store calls/results as native `function_call` / `function_call_output` history, not user/assistant messages; the terminal conversation no longer displays raw Claude tool envelopes. Conversion format v3 prevents reuse of earlier imports that mixed tools into prose. Other targets retain readable historical tool text.
- Hermes reads its native SQLite sessions and active messages, including named profile stores. Rewound messages are excluded. Search hits and older-history navigation use native message IDs; same-length edits and WAL updates invalidate cached search results. Resume commands select the original home and profile explicitly. The file action opens a full text export of the selected conversation; file paths and Explorer actions refer to its database. Hermes session sizes represent conversation text rather than the shared database file size.
- Gemini reads both `%USERPROFILE%\.gemini\antigravity-ide` and `antigravity`, with distinct application labels and conversation identities. Plaintext transcripts are preferred; native SQLite/protobuf text fields provide a fallback. Search, pagination and export work with readable histories. Encrypted legacy `.pb` files remain visible as cards with search/conversion disabled. Unsupported binary step fields are omitted when no plaintext transcript exists.
- Readable Antigravity history can be converted into native Codex, Claude or Hermes sessions. Transfers into Antigravity create native conversation databases with completed tool history. The Antigravity button opens the imported chat in the standalone application, selecting its exact conversation link through Windows UI Automation and verifying the displayed chat URL. A separate Antigravity IDE button imports and opens the conversation through the IDE language server. Existing application windows are reused. Copying text context remains available as a manual fallback. Gemini CLI is not included.
- Claude Desktop cards link to CLI journals using `cliSessionId`; Desktop IDs and CLI IDs remain separate. Cards without journals display their available summary and cannot be resumed or converted through the manager. Creating a CLI session does not guarantee a new Claude Desktop sidebar card.
- Dark, Light, Hyper and Graphite themes; embedded JetBrains Mono fonts; automatic updates every 15 seconds.

Codex sources: `%CODEX_HOME%` or `%USERPROFILE%\.codex`, including `sessions`, `archived_sessions`, and read-only metadata from the latest `state_*.sqlite`. Claude sources: `%CLAUDE_CONFIG_DIR%` or `%USERPROFILE%\.claude\projects`. Desktop cards are discovered under AppData and Microsoft Store Claude installations when using the default Claude store.

Hermes sources: `%HERMES_HOME%` or `%LOCALAPPDATA%\hermes`, using `state.db` in that home and its named profiles. Each session retains its own database/home/profile. Source databases are read-only. Converting into Hermes inserts a new session and its messages in one transaction without changing existing history or migrating the schema. If the target database is missing, the installed Hermes CLI initializes it with `sessions list` before import. Gateway/Desktop sessions with no recorded project directory resume from the user home directory.

The HTML interface, fonts and C# API are embedded in the executable and served inside WebView2 at a private virtual origin. No TCP server or listening port is used. The source checkout and `clodex_manager` are not required at runtime.

The derived search index, conversion exports/manifests and WebView2 profile live in `<account store>\.sessions\`. Search indexes in the background and persists between launches; results are partial until indexing finishes. This cache includes conversation text. The Sources dialog displays scan/index errors. Browsing reads source journals and databases without modification; conversion writes new sessions.

The session detail has a **Delete session** action with confirmation. It removes the selected local journal and client index entry, or the selected Hermes session and its messages from the shared database. Claude Desktop cards and Antigravity session files are included. Backups and original file locations are saved under `<account store>\.sessions\deleted\`; shared databases are backed up through SQLite before changing them. The list and search index refresh after deletion. This removes local data; it does not delete a cloud conversation. A client that keeps an active conversation in memory may need to be closed before deleting it.

Use the checkboxes beside session cards to select several sessions, **Select visible** to select the current filtered list, and **Delete selected** to delete them with one confirmation. Selection persists across filters; the button shows the total selected count. Batch deletion saves backups for each session and reports individual failures; unsuccessful sessions remain selected for retry. Quit Claude Desktop before deleting its sessions.

**Show calls** controls visibility of tool calls and results in the conversation, older pages and opened search matches. The preference persists between launches. Hermes and Gemini calls are displayed as separate collapsible tool entries; legacy textual tool blocks are separated from assistant prose and JSON arguments are decoded for readability. Hiding calls does not remove history or change conversions.

## Local files

The active authentication file is `%CODEX_HOME%\auth.json`, or `%USERPROFILE%\.codex\auth.json` when `CODEX_HOME` is unset.

Saved accounts live in a sibling `cdxSwapper` directory, normally `%USERPROFILE%\cdxSwapper\<name>\auth.json`. This directory also contains `usage.json`, `settings.json`, `cdxSwapper.log`, and `.backup\`.

On startup, the tray application sets `cli_auth_credentials_store = "file"` in the Codex `config.toml` if necessary, backing up the previous configuration. Restart Codex after this setting changes. Saved authentication files contain credentials; keep the account directory private.

Usage requests use the account access token. Automatic renewal uses the saved OAuth refresh token and preserves the other authentication fields. If the account is still active and its credentials have not changed, the active Codex authentication file is updated too. Refresh operations are serialized, and credentials are reloaded before requesting and saving tokens to avoid overwriting newer credentials supplied by Codex.

If a refresh token is missing, expired, revoked, or already used, sign into that account in Codex again. Permanently rejected refresh tokens are not repeatedly submitted during the current run; new credentials allow recovery. Temporary failures are retried on the next refresh of usage limits. The Python CLI does not renew tokens; automatic token renewal is part of the tray application and its `--check` mode.

Run `CdxSwapper.exe --check` to synchronize accounts and write usage results to `check.txt` in the account directory without opening the tray interface.

## Build

Requires the .NET 8 SDK or a compatible newer SDK on Windows.

```powershell
dotnet publish CdxSwapper/CdxSwapper.csproj -c Release -o dist
```

Output: `dist\CdxSwapper.exe`. The build embeds `icon.ico`, which contains sizes from 16 to 256 pixels.

Run the isolated token refresh and persistence checks (no real credentials or network requests):

```powershell
dotnet run --project tests/CdxSwapper.Tests/CdxSwapper.Tests.csproj -c Release
```

Session parsing, Desktop linking, UTF-8 pagination, Hermes SQLite profiles/WAL/rewinds, persistent search invalidation and round-trip conversion checks use temporary fixtures:

```powershell
dotnet run --project tests/CdxSwapper.Sessions.Tests -c Release
dotnet run --project tests/CdxSwapper.Sessions.Tests -c Release -- --ui
```

The `--ui` check opens a real WebView2 window with temporary journals/databases, verifies all four clients (including both Antigravity sources), the interface/API/clipboard, live search invalidation and captures `sessions-ui.png` beside the test executable. Add `--culture=ru-RU` or `--culture=es-MX` to check localization; unsupported cultures fall back to English. `--native` verifies a generated Codex session through the installed Codex app-server without running model inference. `--native-hermes` verifies schema initialization and native export of C# imported history through the installed Hermes CLI, also without inference. `--native-antigravity-app` verifies import, the actual application's selected chat URL, visible user/assistant messages and expanded tool arguments/output. `--native-antigravity` verifies IDE import and focus. These Antigravity checks create local test conversations without model inference. `--hermes-live` and `--antigravity-live` perform read-only scans and print counts from real stores.

## Python CLI

The companion `cdx_swap.py` supports account listing, usage checks, continuous synchronization, and switching from a terminal.

Install dependencies for usage requests and process management:

```powershell
python -m pip install requests psutil
```

If installed, `curl_cffi` is preferred over `requests` for usage requests.

```powershell
python cdx_swap.py list
python cdx_swap.py status --json
python cdx_swap.py sync
python cdx_swap.py watch --interval 30 --usage-interval 300
python cdx_swap.py swap <name>
python cdx_swap.py swap --best
```

Use `--store <directory>` or `CDX_STORE` to override the account directory. By default, the CLI uses the same sibling `cdxSwapper` directory as the tray application. Account matching uses `tokens.account_id`.

Switch options include `--process`, `--exe`, `--no-kill`, `--no-restart`, and `--force`. The watcher preserves newer saved credentials rather than overwriting them with an older `last_refresh` value. Switching saves the current account and backs up the active file before replacement.

CLI data is written to stdout and logs to stderr. Exit codes: `0` success, `1` failure, `2` configuration error, `5` network error, and `130` interrupted. Unexpected exceptions also write a traceback under `logs/`.
