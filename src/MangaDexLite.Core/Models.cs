using System.Globalization;

namespace MangaDexLite.Core;

public sealed record Manga(string Id, string Title, string Description, string? CoverUrl, IReadOnlyList<string>? Languages = null);
public sealed record Group(string Id, string Name);
public sealed record Chapter(string Id, string? Number, string? Volume, string Title,
    string Language, int Pages, IReadOnlyList<Group> Groups, string? ExternalUrl)
{
    public bool CanDownload => Pages > 0 && string.IsNullOrEmpty(ExternalUrl);
    public string GroupNames => Groups.Count == 0 ? "Без группы" : string.Join(", ", Groups.Select(g => g.Name));
    public string Label => $"{(Volume is null ? "" : $"Том {Volume} · ")}Глава {Number ?? "—"}{(Title.Length == 0 ? "" : " — " + Title)}";
    public string Details => $"{Language.ToUpperInvariant()} · {GroupNames} · {(CanDownload ? $"{Pages} стр." : "недоступна для скачивания")}";
    public bool Matches(string? language, string? groupId) =>
        (string.IsNullOrEmpty(language) || Language == language) &&
        (string.IsNullOrEmpty(groupId) || (groupId == "ungrouped" ? Groups.Count == 0 : Groups.Any(g => g.Id == groupId)));
    public bool InRange(decimal from, decimal to) =>
        decimal.TryParse(Number, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number >= from && number <= to;
}

public sealed record DownloadOptions(string Directory, bool Cbz, bool DataSaver);
public sealed record DownloadProgress(int CompletedPages, int TotalPages, string Message);
public sealed record ChapterImages(string BaseUrl, string Hash, IReadOnlyList<string> Files);

public static class OutputNames
{
    public static string Safe(string value)
    {
        var name = string.Concat(value.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (name.Length > 64) name = name[..64].TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "untitled";
        string stem = name.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            name = "_" + name;
        return name;
    }
}
