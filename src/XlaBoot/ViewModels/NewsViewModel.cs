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
        LoadCache();
        // Settings > General > News on Home: BOTH, NEWS, BANNERS or OFF.
        var feed = AppHost.Get("home_feed", "BOTH");
        _newsEnabled = feed is "BOTH" or "NEWS";
        _bannersEnabled = feed is "BOTH" or "BANNERS";
        UpdateVisibility();
        RefreshGateLater(language);
        if (!_newsEnabled && !_bannersEnabled)
            return;
        if (_loading || DateTime.UtcNow - _loadedAt < RefreshAfter)
            return;
        _loading = true;
        _ = Task.Run(() => LoadAsync(language));
    }

    private async Task LoadAsync(ClientLanguage language)
    {
        await Frontier.WaitAsync().ConfigureAwait(false);
        try
        {
            var launcher = new Launcher((ISteam?)null, new CommonUniqueIdCache(null), FrontierUrl, "en-us");
            // One request at a time: two XIVLauncher.Common calls in flight at once make Mono resolve its
            // dependencies on two threads, which crashes the assembly loader (see Frontier). The text shows as
            // soon as it is in; the banner list and images follow.
            var headlines = await Headlines.GetNews(launcher, language).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Fill(Pinned, headlines.Pinned?.Select(n => ToItem(n, language)));
                Fill(News, headlines.News?.Select(n => ToItem(n, language)));
                Fill(Topics, headlines.Topics?.Select(n => ToItem(n, language)));
                Failed = false;
                UpdateVisibility();
            });
            var banners = await Headlines.GetBanners(launcher, language).ConfigureAwait(false);

            var images = await Task.WhenAll((banners ?? Array.Empty<Banner>())
                .Where(b => b.LsbBanner != null && b.Link != null)
                .OrderBy(b => b.OrderPriority ?? int.MaxValue)
                .Select(async b =>
                {
                    try
                    {
                        var bytes = await ImageHttp.GetByteArrayAsync(b.LsbBanner).ConfigureAwait(false);
                        return (Banner: new NewsBanner { Image = new Bitmap(new MemoryStream(bytes)), Url = b.Link.ToString() },
                            Bytes: bytes);
                    }
                    catch (Exception)
                    {
                        return ((NewsBanner Banner, byte[] Bytes)?)null; // one missing image is not worth failing the panel for
                    }
                })).ConfigureAwait(false);

            var loaded = images.Where(i => i != null).Select(i => i!.Value).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (loaded.Count > 0 || Banners.Count == 0)
                {
                    Fill(Banners, loaded.Select(i => i.Banner));
                    ShowBanner(0);
                }
                UpdateVisibility();
            });
            _loadedAt = DateTime.UtcNow;
            SaveCache(loaded);
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
            Frontier.Release();
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

    // ---- Server status chip ----------------------------------------------------------------------

    /// <summary>Whether gate_status.json answered at all; the chip hides until it has.</summary>
    [ObservableProperty] private bool _hasGateStatus;

    /// <summary>The login gate: open = servers up, closed = maintenance.</summary>
    [ObservableProperty] private bool _gateOpen;

    private DateTime _gateCheckedAt = DateTime.MinValue;

    /// <summary>
    /// The news load and the gate check take turns. Started together right after launch, both made Mono load
    /// XIVLauncher.Common's dependencies on two threads at once, which crashed the assembly loader (SIGSEGV in
    /// mono_assembly_request_byname) or failed with FileNotFoundException for System.Data.Common.
    /// </summary>
    private static readonly System.Threading.SemaphoreSlim Frontier = new(1, 1);
    private bool _gateLoading;

    /// <summary>Queued just after the news load so the headlines are not kept waiting behind it.</summary>
    private void RefreshGateLater(ClientLanguage language) =>
        Dispatcher.UIThread.Post(() => RefreshGate(language), DispatcherPriority.Background);

    /// <summary>The same gate check the official launcher makes, at most once a minute, independent of the news setting.</summary>
    private void RefreshGate(ClientLanguage language)
    {
        if (_gateLoading || DateTime.UtcNow - _gateCheckedAt < TimeSpan.FromMinutes(1))
            return;
        _gateLoading = true;
        _ = Task.Run(async () =>
        {
            await Frontier.WaitAsync().ConfigureAwait(false);
            try
            {
                var launcher = new Launcher((ISteam?)null, new CommonUniqueIdCache(null), FrontierUrl, "en-us");
                var gate = await launcher.GetGateStatus(language).ConfigureAwait(false);
                _gateCheckedAt = DateTime.UtcNow;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GateOpen = gate.Status;
                    HasGateStatus = true;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"XlaLauncher: gate status unavailable ({ex.GetType().Name}: {ex.Message} / {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message})");
            }
            finally
            {
                _gateLoading = false;
                Frontier.Release();
            }
        });
    }

    /// <summary>
    /// The feed leaves url empty for Lodestone articles and gives only the id. Headlines.GetNews fills those in for
    /// News and Topics but not for Pinned, so the Important rows opened nothing; build the Lodestone link here.
    /// </summary>
    // ---- Disk cache: the last headlines and banner images show instantly at the next launch ----------

    private sealed record CachedItem(string Title, string Date, string? Tag, string Url);
    private sealed record CachedNews(CachedItem[] Pinned, CachedItem[] News, CachedItem[] Topics, string[] BannerUrls);

    private static string CacheDir => System.IO.Path.Combine(
        AppHost.CacheDir.Length > 0 ? AppHost.CacheDir : System.IO.Path.Combine(AppHost.FilesDir, "cache"), "news");

    private bool _cacheLoaded;

    /// <summary>Once per process, before the first download: whatever the last successful load saved.</summary>
    private void LoadCache()
    {
        if (_cacheLoaded)
            return;
        _cacheLoaded = true;
        try
        {
            var index = System.IO.Path.Combine(CacheDir, "news.json");
            if (!File.Exists(index))
                return;
            var cached = System.Text.Json.JsonSerializer.Deserialize<CachedNews>(File.ReadAllText(index));
            if (cached == null)
                return;
            NewsItem Item(CachedItem c) => new() { Title = c.Title, Date = c.Date, Tag = c.Tag, Url = c.Url };
            Fill(Pinned, cached.Pinned.Select(Item));
            Fill(News, cached.News.Select(Item));
            Fill(Topics, cached.Topics.Select(Item));
            var banners = new System.Collections.Generic.List<NewsBanner>();
            for (var i = 0; i < cached.BannerUrls.Length; i++)
            {
                var file = System.IO.Path.Combine(CacheDir, $"banner{i}.img");
                if (File.Exists(file))
                    banners.Add(new NewsBanner { Image = new Bitmap(file), Url = cached.BannerUrls[i] });
            }
            Fill(Banners, banners);
            ShowBanner(0);
            UpdateVisibility();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: news cache unreadable ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void SaveCache(System.Collections.Generic.List<(NewsBanner Banner, byte[] Bytes)> banners)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            foreach (var old in Directory.GetFiles(CacheDir, "banner*.img"))
                File.Delete(old);
            for (var i = 0; i < banners.Count; i++)
                File.WriteAllBytes(System.IO.Path.Combine(CacheDir, $"banner{i}.img"), banners[i].Bytes);
            CachedItem C(NewsItem n) => new(n.Title, n.Date, n.Tag, n.Url);
            CachedNews snapshot = null!;
            Dispatcher.UIThread.Invoke(() => snapshot = new CachedNews(Pinned.Select(C).ToArray(),
                News.Select(C).ToArray(), Topics.Select(C).ToArray(), banners.Select(b => b.Banner.Url).ToArray()));
            File.WriteAllText(System.IO.Path.Combine(CacheDir, "news.json"),
                System.Text.Json.JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"XlaLauncher: news cache not saved ({ex.GetType().Name}: {ex.Message})");
        }
    }

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
