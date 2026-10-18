using System.IO.Compression;
using System.Net;
using System.Text;
using MangaDexLite.Core;

int passed = 0;
async Task Test(string name, Func<Task> body)
{
    await body();
    Console.WriteLine($"PASS {name}");
    passed++;
}
void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
const string mangaId = "11111111-1111-1111-1111-111111111111";
const string chapterId = "22222222-2222-2222-2222-222222222222";
var manga = new Manga(mangaId, "Test / title", "Description", null);
var chapter = new Chapter(chapterId, "1.5", "1", "First", "ru", 2,
    [new Group("g1", "Same"), new Group("g2", "Same")], null);

await Test("Only MangaDex title URLs and UUIDs are accepted", () =>
{
    Check(MangaDexClient.TryTitleId($"https://mangadex.org/title/{mangaId}/name") == mangaId);
    Check(MangaDexClient.TryTitleId(mangaId) == mangaId);
    Check(MangaDexClient.TryTitleId($"https://evil.example/title/{mangaId}") is null);
    Check(MangaDexClient.TryTitleId($"https://mangadex.org/chapter/{mangaId}") is null);
    return Task.CompletedTask;
});
await Test("Decimal ranges and group UUIDs", () =>
{
    Check(chapter.Matches("ru", "g2") && !chapter.Matches("en", "g2"));
    Check(!chapter.Matches("ru", "Same"));
    Check(chapter.InRange(1.1m, 2m) && !chapter.InRange(2m, 3m));
    Check(!(chapter with { Number = null }).InRange(1m, 3m));
    Check(!(chapter with { Pages = 0, ExternalUrl = "https://example.com" }).CanDownload);
    return Task.CompletedTask;
});
await Test("Windows-safe names including reserved devices", () =>
{
    foreach (var value in new[] { "../CON", "CON", "NUL.txt", "  ", "a:b?c*", "COM1", new string('x', 500) })
    {
        var safe = OutputNames.Safe(value);
        Check(safe.Length is > 0 and <= 64 && !safe.Any(c => "<>:\"/\\|?*".Contains(c)));
        Check(safe != "." && safe != ".." && safe != "CON" && safe != "COM1" && safe != "NUL.txt");
    }
    return Task.CompletedTask;
});
await Test("Chapter feed paginates and keeps joint groups", async () =>
{
    int calls = 0;
    using var http = new HttpClient(new Fixture(request =>
    {
        calls++;
        Check(request.RequestUri!.Query.Contains("includes%5B%5D=scanlation_group") || request.RequestUri.Query.Contains("includes[]=scanlation_group"));
        bool second = request.RequestUri.Query.Contains("offset=1");
        return Json($$$"""{"result":"ok","total":2,"data":[{"id":"{{{(second ? mangaId : chapterId)}}}","attributes":{"chapter":"1.5","volume":"1","title":"First","translatedLanguage":"ru","pages":2},"relationships":[{"id":"g1","type":"scanlation_group","attributes":{"name":"Same"}},{"id":"g2","type":"scanlation_group","attributes":{"name":"Same"}}]}]}""");
    }));
    using var api = new MangaDexClient(http);
    var chapters = await api.GetChaptersAsync(mangaId, "ru", CancellationToken.None);
    Check(calls == 2 && chapters.Count == 2 && chapters[0].Groups.Count == 2);
});
await Test("Transient API failure retries", async () =>
{
    int calls = 0;
    using var http = new HttpClient(new Fixture(_ => ++calls == 1
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        : Json("{\"result\":\"ok\",\"data\":[],\"total\":0}")));
    using var api = new MangaDexClient(http);
    Check((await api.SearchAsync("test", CancellationToken.None)).Count == 0 && calls == 2);
});
await Test("Download resumes, creates CBZ and never replaces finished output", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), "MangaDexLite-tests-" + Guid.NewGuid());
    int imageCalls = 0;
    using var http = new HttpClient(new Fixture(request =>
    {
        if (request.RequestUri!.AbsolutePath.Contains("at-home"))
            return Json("{\"result\":\"ok\",\"baseUrl\":\"https://cdn.example\",\"chapter\":{\"hash\":\"hash\",\"data\":[\"a.jpg\",\"b.png\"],\"dataSaver\":[\"a.jpg\",\"b.jpg\"]}}");
        imageCalls++;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) };
        response.Content.Headers.ContentType = new("image/jpeg");
        return response;
    }));
    using var api = new MangaDexClient(http);
    var downloader = new ChapterDownloader(api);
    try
    {
        var options = new DownloadOptions(root, false, false);
        var folder = await downloader.DownloadAsync(manga, chapter, options, null, CancellationToken.None);
        Check(Directory.GetFiles(folder, "*.jpg").Length == 1 && File.Exists(Path.Combine(folder, "002.png")));
        Check(imageCalls == 2);
        // Missing a completed page invalidates the manifest; the other page is reused.
        File.Delete(Path.Combine(folder, "002.png"));
        await downloader.DownloadAsync(manga, chapter, options, null, CancellationToken.None);
        Check(imageCalls == 3);
        string cbz = await downloader.DownloadAsync(manga, chapter, options with { Cbz = true }, null, CancellationToken.None);
        using (var zip = ZipFile.OpenRead(cbz)) Check(zip.Entries.Count == 2);
        Check(imageCalls == 3);
        await downloader.DownloadAsync(manga, chapter, options with { Cbz = true }, null, CancellationToken.None);
        Check(imageCalls == 3);
        Check(!Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any());
        using (var zip = new ZipArchive(new FileStream(cbz, FileMode.Create), ZipArchiveMode.Create))
            for (int i = 1; i <= 1000; i++)
            {
                using var entry = zip.CreateEntry($"{i:000}.jpg").Open();
                entry.WriteByte(1);
            }
        await downloader.DownloadAsync(manga, chapter with { Pages = 1000 }, options with { Cbz = true }, null, CancellationToken.None);
        Check(imageCalls == 3, "Four-digit page numbers must still count as a completed archive");
    }
    finally { Directory.Delete(root, true); }
});
await Test("Already canceled request performs no HTTP work", async () =>
{
    using var http = new HttpClient(new Fixture(_ => Json("{}")));
    using var api = new MangaDexClient(http);
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try
    {
        await api.SearchAsync("test", cancel.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { }
});
await Test("Mid-chapter cancellation keeps pages but no completion marker", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), "MangaDexLite-cancel-" + Guid.NewGuid());
    using var cancel = new CancellationTokenSource();
    int calls = 0;
    using var http = new HttpClient(new Fixture(request =>
    {
        if (request.RequestUri!.AbsolutePath.Contains("at-home"))
            return Json("{\"result\":\"ok\",\"baseUrl\":\"https://cdn.example\",\"chapter\":{\"hash\":\"h\",\"data\":[\"a.jpg\",\"b.jpg\"]}}");
        if (++calls == 2) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) { Headers = { ContentType = new("image/jpeg") } } };
    }));
    using var api = new MangaDexClient(http);
    try
    {
        var downloader = new ChapterDownloader(api);
        var options = new DownloadOptions(root, false, false);
        bool canceled = false;
        try { await downloader.DownloadAsync(manga, chapter, options, null, cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories).Length == 1);
        Check(Directory.GetFiles(root, ".complete.json", SearchOption.AllDirectories).Length == 0);
        Check(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Length == 0);
        await downloader.DownloadAsync(manga, chapter, options, null, CancellationToken.None);
        Check(calls == 3 && Directory.GetFiles(root, "*.jpg", SearchOption.AllDirectories).Length == 2);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});
