<p align="center">
  <img src="assets/logo.png" width="112" alt="CtxRelay logo">
</p>

<h1 align="center">CtxRelay</h1>

<p align="center">
  One library for your Codex, Claude, Hermes and Gemini (Antigravity) sessions —<br>
  search them, read them, and continue any conversation in another client.
</p>

---

## What it does

- **Session library.** Every local session from Codex, Claude Code / Claude Desktop, Hermes and Antigravity (IDE and app) in one list, with full-text search across message contents.
- **Relay a conversation.** Continue a session in another client: Codex ↔ Claude ↔ Hermes ↔ Antigravity. A new native session is created, tool calls are kept as history, the source stays untouched.
- **Resume / open.** Open a session in its desktop app or resume it in the CLI with one click.
- **Clean up.** Delete one or many sessions; every deletion is backed up first.
- **Tray limits.** The tray icon shows the remaining Codex limit (5‑hour / weekly) for the active account; the menu also shows Claude limits.
- **Codex account switching.** Keep several Codex accounts, switch between them from the tray. Tokens are refreshed automatically.

Windows only. UI follows the system language (English, Russian, Spanish).

## Install

1. [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0) and [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (already present on most Windows 10/11 machines).
2. Download `CtxRelay.exe` from [Releases](../../releases/latest) and run it.

Closing the window keeps CtxRelay in the tray; quit from the tray menu. `--tray` starts minimized.

## Data

| What | Where |
|---|---|
| Saved Codex accounts, settings, log | `%USERPROFILE%\CtxRelay\` (an existing `CtxDeck\` or `cdxSwapper\` folder is reused) |
| Search index, conversion exports, deletion backups | `<store>\.sessions\` |

Source journals and databases are only read; conversion writes new sessions. Saved `auth.json` files are credentials — keep the folder private.

## Build

```powershell
dotnet publish CtxRelay/CtxRelay.csproj -c Release -o dist
```
