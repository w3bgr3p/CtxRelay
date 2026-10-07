namespace CtxDeck;

/// <summary>Лог в файл рядом с хранилищем. Сам никогда не бросает.</summary>
static class Log
{
    static readonly object Gate = new();
    public static string? FilePath { get; set; }

    public static void Write(string msg)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            if (FilePath == null) return;
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000)
                    File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch { }
    }

    public static string Short(object? value, int limit = 300)
    {
        string s;
        try { s = value?.ToString() ?? ""; } catch { return "<unprintable>"; }
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= limit ? s : s[..limit] + "...";
    }

    /// <summary>step + тип + текст исключения, без трактовки.</summary>
    public static string Error(string step, Exception e) => $"step={step} | {e.GetType().Name}: {Short(e.Message)}";
}
