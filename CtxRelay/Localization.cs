using System.Globalization;

namespace CtxRelay;

enum Message
{
    FileAuthEnabled, NoAuth, UsageError, FiveHours, Week, WeekShort, Remaining,
    NoActiveAuth, Refreshing, NotFetched, UpdatedAt, NoData, Error, LimitUntil,
    NoAccounts, RefreshLimits, OpenFolder, Autostart, On, Off, Exit, ConfirmSwap,
    LimitWarning, ActiveAccount, SwapFailed, WindowRemaining, TokenExpired,
    MissingRateLimit, KillTimeout, ProcessStillRunning, AccountNotFound,
    EmptyJson, MissingAccountId, RefreshUnavailable, RefreshSignIn,
    RefreshFailed, InvalidRefreshResponse, RefreshAccountChanged,
    AddAccount, CancelLogin, LoginPending, LoginAdded, LoginExists, LoginFailed,
    LoginInvalid, LoginCliMissing, LoginTimedOut
}

static class Localization
{
    public static string Language { get; private set; } = "en";

    public static string ResolveLanguage(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "ru" => "ru",
        "es" => "es",
        _ => "en"
    };

    public static void Initialize()
    {
        Language = ResolveLanguage(CultureInfo.CurrentUICulture);
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    static readonly Dictionary<Message, (string En, string Ru, string Es)> Strings = new()
    {
        [Message.AddAccount] = ("Add account…", "Добавить аккаунт…", "Añadir cuenta…"),
        [Message.CancelLogin] = ("Cancel sign-in", "Отменить вход", "Cancelar inicio de sesión"),
        [Message.LoginPending] = ("Complete sign-in in your browser", "Завершите вход в браузере", "Completa el inicio de sesión en el navegador"),
        [Message.LoginAdded] = ("Account added: {0}. Select it in the tray menu to switch.", "Аккаунт добавлен: {0}. Для переключения выберите его в меню трея.", "Cuenta añadida: {0}. Selecciónala en el menú de la bandeja para cambiar."),
        [Message.LoginExists] = ("Account {0} is already saved. To add another, choose a different account in your browser.", "Аккаунт {0} уже сохранён. Чтобы добавить другой, выберите другой аккаунт в браузере.", "La cuenta {0} ya está guardada. Para añadir otra, elige otra cuenta en el navegador."),
        [Message.LoginFailed] = ("Browser sign-in failed (exit code {0}). Try adding the account again.", "Вход через браузер не завершён (код {0}). Попробуйте добавить аккаунт снова.", "El inicio de sesión falló (código {0}). Intenta añadir la cuenta de nuevo."),
        [Message.LoginInvalid] = ("Sign-in returned incomplete credentials. Try again.", "Вход вернул неполную авторизацию. Повторите попытку.", "El inicio de sesión devolvió credenciales incompletas. Inténtalo de nuevo."),
        [Message.LoginCliMissing] = ("Codex CLI was not found. Start Codex Desktop or install Codex CLI, then try again.", "Codex CLI не найден. Запустите Codex Desktop или установите Codex CLI и повторите попытку.", "No se encontró Codex CLI. Abre Codex Desktop o instala Codex CLI e inténtalo de nuevo."),
        [Message.LoginTimedOut] = ("Sign-in timed out after 5 minutes. Try adding the account again.", "Время ожидания входа истекло (5 минут). Попробуйте добавить аккаунт снова.", "Se agotó el tiempo de inicio de sesión (5 minutos). Intenta añadir la cuenta de nuevo."),
        [Message.FileAuthEnabled] = ("File-based authentication enabled in config.toml. Restart Codex.", "В config.toml включено хранение авторизации в файле. Перезапустите Codex.", "Se activó la autenticación mediante archivos en config.toml. Reinicia Codex."),
        [Message.NoAuth] = ("no auth.json", "нет auth.json", "sin auth.json"),
        [Message.UsageError] = ("failed to fetch usage limits", "ошибка запроса лимитов", "error al consultar los límites"),
        [Message.FiveHours] = ("5h", "5ч", "5h"),
        [Message.Week] = ("week", "неделя", "semana"),
        [Message.WeekShort] = ("week", "нед", "sem"),
        [Message.Remaining] = ("{0} · {1:0}% remaining", "{0} · осталось {1:0}%", "{0} · queda {1:0}%"),
        [Message.NoActiveAuth] = ("No active auth.json", "Нет активного auth.json", "No hay auth.json activo"),
        [Message.Refreshing] = ("Refreshing limits…", "Обновляю лимиты…", "Actualizando límites…"),
        [Message.NotFetched] = ("Limits not fetched yet", "Лимиты ещё не получены", "Límites aún no consultados"),
        [Message.UpdatedAt] = ("Limits as of {0:t}", "Лимиты на {0:t}", "Límites a las {0:t}"),
        [Message.NoData] = ("no data", "нет данных", "sin datos"),
        [Message.Error] = ("error", "ошибка", "error"),
        [Message.LimitUntil] = ("limited until {0:t}", "лимит до {0:t}", "límite hasta {0:t}"),
        [Message.NoAccounts] = ("No saved accounts", "Нет сохранённых аккаунтов", "No hay cuentas guardadas"),
        [Message.RefreshLimits] = ("Refresh limits", "Обновить лимиты", "Actualizar límites"),
        [Message.OpenFolder] = ("Open folder", "Открыть папку", "Abrir carpeta"),
        [Message.Autostart] = ("Start with Windows", "Автозапуск", "Iniciar con Windows"),
        [Message.On] = ("on", "вкл", "sí"),
        [Message.Off] = ("off", "выкл", "no"),
        [Message.Exit] = ("Exit", "Выход", "Salir"),
        [Message.ConfirmSwap] = ("Switch Codex to “{0}”?\n\nChatGPT will be closed and restarted.", "Переключить Codex на «{0}»?\n\nChatGPT будет закрыт и запущен заново.", "¿Cambiar Codex a «{0}»?\n\nChatGPT se cerrará y se reiniciará."),
        [Message.LimitWarning] = ("\n\nWarning: this account has reached its usage limit.", "\n\nВнимание: у этого аккаунта лимит исчерпан.", "\n\nAviso: esta cuenta ha alcanzado su límite de uso."),
        [Message.ActiveAccount] = ("Active: {0}", "Активен: {0}", "Cuenta activa: {0}"),
        [Message.SwapFailed] = ("CtxRelay: switch failed", "CtxRelay: свап не выполнен", "CtxRelay: no se pudo cambiar de cuenta"),
        [Message.WindowRemaining] = ("{0:0}% (resets {1})", "{0:0}% (сброс {1})", "{0:0}% (se restablece {1})"),
        [Message.TokenExpired] = ("access_token exp={0:yyyy-MM-dd HH:mm}Z (expired; request skipped)", "access_token exp={0:yyyy-MM-dd HH:mm}Z (истёк, запрос не делаю)", "access_token exp={0:yyyy-MM-dd HH:mm}Z (caducado; consulta omitida)"),
        [Message.MissingRateLimit] = ("rate_limit is missing", "нет rate_limit", "falta rate_limit"),
        [Message.KillTimeout] = ("taskkill did not finish within 30s", "taskkill не завершился за 30s", "taskkill no terminó en 30 s"),
        [Message.ProcessStillRunning] = ("{0}.exe still running after 15s: processes={1}", "{0}.exe жив через 15s: процессов={1}", "{0}.exe sigue activo tras 15 s: procesos={1}"),
        [Message.AccountNotFound] = ("Account '{0}' not found in {1}", "аккаунт '{0}' не найден в {1}", "No se encontró la cuenta '{0}' en {1}"),
        [Message.EmptyJson] = ("{0}: empty JSON", "{0}: пустой JSON", "{0}: JSON vacío"),
        [Message.MissingAccountId] = ("{0}: missing tokens.account_id", "{0}: нет tokens.account_id", "{0}: falta tokens.account_id"),
        [Message.RefreshUnavailable] = ("No refresh token. Sign in to this account in Codex again.", "Нет refresh_token. Войдите в этот аккаунт в Codex заново.", "No hay refresh_token. Vuelve a iniciar sesión con esta cuenta en Codex."),
        [Message.RefreshSignIn] = ("Refresh token expired, revoked, or already used. Sign in to this account in Codex again.", "Refresh_token истёк, отозван или уже использован. Войдите в этот аккаунт в Codex заново.", "El refresh_token caducó, fue revocado o ya se utilizó. Vuelve a iniciar sesión con esta cuenta en Codex."),
        [Message.RefreshFailed] = ("Token refresh failed (HTTP {0}).", "Не удалось обновить токен (HTTP {0}).", "No se pudo renovar el token (HTTP {0})."),
        [Message.InvalidRefreshResponse] = ("Token refresh returned no valid access token.", "Обновление не вернуло корректный access_token.", "La renovación no devolvió un access_token válido."),
        [Message.RefreshAccountChanged] = ("Account credentials changed during token refresh. Try refreshing limits again.", "Во время обновления токена сменился аккаунт. Обновите лимиты ещё раз.", "La cuenta cambió durante la renovación. Vuelve a actualizar los límites.")
    };

    public static string Text(Message message, params object?[] args)
    {
        var strings = Strings[message];
        var text = Language switch { "ru" => strings.Ru, "es" => strings.Es, _ => strings.En };
        if (string.IsNullOrEmpty(text)) text = strings.En;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }
}
