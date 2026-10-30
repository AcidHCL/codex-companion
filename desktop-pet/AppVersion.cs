namespace DesktopPet;

// Display the version of the running binary, so copied/older releases identify
// themselves correctly without keeping a second version string in the UI.
internal static class AppVersion
{
    public static string Number { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? L.T("未知", "Unknown");
    public static string Label => "v" + Number;
    public static string ProductLabel => "AI Pet " + Label;
    public static string WindowTitle(string page) => "AI Pet · " + page + " · " + Label;
}
