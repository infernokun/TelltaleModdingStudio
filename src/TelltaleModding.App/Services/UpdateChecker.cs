using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TelltaleModding.App.Services;

/// <summary>A published release on GitHub.</summary>
public sealed record ReleaseInfo(
    Version? Version, string Tag, string Title, string Notes, string PageUrl,
    IReadOnlyList<ReleaseAsset> Assets, bool IsDraft, bool IsPrerelease);

/// <summary>A file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl);

public enum UpdateStatus { UpToDate, UpdateAvailable, Failed }

/// <summary>What a check found: the newer release, or why it could not tell.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, ReleaseInfo? Release = null, string? Error = null)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateStatus.UpToDate);
    public static UpdateCheckResult Failed(string error) => new(UpdateStatus.Failed, Error: error);
}

/// <summary>
/// Asks GitHub for the latest release of the app and compares it with the running version.
/// Every failure (offline, rate limit, unexpected reply) comes back as a
/// <see cref="UpdateStatus.Failed"/> result rather than an exception.
/// </summary>
public sealed class UpdateChecker
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/infernokun/TelltaleModdingStudio/releases/latest";

    /// <summary>The releases page, for a reply that leaves out its own link.</summary>
    public const string ReleasesPage = "https://github.com/infernokun/TelltaleModdingStudio/releases";

    private static readonly Lazy<HttpClient> SharedClient = new(() => new HttpClient());

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <param name="http">The client to send requests with; tests pass one over a fake handler.</param>
    /// <param name="currentVersion">The running version; defaults to the app's own.</param>
    /// <param name="timeout">How long to wait for GitHub before giving up.</param>
    public UpdateChecker(HttpClient? http = null, string? currentVersion = null, TimeSpan? timeout = null)
    {
        _http = http ?? SharedClient.Value;
        CurrentVersionText = CleanVersion(currentVersion ?? AppVersion());
        CurrentVersion = TryParseVersion(CurrentVersionText);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>The running version as shown to the user, e.g. "1.2.0".</summary>
    public string CurrentVersionText { get; }

    /// <summary>The running version, or null when it could not be read.</summary>
    public Version? CurrentVersion { get; }

    /// <summary>Fetches the latest release and reports whether it is newer than this build.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd($"TelltaleModdingStudio/{CurrentVersionText}");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed(DescribeStatus(response));
            json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("GitHub took too long to answer.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return UpdateCheckResult.Failed("Couldn't reach GitHub. Check your internet connection.");
        }

        var release = ParseRelease(json);
        if (release is null)
            return UpdateCheckResult.Failed("GitHub's reply couldn't be read.");
        return IsNewer(release) ? new UpdateCheckResult(UpdateStatus.UpdateAvailable, release) : UpdateCheckResult.UpToDate;
    }

    /// <summary>True for a published, stable release with a higher version than this build.</summary>
    public bool IsNewer(ReleaseInfo release) =>
        !release.IsDraft && !release.IsPrerelease &&
        release.Version is { } latest && CurrentVersion is { } current && latest > current;

    private static string DescribeStatus(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
            "GitHub is limiting requests right now. Try again in a while.",
        HttpStatusCode.NotFound => "No published release was found on GitHub.",
        _ => $"GitHub answered with an error ({(int)response.StatusCode}).",
    };

    // ---- Versions -----------------------------------------------------------------

    /// <summary>The app's informational version (what CI sets from the tag).</summary>
    private static string AppVersion()
    {
        var assembly = typeof(UpdateChecker).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString() ?? "";
    }

    /// <summary>Drops build metadata ("1.2.0+abc123" becomes "1.2.0") and surrounding space.</summary>
    public static string CleanVersion(string version)
    {
        var plus = version.IndexOf('+');
        return (plus >= 0 ? version[..plus] : version).Trim();
    }

    /// <summary>
    /// Reads a tag or version such as "v1.2.0", "1.3" or "1.3.0-beta.1" (the prerelease part
    /// is ignored). Missing parts count as zero, so "1.2" equals "1.2.0". Null when it is not a version.
    /// </summary>
    public static Version? TryParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = CleanVersion(text);
        if (core.StartsWith('v') || core.StartsWith('V'))
            core = core[1..];
        var dash = core.IndexOf('-');
        if (dash >= 0)
            core = core[..dash];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4)
            return null;
        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out numbers[i]))
                return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>The version as people write it: "1.2.0", with a fourth part only when it is set.</summary>
    public static string FormatVersion(Version version) =>
        version.Revision > 0 ? version.ToString(4) : version.ToString(3);

    // ---- Release JSON ---------------------------------------------------------------

    /// <summary>
    /// Reads a GitHub "release" object; null when the JSON is unusable. A tag that is not a
    /// version gives a release with a null <see cref="ReleaseInfo.Version"/>.
    /// </summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var tag = ReadString(root, "tag_name") ?? "";
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.Object &&
                        ReadString(asset, "name") is { Length: > 0 } name &&
                        ReadString(asset, "browser_download_url") is { Length: > 0 } url)
                        assets.Add(new ReleaseAsset(name, url));
                }
            }
            var title = ReadString(root, "name");
            return new ReleaseInfo(
                TryParseVersion(tag), tag,
                string.IsNullOrWhiteSpace(title) ? tag : title.Trim(),
                ReadString(root, "body") ?? "",
                ReadString(root, "html_url") ?? ReleasesPage,
                assets,
                ReadBool(root, "draft"), ReadBool(root, "prerelease"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // ---- Downloads and notes ----------------------------------------------------------

    /// <summary>The end of the release file built for this OS, or null where there is none.</summary>
    public static string? AssetSuffixForThisOs() =>
        OperatingSystem.IsWindows() ? "-win-x64.zip"
        : OperatingSystem.IsLinux() ? "-linux-x64.tar.gz"
        : null;

    /// <summary>The release file whose name ends with <paramref name="suffix"/>, if any.</summary>
    public static ReleaseAsset? PickAsset(IEnumerable<ReleaseAsset> assets, string? suffix) =>
        suffix is null ? null : assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where Download should go: this OS's file when the release has one, else the release page.</summary>
    public static string DownloadUrl(ReleaseInfo release, string? suffix) =>
        PickAsset(release.Assets, suffix)?.DownloadUrl ?? release.PageUrl;

    /// <summary>
    /// The start of the release notes as plain text: heading marks and bold markers removed,
    /// blank runs collapsed, and cut at a word near <paramref name="maxLength"/>.
    /// </summary>
    public static string SummarizeNotes(string notes, int maxLength = 600)
    {
        var text = notes.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        text = text.Replace("**", "").Replace("__", "");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        if (text.Length <= maxLength)
            return text;
        var cut = text.LastIndexOfAny(new[] { ' ', '\n' }, maxLength);
        if (cut < maxLength / 2)
            cut = maxLength;
        return new StringBuilder(text[..cut].TrimEnd()).Append('…').ToString();
    }
}
