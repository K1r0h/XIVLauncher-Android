using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XlaBoot.Localization;
using XlaBoot.Views;

namespace XlaBoot.ViewModels;

public enum LauncherPage { Home, Login, Settings, Logs, About }

/// <summary>
/// The launcher's pages. Home, Settings, Logs and About sit on the bottom navigation; Login (reached from Play) is a
/// pushed page with a back arrow. Android's Back goes to Home from anywhere, then leaves the app.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHomePage), nameof(IsLoginPage), nameof(IsSettingsPage), nameof(IsLogsPage),
        nameof(IsAboutPage), nameof(ShowBottomNav))]
    private LauncherPage _page = LauncherPage.Home;

    public bool IsHomePage => Page == LauncherPage.Home;
    public bool IsLoginPage => Page == LauncherPage.Login;
    public bool IsSettingsPage => Page == LauncherPage.Settings;
    public bool IsLogsPage => Page == LauncherPage.Logs;
    public bool IsAboutPage => Page == LauncherPage.About;
    public bool ShowBottomNav => Page is LauncherPage.Home or LauncherPage.Settings or LauncherPage.Logs or LauncherPage.About;

    public LogsViewModel Logs { get; } = new();

    /// <summary>The home page's news panel (banners, notices, maintenance, events).</summary>
    public NewsViewModel News { get; } = new();

    partial void OnPageChanged(LauncherPage value)
    {
        switch (value)
        {
            case LauncherPage.Home: RefreshHome(); break;
            case LauncherPage.Settings: Settings.Open(); break;
            case LauncherPage.Logs: Logs.Refresh(); break;
        }
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        // A login in progress owns the screen: its status line is on the login page.
        if (IsBusy)
            return;
        // The game card opens Settings on General, where the install location is.
        if (page == "Game")
        {
            Settings.TabIndex = 0;
            page = nameof(LauncherPage.Settings);
        }
        if (!System.Enum.TryParse<LauncherPage>(page, out var target))
            return;
        if (target == Page && target == LauncherPage.Home)
            RefreshHome();
        Page = target;
    }

    /// <summary>Android Back. False when already home, so the platform does its default (leave the app).</summary>
    public bool GoBack()
    {
        if (Page == LauncherPage.Home)
            return false;
        if (!IsBusy)
            Page = LauncherPage.Home;
        return true;
    }

    /// <summary>About page's "Buy me a coffee".</summary>
    [RelayCommand]
    private void OpenKofi() => AppHost.OpenUrl?.Invoke(new System.Uri("https://ko-fi.com/zerothescyther"));

    /// <summary>About page's Discord button.</summary>
    [RelayCommand]
    private void OpenDiscord() => AppHost.OpenUrl?.Invoke(new System.Uri("https://discord.gg/FghfnnhRV8"));

    // ---- Home ------------------------------------------------------------------------------------

    [ObservableProperty]
    private string _gameTitle = "";

    [ObservableProperty]
    private string _gameDetail = "";

    [ObservableProperty]
    private Geometry _gameIcon = Icons.CheckCircle;

    [ObservableProperty]
    private bool _isGameMissing;

    /// <summary>
    /// Under Play: the installed game's version, so the separate "Game installed" card is not needed while the game
    /// is there (it only shows when the game is missing or unreadable, as the way to fix that).
    /// </summary>
    public string PlayDetail => SafeModeNextLaunch ? Strings.Home_PlaySafe
        : IsGameMissing || _shortVersion.Length == 0 ? Strings.Home_PlayDetail
        : Loc.F(Strings.Home_VersionShort, _shortVersion);

    /// <summary>The game version without its always-zero build fields: 2026.11.03.0000.0000 → 2026.11.03.</summary>
    private string _shortVersion = "";

    private static string ShortVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length > 3 ? string.Join('.', parts, 0, 3) : version;
    }

    public string VersionText => AppHost.VersionName.Length > 0
        ? $"{AppHost.VersionName} ({AppHost.VersionCode})"
        : Strings.About_DevBuild;

    /// <summary>Home's setup chip: the driver, DXVK and FEXCore the next launch will use.</summary>
    [ObservableProperty]
    private string _setupSummary = "";

    private static string BuildSetupSummary()
    {
        try
        {
            var driver = GraphicsDrivers.Selected(GraphicsDrivers.All()).Label;
            var dxvk = Imports.DxvkImport.Store(AppHost.FilesDir).Selected();
            var fex = Imports.FexImport.Store(AppHost.FilesDir).Selected();
            if (dxvk == Imports.ComponentStore.Stock) dxvk = Provisioning.RuntimeInstaller.StockDxvkVersion;
            if (fex == Imports.ComponentStore.Stock) fex = Provisioning.RuntimeInstaller.StockFexVersion;
            return $"{driver} · DXVK {dxvk} · FEX {fex}";
        }
        catch (System.Exception)
        {
            return ""; // runtime not installed yet: no chip
        }
    }

    /// <summary>Re-checked every time Home is shown: the folder or the storage permission may have changed in settings.</summary>
    public void RefreshHome()
    {
        var game = GameLocation.Check(GamePath);
        switch (game.Status)
        {
            case GameStatus.Present:
                _shortVersion = ShortVersion(game.Version);
                GameTitle = Strings.Home_GameInstalled;
                GameDetail = game.Version.Length > 0 ? Loc.F(Strings.Home_Version, game.Version) : Strings.Home_VersionUnknown;
                GameIcon = Icons.CheckCircle;
                IsGameMissing = false;
                break;
            case GameStatus.Unreadable:
                GameTitle = Strings.Home_FolderUnreadable;
                GameDetail = Strings.Home_FolderUnreadableDetail;
                GameIcon = Icons.Warning;
                IsGameMissing = true;
                break;
            default:
                GameTitle = Strings.Home_NoGame;
                GameDetail = Strings.Home_NoGameDetail;
                GameIcon = Icons.Warning;
                IsGameMissing = true;
                break;
        }
        OnPropertyChanged(nameof(PlayDetail));
        SetupSummary = BuildSetupSummary();
        News.Refresh(GameLanguage);
    }
}