await Test("Incomplete existing CBZ is preserved and reported", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), "MangaDexLite-incomplete-" + Guid.NewGuid());
    using var http = new HttpClient(new Fixture(request => request.RequestUri!.AbsolutePath.Contains("at-home")
        ? Json("{\"result\":\"ok\",\"baseUrl\":\"https://cdn.example\",\"chapter\":{\"hash\":\"h\",\"data\":[\"a.jpg\",\"b.jpg\"]}}")
        : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) { Headers = { ContentType = new("image/jpeg") } } }));
    using var api = new MangaDexClient(http);
    try
    {
        var downloader = new ChapterDownloader(api);
        var options = new DownloadOptions(root, true, false);
        string cbz = await downloader.DownloadAsync(manga, chapter, options, null, CancellationToken.None);
        using (var zip = ZipFile.Open(cbz, ZipArchiveMode.Update)) zip.Entries[1].Delete();
        byte[] before = await File.ReadAllBytesAsync(cbz);
        bool rejected = false;
        try { await downloader.DownloadAsync(manga, chapter, options, null, CancellationToken.None); }
        catch (IOException) { rejected = true; }
        Check(rejected, "Incomplete CBZ must not count as completed");
        byte[] after = await File.ReadAllBytesAsync(cbz);
        Check(before.SequenceEqual(after));
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
});
await Test("HTTP timeout is retried, never reported as user cancellation", async () =>
{
    int calls = 0;
    using var http = new HttpClient(new Fixture(_ => ++calls == 1
        ? throw new TaskCanceledException("Simulated HTTP timeout")
        : Json("{\"result\":\"ok\",\"data\":[],\"total\":0}")));
    using var api = new MangaDexClient(http);
    Check((await api.SearchAsync("test", CancellationToken.None)).Count == 0 && calls == 2);
});
await Test("Damaged settings are preserved; optional values recover", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), "MangaDexLite-settings-" + Guid.NewGuid());
    Directory.CreateDirectory(root);
    string path = Path.Combine(root, "settings.json");
    try
    {
        await File.WriteAllTextAsync(path, "broken JSON");
        var store = new SettingsStore(path);
        var loaded = await store.LoadAsync();
        Check(loaded.Warning is not null);
        await store.SaveAsync(new AppSettings("C:/Manga", true, false, "ru"));
        Check(await File.ReadAllTextAsync(path) == "broken JSON");
        await File.WriteAllTextAsync(path, "{\"Directory\":123,\"Cbz\":true,\"Language\":\"en\",\"Unknown\":1}");
        store = new SettingsStore(path);
        loaded = await store.LoadAsync();
        Check(loaded.Settings.Cbz && loaded.Settings.Language == "en" && loaded.Warning is not null);
    }
    finally { Directory.Delete(root, true); }
});
Console.WriteLine($"{passed} tests passed.");
await StudioTests.RunAsync();

