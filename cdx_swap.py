"""cdx_swap — вотчер и менеджер авторизаций Codex (ChatGPT desktop).

Хранилище:  <store>/<name>/auth.json   (name — метка аккаунта, напр. w3bgr3p, yar)
Активная:   <codex_home>/auth.json      (CODEX_HOME или ~/.codex)

Команды: list | status | sync | watch | swap.  Подробно — README.md.
"""
import argparse
import base64
import json
import os
import re
import subprocess
import sys
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path

EXIT_OK = 0
EXIT_FAILED = 1
EXIT_CONFIG = 2          # нет файлов/аккаунта/зависимостей — ретрай бессмысленен
EXIT_NETWORK = 5         # соединение не удалось после ретраев
EXIT_INTERRUPTED = 130

USAGE_URL = "https://chatgpt.com/backend-api/wham/usage"
DEFAULT_STORE = str(Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex").resolve().parent / "cdxSwapper")
DEFAULT_PROCESS = "ChatGPT"
LOG_DIR = Path(__file__).parent / "logs"

for _stream in ("stdout", "stderr"):
    try:
        getattr(sys, _stream).reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


# ---------------------------------------------------------------- вывод/ошибки

def _emit(stream, msg):
    try:
        print(msg, file=stream, flush=True)
    except UnicodeEncodeError:
        enc = getattr(stream, "encoding", None) or "ascii"
        try:
            print(str(msg).encode(enc, "replace").decode(enc, "replace"), file=stream, flush=True)
        except Exception:
            pass
    except Exception:
        pass


def _p(msg=""):
    """Данные -> stdout."""
    _emit(sys.stdout, msg)


def _log(msg=""):
    """Лог/диагностика -> stderr."""
    _emit(sys.stderr, f"{datetime.now():%H:%M:%S} {msg}")


def _short(value, limit=200):
    try:
        s = str(value)
    except Exception:
        return "<unprintable>"
    s = " ".join(s.split())
    return s if len(s) <= limit else s[:limit] + "..."


def _dump_traceback():
    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        path = LOG_DIR / f"traceback_{datetime.now():%Y%m%d_%H%M%S}.txt"
        path.write_text(traceback.format_exc(), encoding="utf-8")
        _log(f"[trace] {path}")
    except Exception:
        pass


def _safe(label, fn, *args, **kwargs):
    try:
        return fn(*args, **kwargs)
    except Exception as e:
        _log(f"[warn] {label} не выполнено: {type(e).__name__}: {_short(e)}")
        return None


class CdxError(Exception):
    exit_code = EXIT_FAILED

    def __init__(self, message="", step="", server_messages=None, exit_code=None):
        self.step = step or ""
        self.message = message or ""
        self.server_messages = list(server_messages or [])
        if exit_code is not None:
            self.exit_code = exit_code
        super().__init__(self.describe())

    def describe(self):
        parts = [f"step={self.step}"] if self.step else []
        if self.message:
            parts.append(self.message)
        parts += [f"server: {m}" for m in self.server_messages]
        return " | ".join(parts) or "ошибка без описания"

    def __str__(self):
        return self.describe()


# ---------------------------------------------------------------- auth.json

def codex_home():
    env = os.environ.get("CODEX_HOME")
    return Path(env) if env else Path.home() / ".codex"


def _jwt_claims(token):
    try:
        payload = token.split(".")[1]
        payload += "=" * (-len(payload) % 4)
        return json.loads(base64.urlsafe_b64decode(payload))
    except Exception:
        return {}


def _parse_ts(value):
    """ISO вида 2026-09-25T14:58:53.006686900Z -> datetime (UTC) или None."""
    if not value:
        return None
    try:
        m = re.match(r"(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)(?:\.(\d+))?", str(value))
        if not m:
            return None
        frac = (m.group(2) or "0")[:6].ljust(6, "0")
        return datetime.strptime(f"{m.group(1)}.{frac}", "%Y-%m-%dT%H:%M:%S.%f").replace(tzinfo=timezone.utc)
    except Exception:
        return None


class AuthFile:
    """Разобранный auth.json. raw — байты как на диске (копируем их, не пересериализуем)."""

    def __init__(self, path):
        self.path = Path(path)
        self.raw = self.path.read_bytes()
        data = json.loads(self.raw.decode("utf-8-sig"))
        tokens = data.get("tokens") or {}
        self.account_id = tokens.get("account_id") or ""
        if not self.account_id:
            raise ValueError(f"{self.path}: нет tokens.account_id")
        self.access_token = tokens.get("access_token") or ""
        self.last_refresh_raw = data.get("last_refresh") or ""
        self.last_refresh = _parse_ts(self.last_refresh_raw)
        id_claims = _jwt_claims(tokens.get("id_token") or "")
        at_claims = _jwt_claims(self.access_token)
        self.email = id_claims.get("email") or (at_claims.get("https://api.openai.com/profile") or {}).get("email") or ""
        self.plan = (at_claims.get("https://api.openai.com/auth") or {}).get("chatgpt_plan_type") or ""
        exp = at_claims.get("exp")
        self.access_exp = datetime.fromtimestamp(exp, timezone.utc) if isinstance(exp, (int, float)) else None

    @property
    def access_expired(self):
        return bool(self.access_exp and self.access_exp <= datetime.now(timezone.utc))


def _try_auth(path):
    try:
        return AuthFile(path)
    except Exception as e:
        _log(f"[warn] не читается {path}: {type(e).__name__}: {_short(e)}")
        return None


def _atomic_write(path, data: bytes):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + ".cdx_tmp")
    tmp.write_bytes(data)
    os.replace(tmp, path)


