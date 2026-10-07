# Session manager integration

Approved in chat on 2026-10-05: port Clodex Manager into the main CtxDeck window using C# and WebView2, while keeping account management in the tray context menu.

- Retain the session list, filters, themes, content search, conversation pagination, source diagnostics, clipboard/file/project actions, resume and conversion in both directions.
- Discover Codex sessions/archives and read its state database without modifying it. Discover Claude Code and Claude Desktop journals/cards, including subagents and cards with no journal.
- Implement parsing, caching, indexing, conversion and process actions in C#; require no Python installation or source checkout at runtime.
- Serve embedded HTML/CSS/JS/fonts through WebView2 resource interception at a private virtual origin; no listening TCP port is needed.
- Store the derived search index, conversion manifests and WebView profile under the existing per-user app store.
- Preserve input logs. Conversion creates a new native session, a complete text export and a reusable manifest; historical tools are text, never executable calls.
- Show the main window on normal startup; left tray click shows it. Closing hides it while account monitoring continues. Exit in the existing tray menu ends the application.
- Preserve CLI checks, tray preview, account switching, refresh and existing uncommitted work.
- Retain automatic English/Russian/Spanish localization and English fallback for the integrated UI.
- Validate with temporary stores/logs, malformed records, multibyte offsets, older-message navigation, cached/deleted/reindexed logs, desktop metadata and round-trip conversion; run the existing credential tests and a real WebView2 smoke check.
