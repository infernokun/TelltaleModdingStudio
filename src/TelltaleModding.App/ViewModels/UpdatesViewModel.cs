using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TelltaleModding.App.Services;

namespace TelltaleModding.App.ViewModels;

/// <summary>What the user picked in the new version popup.</summary>
public enum UpdateChoice { Later, Download, Skip }

/// <summary>
/// Checks GitHub for a newer release: quietly on start (when the setting is on), and on
/// demand from the status bar, which always reports what it found. The outcome shows next
/// to the version at the left of the status bar.
/// </summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private UpdateChecker? _checker;

    /// <param name="checker">Tests pass one over a fake handler; otherwise one is made on first use.</param>
    public UpdatesViewModel(AppSettings settings, UpdateChecker? checker = null)
    {
        _settings = settings;
        _checker = checker;
    }

    private UpdateChecker Checker => _checker ??= new UpdateChecker();

    /// <summary>Set by the window to show the new version popup; null in unit tests.</summary>
    public Func<UpdatePrompt, Task<UpdateChoice>>? ShowPrompt { get; set; }

    /// <summary>Opens a link in the default browser (replaced in tests).</summary>
    public Action<string> OpenUrl { get; set; } = OpenInBrowser;

    [ObservableProperty]
    private bool _isChecking;

    /// <summary>The running version, at the left of the status bar.</summary>
    public string VersionLabel => $"Version {Checker.CurrentVersionText}";

    /// <summary>What the last check found, shown after the version; empty until there is something to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCheckStatus))]
    private string _checkStatus = "";

    /// <summary>Why the last check failed (the status's tooltip), or null.</summary>
    [ObservableProperty]
    private string? _checkStatusDetail;

    public bool HasCheckStatus => CheckStatus.Length > 0;

    /// <summary>
    /// The start-up check: notes the result next to the version and offers a version the user
    /// has not skipped. A failure, or a skipped version, says nothing.
    /// </summary>
    public async Task CheckOnStartAsync()
    {
        if (!_settings.Preferences.CheckForUpdatesOnStart || IsChecking)
            return;
        var result = await RunCheckAsync();
        if (result is { Status: UpdateStatus.UpdateAvailable, Release: { } release })
        {
            if (IsSkipped(release))
                return;
            Report($"Version {VersionText(release)} is available");
            await OfferAsync(release);
        }
        else if (result.Status == UpdateStatus.UpToDate)
        {
            Report("Up to date");
        }
    }

    /// <summary>The status bar button: reports the outcome either way and ignores skipped versions.</summary>
    [RelayCommand]
    private async Task CheckNowAsync()
    {
        Report("Checking for updates…");
        var result = await RunCheckAsync();
        switch (result.Status)
        {
            case UpdateStatus.UpdateAvailable when result.Release is { } release:
                Report($"Version {VersionText(release)} is available");
                await OfferAsync(release);
                break;
            case UpdateStatus.Failed:
                Report("Couldn't check for updates", result.Error);
                break;
            default:
                Report("Up to date");
                break;
        }
    }

    private void Report(string status, string? detail = null)
    {
        CheckStatus = status;
        CheckStatusDetail = detail;
    }

    private async Task<UpdateCheckResult> RunCheckAsync()
    {
        IsChecking = true;
        try
        {
            // Off the UI thread so the first request's setup never holds up the window.
            var checker = Checker;
            return await Task.Run(() => checker.CheckAsync());
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>True when the user chose to skip this release's version.</summary>
    public bool IsSkipped(ReleaseInfo release) =>
        UpdateChecker.TryParseVersion(_settings.SkippedUpdateVersion) is { } skipped && skipped == release.Version;

    private async Task OfferAsync(ReleaseInfo release)
    {
        if (ShowPrompt is null)
            return;
        var choice = await ShowPrompt(new UpdatePrompt(release, Checker.CurrentVersionText));
        Apply(choice, release);
    }

    /// <summary>Acts on the popup's answer.</summary>
    public void Apply(UpdateChoice choice, ReleaseInfo release)
    {
        switch (choice)
        {
            case UpdateChoice.Download:
                OpenUrl(UpdateChecker.DownloadUrl(release, UpdateChecker.AssetSuffixForThisOs()));
                break;
            case UpdateChoice.Skip:
                _settings.SkippedUpdateVersion = release.Tag;
                _settings.Save();
                break;
        }
    }

    private static string VersionText(ReleaseInfo release) =>
        release.Version is { } v ? UpdateChecker.FormatVersion(v) : release.Tag;

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
        }
    }
}

/// <summary>What the new version popup shows.</summary>
public sealed class UpdatePrompt
{
    public UpdatePrompt(ReleaseInfo release, string currentVersion)
    {
        Release = release;
        NewVersion = release.Version is { } v ? UpdateChecker.FormatVersion(v) : release.Tag;
        CurrentVersion = currentVersion;
        Notes = UpdateChecker.SummarizeNotes(release.Notes);
    }

    public ReleaseInfo Release { get; }

    public string NewVersion { get; }

    public string CurrentVersion { get; }

    public string Heading => $"Version {NewVersion} is available";

    public string VersionLine => $"You have version {CurrentVersion}.";

    public string Title => Release.Title;

    /// <summary>The start of the release notes, as plain text.</summary>
    public string Notes { get; }

    public bool HasNotes => Notes.Length > 0;
}
