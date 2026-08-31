using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using Wildpinkler.App.Pages;
using Wildpinkler.App.Services;
using Wildpinkler.App.ViewModels;

namespace Wildpinkler.App;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1280;
    private const int DefaultWindowHeight = 800;
    private const int MinimumWindowWidth = 800;
    private const int MinimumWindowHeight = 600;

    private readonly Dictionary<string, Type> _pagesByTag = new();
    private readonly ActiveProfileContext _profileContext = Services.AppServices.ActiveProfile;
    private readonly AppSettings _settings = Services.AppServices.AppSettings;
    private RectInt32 _restoreBounds;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Instance = this;
        Services.AppServices.ThemeService.ApplyToWindow(this);
        
        // Set app icon from Assets/AppIcon folder
        SetAppIcon();
        
        _profileContext.PropertyChanged += (_, _) => UpdateTitleBarSubtitle();
        UpdateTitleBarSubtitle();
        BuildNavigation();
        ContentFrame.Navigated += ContentFrame_Navigated;

        NavView.IsPaneOpen = _settings.IsNavigationPaneOpen;
        ApplyWindowState();
        AppWindow.Changed += AppWindow_Changed;
        AppWindow.Closing += AppWindow_Closing;

        _ = NavigateToStartupPageAsync();
    }

    public static MainWindow? Instance { get; private set; }

    public void NavigateToSettings()
    {
        NavView.SelectedItem = NavView.SettingsItem;
        ContentFrame.Navigate(typeof(SettingsPage));
    }

    public void NavigateToSection(string tag)
    {
        if (!_pagesByTag.TryGetValue(tag, out var pageType))
            return;
        NavView.SelectedItem = FindMenuItem(tag);
        ContentFrame.Navigate(pageType);
    }

    // Unpackaged WinUI 3 pickers must be bound to a window handle before they can be shown.
    public static IntPtr WindowHandle =>
        Instance is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(Instance);

    private void UpdateTitleBarSubtitle() =>
        AppTitleBar.Subtitle = $"{_profileContext.ProfileName} \u00b7 {_profileContext.GameName}";

    // Games is the landing view until at least one game exists; after that, Profiles.
    private async Task NavigateToStartupPageAsync()
    {
        var tag = NavigationCatalog.GamesTag;
        try
        {
            var games = await Services.AppServices.GameStore.LoadAsync();
            if (games.Count > 0)
                tag = NavigationCatalog.ProfilesTag;
        }
        catch (Exception)
        {
            // GamesPage surfaces the load failure itself.
        }

        NavView.SelectedItem = FindMenuItem(tag);
        ContentFrame.Navigate(_pagesByTag[tag]);
    }

    private void ApplyWindowState()
    {
        _restoreBounds = ClampToWorkArea(ReadSavedBounds() ?? CreateDefaultBounds());
        AppWindow.MoveAndResize(_restoreBounds);

        if (_settings.IsWindowMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private RectInt32? ReadSavedBounds()
    {
        if (_settings.WindowLeft is not int left || _settings.WindowTop is not int top ||
            _settings.WindowWidth is not int width || _settings.WindowHeight is not int height)
            return null;

        return new RectInt32(left, top, width, height);
    }

    private static RectInt32 CreateDefaultBounds()
    {
        var workArea = DisplayArea.Primary.WorkArea;
        return new RectInt32(
            workArea.X + ((workArea.Width - DefaultWindowWidth) / 2),
            workArea.Y + ((workArea.Height - DefaultWindowHeight) / 2),
            DefaultWindowWidth,
            DefaultWindowHeight);
    }

    // Keeps the window on-screen when a saved display is gone, and shrinks the default to fit small ones.
    private static RectInt32 ClampToWorkArea(RectInt32 bounds)
    {
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Clamp(bounds.Width, Math.Min(MinimumWindowWidth, workArea.Width), workArea.Width);
        var height = Math.Clamp(bounds.Height, Math.Min(MinimumWindowHeight, workArea.Height), workArea.Height);
        var left = Math.Clamp(bounds.X, workArea.X, workArea.X + workArea.Width - width);
        var top = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + workArea.Height - height);
        return new RectInt32(left, top, width, height);
    }

    // OverlappedPresenter exposes no restore bounds, so track them while the window is restored.
    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange)
            return;

        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            _restoreBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _settings.IsNavigationPaneOpen = NavView.IsPaneOpen;
        _settings.IsWindowMaximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        _settings.WindowLeft = _restoreBounds.X;
        _settings.WindowTop = _restoreBounds.Y;
        _settings.WindowWidth = _restoreBounds.Width;
        _settings.WindowHeight = _restoreBounds.Height;
        Services.AppServices.AppSettingsStore.Save(_settings);
    }

    private NavigationViewItem FindMenuItem(string tag) =>
        NavView.MenuItems
            .OfType<NavigationViewItem>()
            .First(item => (string)item.Tag == tag);

    // Selection follows the frame, so back/forward navigation moves the highlight too.
    private void ContentFrame_Navigated(object sender, NavigationEventArgs args)
    {
        NavView.IsBackEnabled = ContentFrame.CanGoBack;

        if (args.SourcePageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        var tag = _pagesByTag.FirstOrDefault(entry => entry.Value == args.SourcePageType).Key;
        if (tag is not null)
            NavView.SelectedItem = FindMenuItem(tag);
    }

    private void BuildNavigation()
    {
        foreach (var entry in NavigationCatalog.Sections)
        {
            _pagesByTag[entry.Tag] = entry.PageType;
            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = entry.Label,
                Tag = entry.Tag,
                Icon = new FontIcon { Glyph = entry.IconGlyph },
            });
        }
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            ContentFrame.Navigate(typeof(SettingsPage));
            return;
        }

        var tag = args.InvokedItemContainer?.Tag as string;
        if (tag is not null && _pagesByTag.TryGetValue(tag, out var pageType))
            ContentFrame.Navigate(pageType);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
            ContentFrame.GoBack();
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    /// <summary>
    /// Sets the app icon from the Assets/AppIcon folder.
    /// Tries to load the 256×256 PNG icon; falls back to 128×128 if not available.
    /// </summary>
    private void SetAppIcon()
    {
        try
        {
            var appFolder = AppContext.BaseDirectory;
            var iconPath = Path.Combine(appFolder, "Assets", "AppIcon", "Wildpinkler-Icon-256.png");
            
            // Fallback to 128×128 if 256×256 not available
            if (!File.Exists(iconPath))
                iconPath = Path.Combine(appFolder, "Assets", "AppIcon", "Wildpinkler-Icon-128.png");
            
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            // Silently ignore icon-loading failures; app is fully functional without custom icon
            System.Diagnostics.Debug.WriteLine($"Warning: Failed to set app icon: {ex.Message}");
        }
    }
}
