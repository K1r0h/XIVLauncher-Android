using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XlaBoot.Localization;

namespace XlaBoot.ViewModels;

/// <summary>One log the Logs page can show.</summary>
public sealed partial class LogSource : ObservableObject
{
    public string Label { get; }
    public string Path { get; }

    [ObservableProperty]
    private bool _isSelected;

    public LogSource(string label, string path)
    {
        Label = label;
        Path = path;
    }
}

/// <summary>One line of a log, coloured by severity so errors stand out while scrolling.</summary>
public sealed class LogLine
{
    private static readonly IBrush Normal = new ImmutableSolidColorBrush(Color.Parse("#C8D4F0"));
    private static readonly IBrush Warning = new ImmutableSolidColorBrush(Color.Parse("#E5C07B"));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#E06C75"));

    public string Text { get; }
    public IBrush Foreground { get; }

    public LogLine(string text)
    {
        Text = text;
        Foreground = IsError(text) ? Error : IsWarning(text) ? Warning : Normal;
    }

    // Dalamud/Serilog ([ERR], [FTL], [WRN]), Wine (err:, warn:, fixme: stays normal) and plain words.
    private static bool IsError(string t) =>
        t.Contains("[ERR]") || t.Contains("[FTL]") || t.Contains(" err:") || t.StartsWith("err:")
        || t.Contains("Exception") || t.Contains("FATAL");

    private static bool IsWarning(string t) =>
        t.Contains("[WRN]") || t.Contains(" warn:") || t.StartsWith("warn:");
}

/// <summary>
/// The Logs page: the tail of Dalamud's and Wine's logs, readable on the phone without adb, and copyable into a bug
/// report. Only logs that exist are offered.
/// </summary>
public partial class LogsViewModel : ViewModelBase
{
    /// <summary>Enough to hold a crash and what led up to it, small enough to lay out instantly.</summary>
    private const int TailBytes = 96 * 1024;

    public ObservableCollection<LogSource> Sources { get; } = new();

    /// <summary>The whole tail as one string: what Copy puts on the clipboard.</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>
    /// The same tail split into lines for display. One text block per line in a virtualizing list lays out only the
    /// lines on screen; one SelectableTextBlock holding all ~96 KB re-laid out the whole log on every scroll step.
    /// </summary>
    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<LogLine> _lines = Array.Empty<LogLine>();

    partial void OnTextChanged(string value) =>
        Lines = value.Split('\n').Select(l => new LogLine(l.TrimEnd('\r'))).ToArray();

    [ObservableProperty]
    private string _detail = "";

    public bool HasSources => Sources.Count > 0;

    private string? _selectedPath;

    public void Refresh()
    {
        var files = AppHost.FilesDir;
        var dalamudLogs = System.IO.Path.Combine(files, "xlroaming", "logs");
        var candidates = new[]
        {
            new LogSource("Launcher", AppLog.Path(files)),
            new LogSource("Dalamud", System.IO.Path.Combine(dalamudLogs, "dalamud.log")),
            new LogSource("Dalamud boot", System.IO.Path.Combine(dalamudLogs, "dalamud.boot.log")),
            new LogSource("Injector", System.IO.Path.Combine(dalamudLogs, "dalamud.injector.log")),
            new LogSource("Wine", System.IO.Path.Combine(files, "wine-test.log")),
            new LogSource("X server", System.IO.Path.Combine(files, "xserver.log")),
        };

        Sources.Clear();
        foreach (var source in candidates.Where(c => File.Exists(c.Path)))
            Sources.Add(source);
        OnPropertyChanged(nameof(HasSources));

        var selected = Sources.FirstOrDefault(s => s.Path == _selectedPath) ?? Sources.FirstOrDefault();
        Show(selected);
    }

    [RelayCommand]
    private void Select(LogSource source) => Show(source);

    [RelayCommand]
    private void Reload() => Refresh();

    /// <summary>Zips every log plus the launch settings, then lets the user save the zip wherever they like.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task Export()
    {
        if (AppHost.SaveFileAs == null)
        {
            Detail = Strings.Logs_NoSave;
            return;
        }
        var name = LogExport.SuggestedName(DateTime.Now);
        var zip = System.IO.Path.Combine(AppHost.CacheDir.Length > 0 ? AppHost.CacheDir : System.IO.Path.GetTempPath(), name);
        try
        {
            string driver;
            try { driver = GraphicsDrivers.Selected(GraphicsDrivers.All()).Label; }
            catch (Exception) { driver = "unknown"; }
            var info = $"XIVLauncher Android {AppHost.VersionName} ({AppHost.VersionCode})\n"
                       + $"{AppHost.DeviceDescription}\n"
                       + $"Graphics driver: {driver}\n"
                       + $"Dalamud: {AppHost.Get("dalamud_enabled", "OFF")}\n"
                       + $"Exported {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}\n"
                       + $"Game folder: {MainViewModel.GamePath}\n"
                       + (ProcessExits.Newest() is { Length: > 0 } exit ? $"Last exit: {exit}\n" : "");
            var count = await System.Threading.Tasks.Task.Run(() => LogExport.Create(AppHost.FilesDir, zip, info));
            if (count == 0)
            {
                Detail = Strings.Logs_NothingToExport;
                return;
            }
            var saved = await AppHost.SaveFileAs(name, "application/zip", zip);
            Detail = saved == null ? Strings.Logs_ExportCancelled : Loc.F(Strings.Logs_Saved, count, saved);
        }
        catch (Exception ex)
        {
            Detail = Loc.F(Strings.Logs_ExportFailed, ex.Message);
        }
        finally
        {
            try { File.Delete(zip); }
            catch (Exception) { /* the cache dir is Android's to reclaim */ }
        }
    }

    private void Show(LogSource? source)
    {
        foreach (var s in Sources)
            s.IsSelected = s == source;
        _selectedPath = source?.Path;
        if (source == null)
        {
            Text = "";
            Detail = Strings.Logs_None;
            return;
        }

        try
        {
            using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            stream.ReadExactly(buffer);
            var text = Encoding.UTF8.GetString(buffer);
            if (start > 0 && text.IndexOf('\n') is var cut and >= 0)
                text = text[(cut + 1)..]; // drop the partial first line
            Text = text.Length > 0 ? text : Strings.Logs_Empty;
            Detail = $"{source.Path}  ·  {length / 1024.0:F0} KB" + (start > 0 ? "  ·  " + Strings.Logs_ShowingEnd : "")
                     + "  ·  " + Loc.F(Strings.Logs_Updated, File.GetLastWriteTime(source.Path).ToString("HH:mm:ss"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Text = "";
            Detail = Loc.F(Strings.Logs_ReadFailed, source.Label, ex.Message);
        }
    }
}
