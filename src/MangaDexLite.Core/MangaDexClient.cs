using System.Net;
using System.Text.Json;

namespace MangaDexLite.Core;

/// <summary>One paced API connection. Image requests never carry authentication headers.</summary>
public sealed class MangaDexClient : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly SemaphoreSlim apiGate = new(1, 1);
    private DateTimeOffset nextRequest;
    private const string Api = "https://api.mangadex.org";

    public MangaDexClient(HttpClient? http = null)
    {
        ownsHttp = http is null;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    public static string? TryTitleId(string input)
    {
        input = input.Trim();
        if (Guid.TryParseExact(input, "D", out var id)) return id.ToString();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host is not ("mangadex.org" or "www.mangadex.org")) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "title" && Guid.TryParseExact(parts[1], "D", out id) ? id.ToString() : null;
    }

    public async Task<IReadOnlyList<Manga>> SearchAsync(string text, CancellationToken ct, int offset = 0)
    {
        var id = TryTitleId(text);
        if (id is not null)
        {
            using var single = await GetJsonAsync($"{Api}/manga/{id}?includes[]=cover_art", ct);
            return [ReadManga(single.RootElement.GetProperty("data"))];
        }
        if (text.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("Вставьте ссылку MangaDex вида https://mangadex.org/title/…");
        using var json = await GetJsonAsync($"{Api}/manga?limit=30&offset={offset}&title={Uri.EscapeDataString(text.Trim())}&includes[]=cover_art&order[relevance]=desc&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica", ct);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(ReadManga).ToList();
    }

    public async Task<IReadOnlyList<Chapter>> GetChaptersAsync(string mangaId, string language, CancellationToken ct)
    {
        var result = new List<Chapter>();
        int offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{Api}/manga/{Uri.EscapeDataString(mangaId)}/feed?limit=500&offset={offset}&includes[]=scanlation_group&order[volume]=asc&order[chapter]=asc&includeFutureUpdates=0";
            if (!string.IsNullOrEmpty(language)) url += "&translatedLanguage[]=" + Uri.EscapeDataString(language);
            using var json = await GetJsonAsync(url, ct);
            var root = json.RootElement;
            var data = root.GetProperty("data");
            foreach (var item in data.EnumerateArray()) result.Add(ReadChapter(item));
            offset += data.GetArrayLength();
            if (offset >= root.GetProperty("total").GetInt32()) break;
            if (data.GetArrayLength() == 0) throw new InvalidDataException("MangaDex вернул неполный список глав. Повторите загрузку.");
            if (offset >= 10000) throw new InvalidOperationException("Превышен лимит MangaDex в 10 000 глав. Выберите отдельный язык.");
        }
        return result.DistinctBy(c => c.Id).ToList();
    }

    public async Task<ChapterImages> GetImagesAsync(string chapterId, bool dataSaver, CancellationToken ct)
    {
        using var json = await GetJsonAsync($"{Api}/at-home/server/{Uri.EscapeDataString(chapterId)}", ct);
        var root = json.RootElement;
        var chapter = root.GetProperty("chapter");
        string baseUrl = root.GetProperty("baseUrl").GetString()!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var server) || server.Scheme != "https")
            throw new InvalidDataException("MangaDex вернул некорректный адрес сервера.");
        var files = chapter.GetProperty(dataSaver ? "dataSaver" : "data").EnumerateArray().Select(e => e.GetString()!).ToList();
        if (files.Count == 0) throw new InvalidOperationException("Страницы главы недоступны на MangaDex.");
        return new(baseUrl.TrimEnd('/') + (dataSaver ? "/data-saver/" : "/data/"), chapter.GetProperty("hash").GetString()!, files);
    }

    internal async Task DownloadImageAsync(string url, string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await SendAsync(url, false, timeout.Token);
            if (response.Content.Headers.ContentType?.MediaType is not string media || !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Сервер вернул не изображение. Повторите скачивание позже.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, timeout.Token);
            if (output.Length == 0 || (response.Content.Headers.ContentLength is long expected && expected != output.Length))
                throw new InvalidDataException("Сервер вернул пустую или неполную страницу. Повторите скачивание.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Превышено время ожидания страницы. Повторите скачивание."); }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await SendAsync(url, true, timeout.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Превышено время ожидания MangaDex. Повторите запрос."); }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, bool api, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (api) await apiGate.WaitAsync(ct);
            HttpResponseMessage response;
            try
            {
                if (api && nextRequest > DateTimeOffset.UtcNow) await Task.Delay(nextRequest - DateTimeOffset.UtcNow, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("MangaDexLite/0.1 (+https://github.com/maqsatto/MangaDexLite)");
                try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
                catch (HttpRequestException) when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
                    continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct);
                    continue;
                }
                finally { if (api) nextRequest = DateTimeOffset.UtcNow.AddMilliseconds(300); }
            }
            finally { if (api) apiGate.Release(); }
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 3)
            {
                var wait = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(attempt + 1);
                response.Dispose();
                if (wait > TimeSpan.FromMinutes(2)) throw new HttpRequestException("MangaDex просит подождать более двух минут. Повторите позже.");
                await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1), ct);
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var status = response.StatusCode;
            response.Dispose();
            string message = status switch
            {
                HttpStatusCode.NotFound => "Тайтл или глава не найдены на MangaDex.",
                HttpStatusCode.Forbidden => "MangaDex ограничил доступ к этому содержимому.",
                HttpStatusCode.TooManyRequests => "Лимит запросов MangaDex. Подождите и повторите.",
                _ => $"Ошибка MangaDex: HTTP {(int)status}. Повторите позже."
            };
            throw new HttpRequestException(message, null, status);
        }
    }

    private static Manga ReadManga(JsonElement item)
    {
        var attr = item.GetProperty("attributes");
        string id = item.GetProperty("id").GetString()!;
        string? cover = null;
        foreach (var rel in item.GetProperty("relationships").EnumerateArray())
            if (Text(rel, "type") == "cover_art" && rel.TryGetProperty("attributes", out var coverAttr))
                cover = $"https://uploads.mangadex.org/covers/{id}/{Uri.EscapeDataString(Text(coverAttr, "fileName") ?? "")}.256.jpg";
        var languages = attr.TryGetProperty("availableTranslatedLanguages", out var langs) && langs.ValueKind == JsonValueKind.Array
            ? langs.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).ToList() : [];
        return new(id, Localized(attr.GetProperty("title")), Localized(attr.GetProperty("description")), cover, languages);
    }

    private static Chapter ReadChapter(JsonElement item)
    {
        var attr = item.GetProperty("attributes");
        var groups = new List<Group>();
        foreach (var rel in item.GetProperty("relationships").EnumerateArray())
            if (Text(rel, "type") == "scanlation_group")
                groups.Add(new(rel.GetProperty("id").GetString()!, rel.TryGetProperty("attributes", out var ga) ? Text(ga, "name") ?? "Без названия" : "Без названия"));
        return new(item.GetProperty("id").GetString()!, Text(attr, "chapter"), Text(attr, "volume"), Text(attr, "title") ?? "",
            Text(attr, "translatedLanguage") ?? "", attr.GetProperty("pages").GetInt32(), groups, Text(attr, "externalUrl"));
    }

    private static string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Localized(JsonElement values)
    {
        foreach (var key in new[] { "ru", "en", "ja-ro", "ko-ro" }) if (Text(values, key) is string text && text.Length > 0) return text;
        return values.EnumerateObject().FirstOrDefault().Value is var first && first.ValueKind == JsonValueKind.String ? first.GetString()! : "";
    }

    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