# ---------------------------------------------------------------- хранилище

class Store:
    """<store>/<name>/auth.json. Плоские <id>.json в корне (старый формат) не трогаем."""

    def __init__(self, root):
        self.root = Path(root)

    def accounts(self):
        """name -> AuthFile, по папкам с auth.json."""
        out = {}
        if not self.root.is_dir():
            return out
        for d in sorted(self.root.iterdir(), key=lambda p: p.name.lower()):
            if d.is_dir() and not d.name.startswith(".") and (d / "auth.json").is_file():
                a = _try_auth(d / "auth.json")
                if a:
                    out[d.name] = a
        return out

    def name_for(self, auth, accounts=None):
        """Существующая папка с тем же account_id, иначе новое имя из email."""
        accounts = self.accounts() if accounts is None else accounts
        for name, a in accounts.items():
            if a.account_id == auth.account_id:
                return name
        base = re.sub(r"[^\w.\-]+", "_", (auth.email.split("@")[0] if auth.email else "")) or auth.account_id
        name = base
        if name in accounts or (self.root / name).exists():
            name = f"{base}_{auth.account_id[:8]}"
        return name

    def path_for(self, name):
        return self.root / name / "auth.json"

    def write_names_list(self, accounts=None):
        """forSettings.txt — имена через | (как в ZP-скрипте)."""
        accounts = self.accounts() if accounts is None else accounts
        _atomic_write(self.root / "forSettings.txt", "|".join(accounts).encode("utf-8"))


def sync_active(store, verbose=True):
    """Копирует активный auth.json в store/<name>/auth.json, если он отличается
    и не старее сохранённого по last_refresh. Возвращает (name, changed)."""
    active_path = codex_home() / "auth.json"
    active = AuthFile(active_path)
    accounts = store.accounts()
    name = store.name_for(active, accounts)
    saved = accounts.get(name)
    if saved and saved.raw == active.raw:
        return name, False
    if saved and saved.last_refresh and active.last_refresh and active.last_refresh < saved.last_refresh:
        if verbose:
            _log(f"[sync] {name}: активный last_refresh={active.last_refresh_raw} старше "
                 f"сохранённого {saved.last_refresh_raw} — не перезаписываю")
        return name, False
    _atomic_write(store.path_for(name), active.raw)
    if verbose:
        _log(f"[sync] {name} ({active.email}) <- {active_path} last_refresh={active.last_refresh_raw}"
             + ("" if saved else " [новый аккаунт]"))
    _safe("запись forSettings.txt", store.write_names_list)
    return name, True


# ---------------------------------------------------------------- лимиты

