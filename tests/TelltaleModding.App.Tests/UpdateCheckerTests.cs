using System.Net;
using System.Text;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using TelltaleModding.App.Services;
using TelltaleModding.App.ViewModels;
using TelltaleModding.App.Views;
using Xunit;

namespace TelltaleModding.App.Tests;

public class UpdateCheckerTests
{
    // A trimmed releases/latest reply in GitHub's shape.
    private static string ReleaseJson(string tag = "v1.3.0", bool draft = false, bool prerelease = false) => $$"""
        {
          "url": "https://api.github.com/repos/infernokun/TelltaleModdingStudio/releases/1",
          "html_url": "https://github.com/infernokun/TelltaleModdingStudio/releases/tag/{{tag}}",
          "tag_name": "{{tag}}",
          "name": "Faster scenes",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "## What's new\r\n\r\n- **Scenes** open twice as fast\r\n- Fixed a crash in Dialogue",
          "assets": [
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt" },
            { "name": "TelltaleModdingStudio-1.3.0-win-x64.zip", "browser_download_url": "https://example.test/win.zip" },
            { "name": "TelltaleModdingStudio-1.3.0-linux-x64.tar.gz", "browser_download_url": "https://example.test/linux.tar.gz" }
          ]
        }
        """;

    /// <summary>Answers every request from a function and records what was asked.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public FakeHandler(HttpStatusCode status, string body = "")
            : this((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }))
        {
        }

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _respond(request, cancellationToken);
        }
    }

    private static UpdateChecker Checker(HttpMessageHandler handler, string current = "1.2.0", TimeSpan? timeout = null) =>
        new(new HttpClient(handler), current, timeout);

    // ---- Versions -------------------------------------------------------------------

    [Theory]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("V1.2.0", 1, 2, 0)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("v1.3.0-beta.1", 1, 3, 0)]
    [InlineData("1.2.0+4f2c9e1", 1, 2, 0)]
    [InlineData("1.2.0-rc.2+4f2c9e1", 1, 2, 0)]
    [InlineData(" v10.0.12 ", 10, 0, 12)]
    public void Versions_are_read_from_tags(string text, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build, 0), UpdateChecker.TryParseVersion(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("v1..2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("-1.2")]
    [InlineData("release-2026")]
    public void Garbage_tags_are_not_versions(string? text)
    {
        Assert.Null(UpdateChecker.TryParseVersion(text));
    }

    [Fact]
    public void Build_metadata_is_dropped_from_the_current_version()
    {
        var checker = Checker(new FakeHandler(HttpStatusCode.OK), "1.2.0+4f2c9e1a7b");
        Assert.Equal("1.2.0", checker.CurrentVersionText);
        Assert.Equal(new Version(1, 2, 0, 0), checker.CurrentVersion);
    }

    [Theory]
    [InlineData("v1.3.0", true)]
    [InlineData("v1.2.1", true)]
    [InlineData("v2.0", true)]
    [InlineData("v1.2.0", false)]
    [InlineData("1.2", false)]
    [InlineData("v1.1.9", false)]
    [InlineData("v1.2.0-beta", false)]
    [InlineData("nightly", false)]
    public void Only_a_higher_version_counts_as_newer(string tag, bool newer)
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson(tag))!;
        Assert.Equal(newer, Checker(new FakeHandler(HttpStatusCode.OK)).IsNewer(release));
    }

    [Fact]
    public void An_unreadable_current_version_never_offers_an_update()
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson("v9.0.0"))!;
        Assert.False(Checker(new FakeHandler(HttpStatusCode.OK), "dev").IsNewer(release));
    }

    // ---- Release JSON -----------------------------------------------------------------

    [Fact]
    public void A_release_is_read_from_GitHubs_json()
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson());

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 3, 0, 0), release!.Version);
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Equal("Faster scenes", release.Title);
        Assert.Contains("twice as fast", release.Notes);
        Assert.Equal("https://github.com/infernokun/TelltaleModdingStudio/releases/tag/v1.3.0", release.PageUrl);
        Assert.False(release.IsDraft);
        Assert.False(release.IsPrerelease);
        Assert.Equal(3, release.Assets.Count);
        Assert.Equal("TelltaleModdingStudio-1.3.0-win-x64.zip", release.Assets[1].Name);
        Assert.Equal("https://example.test/win.zip", release.Assets[1].DownloadUrl);
    }

    [Fact]
    public void A_release_without_a_title_uses_its_tag()
    {
        var release = UpdateChecker.ParseRelease("""{ "tag_name": "v1.4.0", "name": "", "assets": [] }""");
        Assert.Equal("v1.4.0", release!.Title);
        Assert.Equal(UpdateChecker.ReleasesPage, release.PageUrl);
        Assert.Empty(release.Assets);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"tag_name\": ")]
    [InlineData("")]
    public void Unusable_json_gives_no_release(string json)
    {
        Assert.Null(UpdateChecker.ParseRelease(json));
    }

    [Fact]
    public void Notes_are_shortened_to_plain_text()
    {
        var notes = UpdateChecker.SummarizeNotes(UpdateChecker.ParseRelease(ReleaseJson())!.Notes);
        Assert.Equal("What's new\n\n- Scenes open twice as fast\n- Fixed a crash in Dialogue", notes);

        var longNotes = UpdateChecker.SummarizeNotes(string.Join(" ", Enumerable.Repeat("word", 400)), 100);
        Assert.True(longNotes.Length <= 101);
        Assert.EndsWith("word…", longNotes);
    }

    // ---- Downloads ----------------------------------------------------------------------

    [Fact]
    public void Download_links_to_this_OS_file_or_the_release_page()
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson())!;

        Assert.Equal("https://example.test/win.zip", UpdateChecker.DownloadUrl(release, "-win-x64.zip"));
        Assert.Equal("https://example.test/linux.tar.gz", UpdateChecker.DownloadUrl(release, "-linux-x64.tar.gz"));
        Assert.Equal(release.PageUrl, UpdateChecker.DownloadUrl(release, "-osx-arm64.zip"));
        Assert.Equal(release.PageUrl, UpdateChecker.DownloadUrl(release, null));
        Assert.Null(UpdateChecker.PickAsset(release.Assets, "-osx-arm64.zip"));
    }

    [Fact]
    public void This_OS_has_a_matching_build_name()
    {
        var suffix = UpdateChecker.AssetSuffixForThisOs();
        if (OperatingSystem.IsWindows())
            Assert.Equal("-win-x64.zip", suffix);
        else if (OperatingSystem.IsLinux())
            Assert.Equal("-linux-x64.tar.gz", suffix);
        else
            Assert.Null(suffix);
    }

    // ---- Checking ------------------------------------------------------------------------

    [Fact]
    public async Task A_newer_release_is_reported()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson());
        var result = await Checker(handler).CheckAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("v1.3.0", result.Release!.Tag);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(UpdateChecker.LatestReleaseUrl, request.RequestUri!.ToString());
        Assert.Contains("TelltaleModdingStudio", request.Headers.UserAgent.ToString());
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
    }

    [Theory]
    [InlineData("v1.2.0", false, false)]
    [InlineData("v1.0.0", false, false)]
    [InlineData("v1.3.0", true, false)]
    [InlineData("v1.3.0", false, true)]
    [InlineData("not-a-version", false, false)]
    public async Task Same_older_draft_prerelease_and_odd_tags_are_up_to_date(string tag, bool draft, bool prerelease)
    {
        var result = await Checker(new FakeHandler(HttpStatusCode.OK, ReleaseJson(tag, draft, prerelease))).CheckAsync();
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limiting")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting")]
    [InlineData(HttpStatusCode.NotFound, "No published release")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    public async Task Http_errors_give_a_readable_failure(HttpStatusCode status, string expected)
    {
        var result = await Checker(new FakeHandler(status, """{ "message": "nope" }""")).CheckAsync();
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public async Task Invalid_json_gives_a_failure()
    {
        var result = await Checker(new FakeHandler(HttpStatusCode.OK, "<html>oops</html>")).CheckAsync();
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("couldn't be read", result.Error);
    }

    [Fact]
    public async Task No_connection_gives_a_failure()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("No such host is known."));
        var result = await Checker(handler).CheckAsync();
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("internet connection", result.Error);
    }

    [Fact]
    public async Task A_slow_reply_times_out_as_a_failure()
    {
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var result = await Checker(handler, timeout: TimeSpan.FromMilliseconds(50)).CheckAsync();
        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("too long", result.Error);
    }

    // ---- The view-model: start-up, button and skipping ----------------------------------------

    private static string TempSettings() => Path.Combine(Path.GetTempPath(), $"tms-updates-{Guid.NewGuid():N}.json");

    private sealed class Harness
    {
        public Harness(HttpMessageHandler handler, AppSettings? settings = null)
        {
            Settings = settings ?? new AppSettings { FilePath = TempSettings() };
            Updates = new UpdatesViewModel(Settings, Checker(handler))
            {
                ShowPrompt = prompt =>
                {
                    Prompts.Add(prompt);
                    return Task.FromResult(Answer);
                },
                OpenUrl = Opened.Add,
            };
            Updates.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UpdatesViewModel.CheckStatus))
                    Status.Add(Updates.CheckStatus);
            };
        }

        public AppSettings Settings { get; }
        public UpdatesViewModel Updates { get; }
        public UpdateChoice Answer { get; set; } = UpdateChoice.Later;
        public List<UpdatePrompt> Prompts { get; } = new();
        public List<string> Opened { get; } = new();
        public List<string> Status { get; } = new();
    }

    [Fact]
    public async Task The_start_up_check_offers_a_new_version()
    {
        var h = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson()));
        await h.Updates.CheckOnStartAsync();

        var prompt = Assert.Single(h.Prompts);
        Assert.Equal("1.3.0", prompt.NewVersion);
        Assert.Equal("1.2.0", prompt.CurrentVersion);
        Assert.Equal("Faster scenes", prompt.Title);
        Assert.StartsWith("What's new", prompt.Notes);
        Assert.Equal("Version 1.3.0 is available", h.Updates.CheckStatus);
    }

    [Fact]
    public async Task The_start_up_check_is_silent_when_it_fails_and_notes_when_up_to_date()
    {
        foreach (var handler in new HttpMessageHandler[]
                 {
                     new FakeHandler(HttpStatusCode.Forbidden),
                     new FakeHandler(HttpStatusCode.OK, "garbage"),
                 })
        {
            var h = new Harness(handler);
            await h.Updates.CheckOnStartAsync();
            Assert.Empty(h.Prompts);
            Assert.Empty(h.Status);
            Assert.False(h.Updates.HasCheckStatus);
        }

        var upToDate = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson("v1.2.0")));
        await upToDate.Updates.CheckOnStartAsync();
        Assert.Empty(upToDate.Prompts);
        Assert.Equal("Up to date", upToDate.Updates.CheckStatus);
        Assert.Equal("Version 1.2.0", upToDate.Updates.VersionLabel);
    }

    [Fact]
    public async Task The_start_up_check_follows_the_setting()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, ReleaseJson());
        var h = new Harness(handler);
        h.Settings.Preferences.CheckForUpdatesOnStart = false;
        await h.Updates.CheckOnStartAsync();

        Assert.Empty(handler.Requests);
        Assert.Empty(h.Prompts);
        Assert.True(new UserPreferences().CheckForUpdatesOnStart);
    }

    [Fact]
    public async Task A_skipped_version_is_remembered_and_only_the_button_shows_it()
    {
        var file = TempSettings();
        try
        {
            var h = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson()), new AppSettings { FilePath = file })
            {
                Answer = UpdateChoice.Skip,
            };
            await h.Updates.CheckOnStartAsync();
            Assert.Single(h.Prompts);
            Assert.Equal("v1.3.0", AppSettings.Load(file).SkippedUpdateVersion);

            // Next start, the skipped version stays quiet...
            var again = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson()), AppSettings.Load(file));
            await again.Updates.CheckOnStartAsync();
            Assert.Empty(again.Prompts);
            Assert.Empty(again.Status);

            // ...but the button still offers it, and a later version is offered on start.
            await again.Updates.CheckNowCommand.ExecuteAsync(null);
            Assert.Single(again.Prompts);
            var newer = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson("v1.4.0")), AppSettings.Load(file));
            await newer.Updates.CheckOnStartAsync();
            Assert.Single(newer.Prompts);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Download_opens_the_build_for_this_OS_and_Later_does_nothing()
    {
        var h = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson())) { Answer = UpdateChoice.Download };
        await h.Updates.CheckNowCommand.ExecuteAsync(null);
        var url = Assert.Single(h.Opened);
        var expected = OperatingSystem.IsWindows() ? "https://example.test/win.zip"
            : OperatingSystem.IsLinux() ? "https://example.test/linux.tar.gz"
            : "https://github.com/infernokun/TelltaleModdingStudio/releases/tag/v1.3.0";
        Assert.Equal(expected, url);

        h.Answer = UpdateChoice.Later;
        await h.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Single(h.Opened);
        Assert.Null(h.Settings.SkippedUpdateVersion);
    }

    [Fact]
    public async Task The_button_always_reports_what_it_found()
    {
        var upToDate = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson("v1.2.0")));
        await upToDate.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Checking for updates…", "Up to date" }, upToDate.Status);
        Assert.Empty(upToDate.Prompts);

        var failed = new Harness(new FakeHandler(HttpStatusCode.InternalServerError));
        await failed.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal("Couldn't check for updates", failed.Status[^1]);
        Assert.False(string.IsNullOrEmpty(failed.Updates.CheckStatusDetail));
        Assert.False(failed.Updates.IsChecking);

        var available = new Harness(new FakeHandler(HttpStatusCode.OK, ReleaseJson()));
        await available.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.Equal("Version 1.3.0 is available", available.Status[^1]);
        Assert.Null(available.Updates.CheckStatusDetail);
        Assert.Single(available.Prompts);
    }

    [AvaloniaFact]
    public void The_popup_renders_the_release()
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson())!;
        var window = new UpdateWindow(new UpdatePrompt(release, "1.2.0"));
        window.Show();
        Assert.Equal("Version 1.3.0 is available", ((UpdatePrompt)window.DataContext!).Heading);
        Directory.CreateDirectory(UiTests.ShotsFolder);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(UiTests.ShotsFolder, "update-popup.png"));
        window.Close();
    }
}
