<p align="center">
  <img src="assets/logo.png" width="112" alt="CtxRelay logo">
</p>

<h1 align="center">CtxRelay</h1>

<p align="center">
  One library for your Codex, Claude, Hermes and Gemini (Antigravity) sessions —<br>
  search them, read them, and continue any conversation in another client.<br><br>
  <a href="https://ctxrelay.z3n.pro">ctxrelay.z3n.pro</a> · <a href="../../releases/latest">Download</a>
</p>

---

## What it does

- **Session library.** Every local session from Codex, Claude Code / Claude Desktop, Hermes and Antigravity (IDE and app) in one list, with full-text search across message contents.
- **Relay a conversation.** Continue a session in another client: Codex ↔ Claude ↔ Hermes ↔ Antigravity. A new native session is created, tool calls are kept as history, the source stays untouched.
- **Resume / open.** Open a session in its desktop app or resume it in the CLI with one click.
- **Clean up.** Delete one or many sessions; every deletion is backed up first.
- **Tray limits.** The tray icon shows the remaining Codex limit (5‑hour / weekly) for the active account; the menu also shows Claude limits.
- **Codex account switching.** Keep several Codex accounts, switch between them from the tray. Tokens are refreshed automatically.
- **Add a Codex account.** Right-click the tray icon → **Add account…** and complete sign-in in your browser. Existing accounts and the active authorization stay in place. Select the saved account from the tray to switch. Choose a different browser account if the current one is already saved. Sign-in can be cancelled from the same menu and expires after five minutes. Requires Codex Desktop running or Codex CLI installed.
- **Desktop applications.** Open **Applications** in the window or tray to detect, install, update, launch and uninstall Codex / Claude without Microsoft Store. Both clients support portable installation into a chosen folder and direct signed MSIX installation. **Copy as portable…** copies an existing installation into a separate managed folder, preserving the original. Portable clients have a Start menu shortcut, updates and rollback; Codex keeps its bundled CLI. Existing standalone clients can also be connected and selected for opening sessions.
- **Choose an application folder.** Select the existing Codex / Claude folder directly. The panel reads the actual application version from `app.asar`, shows the selected installation and reports **Update available**, **Up to date**, **Installed version is newer**, or **Version could not be read**. Update is enabled only when the selected installation needs it. Updating a connected standalone folder keeps its previous contents for rollback and adopts it as a managed portable installation.

Downloads are checked against SHA-256, package identity, architecture and publisher signature. A running client is never closed by the application manager: verified updates wait for you to close it and click **Apply staged update**. Managed portable installs retain one previous version for rollback. Uninstall preserves external account/session stores and backs up MSIX profile data under `<store>\.desktop-apps\backups\` before removing the package; the backup path is shown in the application panel. Connected external installs are removed with their original installer. Release metadata comes from Wangnov's mirrors; Claude payloads download from the official `downloads.claude.ai` host. See [third-party notices](THIRD-PARTY-NOTICES.md).

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