def fetch_usage(auth, timeout=20, retries=3):
    """GET wham/usage. Возвращает dict ответа или бросает CdxError."""
    try:
        from curl_cffi import requests as http
        kw = {"impersonate": "chrome"}
    except ImportError:
        try:
            import requests as http
            kw = {}
        except ImportError:
            raise CdxError("нет ни curl_cffi, ни requests", step="usage", exit_code=EXIT_CONFIG)
    headers = {
        "Authorization": f"Bearer {auth.access_token}",
        "ChatGPT-Account-Id": auth.account_id,
        "Accept": "application/json",
    }
    last_exc = None
    for attempt in range(1, retries + 1):
        try:
            r = http.get(USAGE_URL, headers=headers, timeout=timeout, **kw)
        except Exception as e:
            last_exc = e
            time.sleep(2 * attempt)
            continue
        body = _resp_text(r)
        if r.status_code >= 500 and attempt < retries:
            time.sleep(2 * attempt)
            continue
        if r.status_code != 200:
            raise CdxError(f"HTTP {r.status_code}", step="usage", server_messages=[_short(body, 500)])
        try:
            return json.loads(body)
        except Exception:
            raise CdxError("ответ не JSON", step="usage", server_messages=[_short(body, 500)])
    raise CdxError(f"{type(last_exc).__name__}: {_short(last_exc)}", step="usage", exit_code=EXIT_NETWORK)


def _resp_text(r):
    try:
        return r.text
    except Exception:
        try:
            return r.content.decode("utf-8", "replace")
        except Exception:
            return ""


def _window(w):
    if not isinstance(w, dict):
        return None
    reset_at = w.get("reset_at")
    return {
        "used_percent": w.get("used_percent"),
        "window_seconds": w.get("limit_window_seconds"),
        "reset_at": datetime.fromtimestamp(reset_at, timezone.utc).isoformat() if isinstance(reset_at, (int, float)) else None,
        "reset_after_seconds": w.get("reset_after_seconds"),
    }


def usage_row(name, auth, active_id):
    """Сводка по аккаунту. Ошибка запроса не роняет — пишется в поле error."""
    row = {
        "name": name, "email": auth.email, "account_id": auth.account_id, "plan": auth.plan,
        "active": auth.account_id == active_id, "last_refresh": auth.last_refresh_raw,
        "access_exp": auth.access_exp.isoformat() if auth.access_exp else None,
        "limit_reached": None, "primary": None, "secondary": None, "error": None,
    }
    if auth.access_expired:
        row["error"] = f"step=usage | access_token exp={row['access_exp']} (истёк, запрос не делаю)"
        return row
    try:
        u = fetch_usage(auth)
        rl = u.get("rate_limit") or {}
        row["limit_reached"] = rl.get("limit_reached")
        row["primary"] = _window(rl.get("primary_window"))
        row["secondary"] = _window(rl.get("secondary_window"))
        row["plan"] = u.get("plan_type") or row["plan"]
    except CdxError as e:
        row["error"] = str(e)
    except Exception as e:
        row["error"] = f"step=usage | {type(e).__name__}: {_short(e)}"
    return row


def collect_usage(store):
    active = _try_auth(codex_home() / "auth.json")
    active_id = active.account_id if active else ""
    return [usage_row(n, a, active_id) for n, a in store.accounts().items()]


# ---------------------------------------------------------------- процесс

def _procs(name):
    import psutil
    target = name.lower() + ".exe"
    out = []
    for p in psutil.process_iter(["name", "exe"]):
        try:
            if (p.info["name"] or "").lower() == target:
                out.append(p)
        except Exception:
            pass
    return out


def find_exe(name):
    for p in _procs(name):
        try:
            if p.info.get("exe"):
                return p.info["exe"]
        except Exception:
            pass
    return None


def kill_tree(name, wait=15.0):
    r = subprocess.run(["taskkill", "/F", "/T", "/IM", f"{name}.exe"],
                       capture_output=True, text=True, timeout=30, errors="replace")
    _log(f"[kill] taskkill rc={r.returncode} {_short(r.stdout or r.stderr, 200)}")
    deadline = time.time() + wait
    while time.time() < deadline:
        if not _procs(name):
            time.sleep(1.0)            # даём ОС отпустить хэндлы
            return
        time.sleep(0.5)
    left = [p.pid for p in _procs(name)]
    raise CdxError(f"процессы {name}.exe живы через {wait}s: pids={left}", step="kill")


