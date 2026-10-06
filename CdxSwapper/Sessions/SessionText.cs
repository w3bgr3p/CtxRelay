namespace CdxSwapper.Sessions;

static class SessionText
{
    public static string Pick(string en, string ru, string es) => Localization.Language switch { "ru" => ru, "es" => es, _ => en };
}
