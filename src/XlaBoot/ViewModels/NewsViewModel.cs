using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XIVLauncher.Common;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.PlatformAbstractions; // CommonUniqueIdCache lives here despite its folder

namespace XlaBoot.ViewModels;

/// <summary>One headline: a notice, a maintenance/recovery post, or a topic. Tapping it opens the Lodestone page.</summary>
public sealed class NewsItem
{
    public required string Title { get; init; }
    public required string Date { get; init; }
    public string? Tag { get; init; }
    public bool HasTag => !string.IsNullOrEmpty(Tag);
    public required string Url { get; init; }
    public IRelayCommand OpenCommand => new RelayCommand(() => NewsViewModel.OpenUrl(Url));
}

/// <summary>One dot under the banner carousel; the current banner's dot is drawn as a short bar.</summary>
public sealed partial class BannerDot : ObservableObject
{
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>A banner from the official launcher's carousel, with its image already downloaded.</summary>
public sealed class NewsBanner
{
    public required Bitmap Image { get; init; }
    public required string Url { get; init; }
    public IRelayCommand OpenCommand => new RelayCommand(() => NewsViewModel.OpenUrl(Url));
}

/// <summary>
/// The official launcher's news panel for the home page: banners, pinned notices, news (maintenance and the like) and
/// topics (events), read from the same frontier.ffxiv.com feeds XIVLauncher uses (XIVLauncher.Common Headlines).
/// Loaded in the background and refreshed at most every ten minutes; a failure only shows a short line.
/// </summary>
public partial class NewsViewModel : ObservableObject
{
    private const string FrontierUrl = "https://launcher.finalfantasyxiv.com/v740/index.html?rc_lang={0}&time={1}";
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);

    private DateTime _loadedAt = DateTime.MinValue;
    private bool _loading;

    public ObservableCollection<NewsBanner> Banners { get; } = new();
    public ObservableCollection<NewsItem> Pinned { get; } = new();
    public ObservableCollection<NewsItem> News { get; } = new();
    public ObservableCollection<NewsItem> Topics { get; } = new();

    [ObservableProperty] private bool _hasBanners;
    [ObservableProperty] private bool _hasPinned;
    [ObservableProperty] private bool _hasNews;
    [ObservableProperty] private bool _hasTopics;
    [ObservableProperty] private bool _failed;

    /// <summary>The one banner on show; the header steps through them with taps on its left and right edges.</summary>
    [ObservableProperty] private NewsBanner? _currentBanner;
    [ObservableProperty] private string _bannerPosition = "";

    /// <summary>One dot per banner, the current one highlighted.</summary>
    public ObservableCollection<BannerDot> BannerDots { get; } = new();
    private int _bannerIndex;

    [RelayCommand]
    private void PreviousBanner()
    {
        ShowBanner(_bannerIndex - 1);
        _bannerTimer?.Stop(); // a manual step gets a full interval before the next automatic one
        _bannerTimer?.Start();
    }

    [RelayCommand]
    private void NextBanner()
    {
        ShowBanner(_bannerIndex + 1);
        _bannerTimer?.Stop();
        _bannerTimer?.Start();
    }

    /// <summary>Steps to the next banner on its own every few seconds while banners are on show.</summary>
    private static readonly TimeSpan BannerInterval = TimeSpan.FromSeconds(7);
    private DispatcherTimer? _bannerTimer;

    private void UpdateBannerTimer()
    {
        if (HasBanners && Banners.Count > 1)
        {
            _bannerTimer ??= new DispatcherTimer(BannerInterval, DispatcherPriority.Background,
                (_, _) => ShowBanner(_bannerIndex + 1));
            _bannerTimer.Start();
        }
        else
            _bannerTimer?.Stop();
    }

    [RelayCommand]
    private void OpenBanner() => OpenUrl(CurrentBanner?.Url);

    /// <summary>Wraps around at both ends.</summary>
    private void ShowBanner(int index)
    {
        if (Banners.Count == 0)
        {
            CurrentBanner = null;
            BannerPosition = "";
            return;
        }
        _bannerIndex = ((index % Banners.Count) + Banners.Count) % Banners.Count;
        CurrentBanner = Banners[_bannerIndex];
        BannerPosition = $"{_bannerIndex + 1} / {Banners.Count}";
        while (BannerDots.Count < Banners.Count)
            BannerDots.Add(new BannerDot());
        while (BannerDots.Count > Banners.Count)
            BannerDots.RemoveAt(BannerDots.Count - 1);
        for (var i = 0; i < BannerDots.Count; i++)
            BannerDots[i].IsCurrent = i == _bannerIndex;
    }