if (args.Contains("--live"))
{
    using var live = new MangaDexClient();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    using var discovery = new HttpClient();
    discovery.DefaultRequestHeaders.UserAgent.ParseAdd("MangaDexLite/0.1 (+https://github.com/maqsatto/MangaDexLite)");
    using var discoveryResponse = await discovery.GetAsync("https://api.mangadex.org/chapter?limit=1&translatedLanguage[]=ru&order[readableAt]=desc&includeFutureUpdates=0", timeout.Token);
    string discoveryJson = await discoveryResponse.Content.ReadAsStringAsync(timeout.Token);
    Check(discoveryResponse.IsSuccessStatusCode, discoveryJson);
    using var latest = System.Text.Json.JsonDocument.Parse(discoveryJson);
    string latestManga = latest.RootElement.GetProperty("data")[0].GetProperty("relationships").EnumerateArray().First(r => r.GetProperty("type").GetString() == "manga").GetProperty("id").GetString()!;
    var search = await live.SearchAsync("One Piece", timeout.Token);
    Check(search.Count > 0);
    var found = await live.SearchAsync(latestManga, timeout.Token);
    Check(found.Count > 0 && found[0].Title.Length > 0);
    var feed = await live.GetChaptersAsync(found[0].Id, "ru", timeout.Token);
    Check(feed.Count > 0 && feed.All(c => c.Language == "ru"));
    Console.WriteLine($"LIVE search: {found.Count} titles; feed: {feed.Count} Russian chapters, {feed.SelectMany(c => c.Groups).DistinctBy(g => g.Id).Count()} groups.");
    var available = feed.FirstOrDefault(c => c.CanDownload);
    if (available is not null)
    {
        var images = await live.GetImagesAsync(available.Id, false, timeout.Token);
        Check(images.Files.Count > 0);
        Console.WriteLine($"LIVE at-home: {images.Files.Count} page URLs; no images downloaded.");
    }
}

static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
{ Content = new StringContent(json, Encoding.UTF8, "application/json") };

sealed class Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