def start_exe(exe):
    flags = getattr(subprocess, "DETACHED_PROCESS", 0) | getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
    subprocess.Popen([exe], cwd=str(Path(exe).parent), close_fds=True, creationflags=flags,
                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _log(f"[start] {exe}")


def pick_best(rows):
    """Не в лимите, минимум по 5ч-окну, затем по недельному. Только аккаунты без ошибки."""
    ok = [r for r in rows if not r["error"] and r["limit_reached"] is False]
    if not ok:
        return None
    pct = lambda w: (w or {}).get("used_percent") or 0
    return min(ok, key=lambda r: (pct(r["primary"]), pct(r["secondary"])))["name"]


# ---------------------------------------------------------------- команды

def cmd_list(args, store):
    active = _try_auth(codex_home() / "auth.json")
    rows = [{"name": n, "email": a.email, "account_id": a.account_id,
             "active": bool(active and active.account_id == a.account_id),
             "last_refresh": a.last_refresh_raw} for n, a in store.accounts().items()]
    if args.json:
        _p(json.dumps(rows, ensure_ascii=False, indent=2))
    else:
        for r in rows:
            _p(f"{'*' if r['active'] else ' '} {r['name']:<16} {r['email']:<36} {r['last_refresh']}")
    return EXIT_OK


def _fmt_win(w):
    if not w:
        return "-"
    reset = w.get("reset_at")
    try:
        reset = datetime.fromisoformat(reset).astimezone().strftime("%d.%m %H:%M")
    except Exception:
        pass
    return f"{w.get('used_percent')}% (сброс {reset})"


def print_usage(rows, as_json):
    if as_json:
        _p(json.dumps(rows, ensure_ascii=False, indent=2))
        return
    for r in rows:
        mark = "*" if r["active"] else " "
        if r["error"]:
            _p(f"{mark} {r['name']:<14} {r['email']:<32} ERROR {r['error']}")
            continue
        flag = "LIMIT" if r["limit_reached"] else "ok"
        _p(f"{mark} {r['name']:<14} {r['email']:<32} {flag:<5} 5h: {_fmt_win(r['primary']):<24} "
           f"week: {_fmt_win(r['secondary'])}")


def cmd_status(args, store):
    _safe("синхронизация активной авторизации", sync_active, store, False)
    rows = collect_usage(store)
    print_usage(rows, args.json)
    return EXIT_OK if rows and not all(r["error"] for r in rows) else EXIT_FAILED


def cmd_sync(args, store):
    name, changed = sync_active(store)
    _p(json.dumps({"name": name, "changed": changed}, ensure_ascii=False))
    return EXIT_OK


def cmd_watch(args, store):
    _log(f"[watch] {codex_home() / 'auth.json'} -> {store.root} | sync каждые {args.interval}s, "
         f"лимиты каждые {args.usage_interval}s" + (" (выкл)" if args.usage_interval <= 0 else ""))
    last_usage = 0.0
    last_active = None
    while True:
        res = _safe("sync", sync_active, store)
        if res and res[0] != last_active:
            _log(f"[watch] активный аккаунт: {res[0]}")
            last_active = res[0]
        if args.usage_interval > 0 and time.time() - last_usage >= args.usage_interval:
            last_usage = time.time()
            rows = _safe("опрос лимитов", collect_usage, store)
            if rows is not None:
                state = {"checked_at": datetime.now(timezone.utc).isoformat(), "accounts": rows}
                _safe("запись usage.json", _atomic_write, store.root / "usage.json",
                      json.dumps(state, ensure_ascii=False, indent=2).encode("utf-8"))
                for r in rows:
                    _log(f"[usage] {r['name']}: " + (r["error"] or
                         f"{'LIMIT' if r['limit_reached'] else 'ok'} 5h {_fmt_win(r['primary'])} "
                         f"week {_fmt_win(r['secondary'])}"))
        time.sleep(args.interval)


def cmd_swap(args, store):
    stage = "resolve"
    accounts = store.accounts()
    if args.best:
        rows = collect_usage(store)
        for r in rows:
            _log(f"[best] {r['name']}: " + (r["error"] or f"limit_reached={r['limit_reached']} "
                 f"5h {_fmt_win(r['primary'])} week {_fmt_win(r['secondary'])}"))
        target = pick_best(rows)
        if not target:
            raise CdxError("нет аккаунта без лимита и без ошибки запроса", step=stage)
    else:
        target = args.name
    if not target or target not in accounts:
        raise CdxError(f"аккаунт '{target}' не найден в {store.root}; есть: {', '.join(accounts) or '-'}",
                       step=stage, exit_code=EXIT_CONFIG)
    new_auth = accounts[target]

    stage = "sync"                    # сохраняем свежие токены текущего аккаунта до подмены
    current_name, _ = sync_active(store)
    if new_auth.account_id == AuthFile(codex_home() / "auth.json").account_id and not args.force:
        _log(f"[swap] '{target}' уже активен")
        _p(json.dumps({"active": target, "swapped": False}, ensure_ascii=False))
        return EXIT_OK

    stage = "find_exe"
    exe = args.exe or find_exe(args.process)
    if not exe and not args.no_restart:
        _log(f"[swap] {args.process}.exe не запущен и --exe не задан — перезапуска не будет")

    killed = False
    try:
        stage = "kill"
        if not args.no_kill:
            killed = True             # taskkill мог убить часть дерева даже при ошибке
            kill_tree(args.process)

        stage = "replace"
        active_path = codex_home() / "auth.json"
        backup = store.root / ".backup" / f"auth_{current_name}_{datetime.now():%Y%m%d_%H%M%S}.json"
        _atomic_write(backup, active_path.read_bytes())
        _atomic_write(active_path, new_auth.raw)
        _log(f"[swap] {current_name} -> {target} ({new_auth.email}); бэкап {backup}")
    finally:
        # процесс поднимаем на любом исходе, если мы его убивали (или просили перезапуск)
        if exe and not args.no_restart and (killed or args.no_kill):
            _safe("запуск " + exe, start_exe, exe)
    _p(json.dumps({"active": target, "previous": current_name, "swapped": True}, ensure_ascii=False))
    return EXIT_OK


# ---------------------------------------------------------------- main

def build_parser():
    ap = argparse.ArgumentParser(description="Вотчер и менеджер авторизаций Codex")
    ap.add_argument("--store", default=os.environ.get("CDX_STORE", DEFAULT_STORE),
                    help=f"каталог с аккаунтами (по умолчанию {DEFAULT_STORE})")
    ap.add_argument("--json", action="store_true", help="вывод JSON в stdout")
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--json", action="store_true", default=argparse.SUPPRESS, help="вывод JSON в stdout")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("list", parents=[common], help="сохранённые аккаунты")
    sub.add_parser("status", parents=[common], help="лимиты по всем аккаунтам")
    sub.add_parser("sync", parents=[common], help="один раз скопировать активный auth.json в хранилище")
    w = sub.add_parser("watch", parents=[common], help="следить за активным аккаунтом и лимитами")
    w.add_argument("--interval", type=float, default=30, help="период синхронизации, с")
    w.add_argument("--usage-interval", type=float, default=300, help="период опроса лимитов, с (0 = выкл)")
    s = sub.add_parser("swap", parents=[common], help="сделать аккаунт активным")
    s.add_argument("name", nargs="?", help="имя аккаунта (папка в хранилище)")
    s.add_argument("--best", action="store_true", help="выбрать аккаунт с наименьшим расходом лимита")
    s.add_argument("--process", default=DEFAULT_PROCESS, help="имя процесса без .exe")
    s.add_argument("--exe", help="путь к exe для перезапуска, если процесс не запущен")
    s.add_argument("--no-kill", action="store_true", help="не убивать процесс")
    s.add_argument("--no-restart", action="store_true", help="не запускать процесс после замены")
    s.add_argument("--force", action="store_true", help="перезаписать даже если аккаунт уже активен")
    return ap


COMMANDS = {"list": cmd_list, "status": cmd_status, "sync": cmd_sync, "watch": cmd_watch, "swap": cmd_swap}


def main():
    args = build_parser().parse_args()
    if args.cmd == "swap" and not args.name and not args.best:
        _log("[FAIL] swap: укажи имя аккаунта или --best")
        return EXIT_CONFIG
    store = Store(args.store)
    if not store.root.is_dir():
        _log(f"[FAIL] нет каталога хранилища {store.root}")
        return EXIT_CONFIG
    try:
        return COMMANDS[args.cmd](args, store)
    except KeyboardInterrupt:
        return EXIT_INTERRUPTED
    except CdxError as e:
        _log(f"[FAIL] {e}")
        return e.exit_code
    except Exception as e:
        _log(f"[FAIL] непредвиденная ошибка: step={args.cmd} | {type(e).__name__}: {_short(e, 300)}")
        _dump_traceback()
        return EXIT_FAILED


if __name__ == "__main__":
    try:
        code = main()
    except KeyboardInterrupt:
        code = EXIT_INTERRUPTED
    except Exception as e:
        _log(f"[FATAL] {type(e).__name__}: {_short(e, 300)}")
        _dump_traceback()
        code = EXIT_FAILED
    sys.exit(code if isinstance(code, int) else EXIT_FAILED)