    /// <summary>Called whenever Home is shown; does nothing while a load is running or the last one is recent.</summary>
    public void Refresh(ClientLanguage language)
    {
        // Settings > General > News on Home: BOTH, NEWS, BANNERS or OFF.
        var feed = AppHost.Get("home_feed", "BOTH");
        _newsEnabled = feed is "BOTH" or "NEWS";
        _bannersEnabled = feed is "BOTH" or "BANNERS";
        UpdateVisibility();
        if (!_newsEnabled && !_bannersEnabled)
            return;
        if (_loading || DateTime.UtcNow - _loadedAt < RefreshAfter)
            return;
        _loading = true;
        _ = Task.Run(() => LoadAsync(language));
    }

    private async Task LoadAsync(ClientLanguage language)
    {
        try
        {
            var launcher = new Launcher((ISteam?)null, new CommonUniqueIdCache(null), FrontierUrl, "en-us");
            var headlines = await Headlines.GetNews(launcher, language).ConfigureAwait(false);
            var banners = await Headlines.GetBanners(launcher, language).ConfigureAwait(false);

            var images = await Task.WhenAll((banners ?? Array.Empty<Banner>())
                .Where(b => b.LsbBanner != null && b.Link != null)
                .OrderBy(b => b.OrderPriority ?? int.MaxValue)
                .Select(async b =>
                {
                    try
                    {
                        var bytes = await ImageHttp.GetByteArrayAsync(b.LsbBanner).ConfigureAwait(false);
                        return new NewsBanner { Image = new Bitmap(new MemoryStream(bytes)), Url = b.Link.ToString() };
                    }
                    catch (Exception)
                    {
                        return null; // one missing banner image is not worth failing the panel for
                    }
                })).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Fill(Banners, images.OfType<NewsBanner>());
                Fill(Pinned, headlines.Pinned?.Select(n => ToItem(n, language)));
                Fill(News, headlines.News?.Select(n => ToItem(n, language)));
                Fill(Topics, headlines.Topics?.Select(n => ToItem(n, language)));
                ShowBanner(0);
                Failed = false;
                UpdateVisibility();
            });
            _loadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: news unavailable ({ex.GetType().Name}: {ex.Message})");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Failed = Pinned.Count + News.Count + Topics.Count == 0;
                UpdateVisibility();
            });
        }
        finally
        {
            _loading = false;
        }
    }

    private bool _newsEnabled = true, _bannersEnabled = true;

    /// <summary>What the home page shows: loaded content, minus what Settings turned off.</summary>
    private void UpdateVisibility()
    {
        HasBanners = _bannersEnabled && Banners.Count > 0;
        HasPinned = _newsEnabled && Pinned.Count > 0;
        HasNews = _newsEnabled && News.Count > 0;
        HasTopics = _newsEnabled && Topics.Count > 0;
        ShowFailure = _newsEnabled && Failed;
        UpdateBannerTimer();
    }

    [ObservableProperty] private bool _showFailure;

    /// <summary>
    /// The feed leaves url empty for Lodestone articles and gives only the id. Headlines.GetNews fills those in for
    /// News and Topics but not for Pinned, so the Important rows opened nothing; build the Lodestone link here.
    /// </summary>
    private static NewsItem ToItem(News n, ClientLanguage language) => new()
    {
        Title = n.Title,
        Date = n.Date.ToLocalTime().ToString("MMM d"),
        Tag = string.IsNullOrWhiteSpace(n.Tag) ? null : n.Tag,
        Url = !string.IsNullOrEmpty(n.Url) || string.IsNullOrEmpty(n.Id) ? n.Url
            : $"https://{language.GetLangCodeLodestone()}.finalfantasyxiv.com/lodestone/news/detail/{n.Id}",
    };

    private static void Fill<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T>? items)
    {
        target.Clear();
        if (items == null)
            return;
        foreach (var item in items)
            target.Add(item);
    }

    internal static void OpenUrl(string? url)
    {
        if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            AppHost.OpenUrl?.Invoke(uri);
    }
}
