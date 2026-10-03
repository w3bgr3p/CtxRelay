# CdxSwapper

A Windows tray utility for switching between saved Codex accounts and monitoring their remaining usage limits. Includes a Python command-line companion.

## Download and run

1. Install [.NET 8 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Select **.NET Desktop Runtime → Windows → x64** on the download page.
2. Download `CdxSwapper.exe` from [Releases](https://github.com/w3bgr3p/cdxSwapper/releases/latest).
3. Run the executable. The application appears in the Windows notification area.

The release is a single executable; no installer is required. The tray application automatically follows the Windows display language: English, Russian, or Spanish. English is the default and fallback for unsupported languages. Restart CdxSwapper after changing the Windows display language. Dates and times follow your regional settings.

## Features

- Large tray digits show the remaining limit of the active account: the lower of the five-hour and weekly windows.
- Green digits indicate more than 35% remaining, amber 11–35%, and red 10% or less. A gray `?` means unavailable data or a request error. The tooltip shows both windows and their reset times.
- Right-click the tray icon to view saved accounts, switch accounts, refresh limits, open the account folder, toggle startup with Windows, or exit.
- Switching asks for confirmation, saves the current credentials, backs up the active authentication file, replaces it, and restarts the running `ChatGPT.exe` desktop process.
- Left-click the tray icon to refresh usage limits.
- Active credentials are synchronized every 15 seconds; limits are refreshed every five minutes and when opening the menu if the data is more than a minute old.
- New accounts are discovered automatically after signing into Codex. Folder names are derived from the account email.
- The executable includes a multi-resolution application icon generated from the root `icon.png`.

## Local files

The active authentication file is `%CODEX_HOME%\auth.json`, or `%USERPROFILE%\.codex\auth.json` when `CODEX_HOME` is unset.

Saved accounts live in a sibling `cdxSwapper` directory, normally `%USERPROFILE%\cdxSwapper\<name>\auth.json`. This directory also contains `usage.json`, `settings.json`, `cdxSwapper.log`, and `.backup\`.

On startup, the tray application sets `cli_auth_credentials_store = "file"` in the Codex `config.toml` if necessary, backing up the previous configuration. Restart Codex after this setting changes. Saved authentication files contain credentials; keep the account directory private.

Usage requests use the account access token. The utility does not refresh tokens itself: if a token expires, activate that account in Codex so the watcher can capture updated credentials.

Run `CdxSwapper.exe --check` to synchronize accounts and write usage results to `check.txt` in the account directory without opening the tray interface.

## Build

Requires the .NET 8 SDK or a compatible newer SDK on Windows.

```powershell
dotnet publish CdxSwapper/CdxSwapper.csproj -c Release -o dist
```

Output: `dist\CdxSwapper.exe`. The build embeds `icon.ico`, which contains sizes from 16 to 256 pixels.

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
