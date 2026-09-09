using Microsoft.UI.Xaml;

namespace Wildpinkler.App.Services;

// Theme is applied per-window: Application.RequestedTheme can only be set before the first window
// exists and cannot change at runtime.
public sealed class ThemeService
{
    private readonly AppSettingsStore _store;
    private readonly AppSettings _settings;

    public ThemeService(AppSettingsStore store, AppSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    public AppThemePreference Preference => _settings.Theme;

    public ElementTheme CurrentElementTheme => ToElementTheme(_settings.Theme);

    public void ApplyToWindow(Window window)
    {
        if (window.Content is FrameworkElement root)
            root.RequestedTheme = CurrentElementTheme;
    }

    // Saved immediately rather than at window close so a crash cannot lose the choice.
    public void SetPreference(AppThemePreference preference)
    {
        if (_settings.Theme == preference)
            return;

        _settings.Theme = preference;
        if (MainWindow.Instance is { } window)
            ApplyToWindow(window);

        _store.Save(_settings);
    }

    private static ElementTheme ToElementTheme(AppThemePreference preference) => preference switch
    {
        AppThemePreference.Light => ElementTheme.Light,
        AppThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };
}
