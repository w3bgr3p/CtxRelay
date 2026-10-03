using System.Globalization;

namespace CdxSwapper;

enum Message
{
    FileAuthEnabled, NoAuth, UsageError, FiveHours, Week, WeekShort, Remaining,
    NoActiveAuth, Refreshing, NotFetched, UpdatedAt, NoData, Error, LimitUntil,
    NoAccounts, RefreshLimits, OpenFolder, Autostart, On, Off, Exit, ConfirmSwap,
    LimitWarning, ActiveAccount, SwapFailed, WindowRemaining, TokenExpired,
    MissingRateLimit, KillTimeout, ProcessStillRunning, AccountNotFound,
    EmptyJson, MissingAccountId
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
        [Message.SwapFailed] = ("CdxSwapper: switch failed", "CdxSwapper: свап не выполнен", "CdxSwapper: no se pudo cambiar de cuenta"),
        [Message.WindowRemaining] = ("{0:0}% (resets {1})", "{0:0}% (сброс {1})", "{0:0}% (se restablece {1})"),
        [Message.TokenExpired] = ("access_token exp={0:yyyy-MM-dd HH:mm}Z (expired; request skipped)", "access_token exp={0:yyyy-MM-dd HH:mm}Z (истёк, запрос не делаю)", "access_token exp={0:yyyy-MM-dd HH:mm}Z (caducado; consulta omitida)"),
        [Message.MissingRateLimit] = ("rate_limit is missing", "нет rate_limit", "falta rate_limit"),
        [Message.KillTimeout] = ("taskkill did not finish within 30s", "taskkill не завершился за 30s", "taskkill no terminó en 30 s"),
        [Message.ProcessStillRunning] = ("{0}.exe still running after 15s: processes={1}", "{0}.exe жив через 15s: процессов={1}", "{0}.exe sigue activo tras 15 s: procesos={1}"),
        [Message.AccountNotFound] = ("Account '{0}' not found in {1}", "аккаунт '{0}' не найден в {1}", "No se encontró la cuenta '{0}' en {1}"),
        [Message.EmptyJson] = ("{0}: empty JSON", "{0}: пустой JSON", "{0}: JSON vacío"),
        [Message.MissingAccountId] = ("{0}: missing tokens.account_id", "{0}: нет tokens.account_id", "{0}: falta tokens.account_id")
    };

    public static string Text(Message message, params object?[] args)
    {
        var strings = Strings[message];
        var text = Language switch { "ru" => strings.Ru, "es" => strings.Es, _ => strings.En };
        if (string.IsNullOrEmpty(text)) text = strings.En;
        return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
    }
}
