using System.Globalization;

namespace MangaDexLite.Core.Studio;

/// <summary>GUI-independent operations; future MCP handlers can use the same typed requests and IDs.</summary>
public sealed class StudioOperations
{
    public async Task<StudioProject> ImportChapterAsync(string directory, IImageProcessor processor, CancellationToken ct)
    {
        string root = Path.GetFullPath(directory);
        return await Task.Run(async () =>
        {
            var comparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);
            var files = Directory.EnumerateFiles(root).Where(file => Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".avif")
                .OrderBy(Path.GetFileName, comparer).ThenBy(Path.GetFileName, StringComparer.Ordinal).ToList();
            if (files.Count == 0) throw new InvalidDataException("В папке нет поддерживаемых изображений главы.");
            var pages = new List<StudioPage>(files.Count);
            foreach (string file in files)
            {
                ct.ThrowIfCancellationRequested();
                var info = await processor.InspectAsync(file, ct).ConfigureAwait(false);
                if (info.PixelWidth <= 0 || info.PixelHeight <= 0) throw new InvalidDataException($"Неверные размеры изображения: {Path.GetFileName(file)}");
                pages.Add(new(Guid.NewGuid(), Path.GetFileName(file), info.PixelWidth, info.PixelHeight, []));
            }
            return new StudioProject(1, Guid.NewGuid(), Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), root, "auto", "ru", pages);
        }, ct).ConfigureAwait(false);
    }

    public Task<StudioLoadResult> LoadAsync(string file, CancellationToken ct) => StudioProjectStore.LoadAsync(file, ct);
    public Task SaveAsync(StudioProject project, string file, CancellationToken ct) => StudioProjectStore.SaveAsync(project, file, ct);

    public StudioProject AddRegion(StudioProject project, Guid pageId, PixelBounds bounds)
    {
        var page = GetPage(project, pageId);
        bounds.ValidateWithin(page.PixelWidth, page.PixelHeight);
        return ReplacePage(project, page with { Regions = page.Regions.Append(new TextRegion(Guid.NewGuid(), bounds)).ToArray() });
    }

    public StudioProject UpdateRegion(StudioProject project, Guid pageId, Guid regionId, PixelBounds bounds, string sourceText, string translation)
    {
        var page = GetPage(project, pageId);
        var region = GetRegion(page, regionId);
        bounds.ValidateWithin(page.PixelWidth, page.PixelHeight);
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(translation);
        if (region.Bounds == bounds && region.SourceText == sourceText && region.Translation == translation) return project;
        return ReplaceRegion(project, page, region with { Bounds = bounds, SourceText = sourceText, Translation = translation,
            ReviewStatus = ReviewStatus.Draft, TranslationProvider = region.Translation == translation ? region.TranslationProvider : "manual" });
    }

    public StudioProject SetReviewStatus(StudioProject project, Guid pageId, Guid regionId, ReviewStatus status)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("Неизвестный статус проверки.");
        var page = GetPage(project, pageId);
        var region = GetRegion(page, regionId);
        if (status == ReviewStatus.Approved && string.IsNullOrWhiteSpace(region.Translation)) throw new ArgumentException("Нельзя одобрить пустой перевод.");
        if (region.ReviewStatus == status) return project;
        return ReplaceRegion(project, page, region with { ReviewStatus = status });
    }

    public StudioProject RemoveRegion(StudioProject project, Guid pageId, Guid regionId)
    {
        var page = GetPage(project, pageId);
        _ = GetRegion(page, regionId);
        return ReplacePage(project, page with { Regions = page.Regions.Where(r => r.Id != regionId).ToArray() });
    }

    public async Task<StudioProject> TranslateRegionAsync(StudioProject project, Guid pageId, Guid regionId, ITranslationProvider provider, CancellationToken ct)
    {
        var page = GetPage(project, pageId);
        var region = GetRegion(page, regionId);
        ct.ThrowIfCancellationRequested();
        var result = await provider.TranslateAsync(new(region.SourceText, project.SourceLanguage, project.TargetLanguage), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(result.Text)) throw new InvalidDataException("Провайдер вернул пустой перевод.");
        return ReplaceRegion(project, page, region with { Translation = result.Text, TranslationProvider = result.Provider, ReviewStatus = ReviewStatus.NeedsReview });
    }

    public static StudioPage GetPage(StudioProject project, Guid pageId) => project.Pages.FirstOrDefault(p => p.Id == pageId) ?? throw new ArgumentException("Страница не найдена.");
    public static TextRegion GetRegion(StudioPage page, Guid regionId) => page.Regions.FirstOrDefault(r => r.Id == regionId) ?? throw new ArgumentException("Область не найдена.");
    private static StudioProject ReplacePage(StudioProject project, StudioPage page) => project with { Pages = project.Pages.Select(p => p.Id == page.Id ? page : p).ToArray() };
    private static StudioProject ReplaceRegion(StudioProject project, StudioPage page, TextRegion region) => ReplacePage(project, page with { Regions = page.Regions.Select(r => r.Id == region.Id ? region : r).ToArray() });
}
