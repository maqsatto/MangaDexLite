using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaDexLite.Core.Studio;

public static class StudioProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter<ReviewStatus>(JsonNamingPolicy.CamelCase, false) }
    };

    public static string Serialize(StudioProject project)
    {
        Validate(project);
        return JsonSerializer.Serialize(project, Options);
    }

    public static StudioLoadResult Deserialize(string json, string projectDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var warnings = new List<string>();
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Корень проекта должен быть JSON-объектом.");
            int version = root.TryGetProperty("schemaVersion", out var schema) ? schema.GetInt32() : 1;
            if (version != 1) throw new InvalidDataException($"Версия схемы {version} не поддерживается. Исходный файл не изменён.");
            Unknown(root, ["schemaVersion", "id", "title", "sourceDirectory", "sourceLanguage", "targetLanguage", "pages"], warnings);
            Guid id = RequiredId(root);
            string source = RequiredString(root, "sourceDirectory");
            source = Path.GetFullPath(Path.Combine(projectDirectory, source));
            var pages = new List<StudioPage>();
            var pageIds = new HashSet<Guid>();
            foreach (var p in RequiredArray(root, "pages").EnumerateArray())
            {
                try
                {
                    Guid pageId = RequiredId(p);
                    string file = RequiredString(p, "sourceFile");
                    ValidateFilename(file);
                    int width = p.GetProperty("pixelWidth").GetInt32(), height = p.GetProperty("pixelHeight").GetInt32();
                    if (width <= 0 || height <= 0 || !pageIds.Add(pageId)) throw new InvalidDataException("Размеры страницы или идентификатор неверны.");
                    Unknown(p, ["id", "sourceFile", "pixelWidth", "pixelHeight", "regions"], warnings);
                    var regions = new List<TextRegion>();
                    var regionIds = new HashSet<Guid>();
                    if (p.TryGetProperty("regions", out var regionArray))
                    {
                        if (regionArray.ValueKind != JsonValueKind.Array) warnings.Add($"{file}: некорректный список областей пропущен.");
                        else foreach (var r in regionArray.EnumerateArray())
                        {
                            try
                            {
                                Guid regionId = RequiredId(r);
                                var b = r.GetProperty("bounds");
                                var bounds = new PixelBounds(b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("width").GetDouble(), b.GetProperty("height").GetDouble());
                                bounds.ValidateWithin(width, height);
                                if (!regionIds.Add(regionId)) throw new InvalidDataException("Повторяющийся ID области.");
                                Unknown(r, ["id", "bounds", "sourceText", "translation", "reviewStatus", "translationProvider"], warnings);
                                Unknown(b, ["x", "y", "width", "height"], warnings);
                                string status = OptionalString(r, "reviewStatus", "draft", warnings);
                                ReviewStatus review = status switch { "draft" => ReviewStatus.Draft, "needsReview" => ReviewStatus.NeedsReview, "approved" => ReviewStatus.Approved, _ => ReviewStatus.Draft };
                                if (status is not ("draft" or "needsReview" or "approved")) warnings.Add($"{file}: неизвестный статус проверки сброшен.");
                                string translation = OptionalString(r, "translation", "", warnings);
                                if (review == ReviewStatus.Approved && string.IsNullOrWhiteSpace(translation)) { review = ReviewStatus.Draft; warnings.Add($"{file}: одобрение пустого перевода сброшено."); }
                                string? provider = r.TryGetProperty("translationProvider", out var value) && value.ValueKind != JsonValueKind.Null ? OptionalString(r, "translationProvider", "", warnings) : null;
                                regions.Add(new(regionId, bounds, OptionalString(r, "sourceText", "", warnings), translation, review, provider));
                            }
                            catch (Exception ex) when (Recoverable(ex)) { warnings.Add($"{file}: некорректная область пропущена: {ex.Message}"); }
                        }
                    }
                    pages.Add(new(pageId, file, width, height, regions));
                }
                catch (Exception ex) when (Recoverable(ex)) { warnings.Add("Некорректная страница пропущена: " + ex.Message); }
            }
            if (pages.Count == 0) throw new InvalidDataException("Проект не содержит корректных страниц. Файл не изменён.");
            var project = new StudioProject(1, id, OptionalString(root, "title", "Untitled", warnings), source,
                OptionalString(root, "sourceLanguage", "auto", warnings), OptionalString(root, "targetLanguage", "ru", warnings), pages);
            Validate(project);
            return new(project, warnings, warnings.Count == 0);
        }
        catch (Exception ex) when (Recoverable(ex)) { throw new InvalidDataException("Не удалось открыть проект. " + ex.Message, ex); }
    }

    public static async Task<StudioLoadResult> LoadAsync(string file, CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(file);
        string json = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        // Parsing/validation can grow with chapter size; do not occupy the UI thread.
        return await Task.Run(() => Deserialize(json, Path.GetDirectoryName(fullPath)!), ct).ConfigureAwait(false);
    }

    public static async Task SaveAsync(StudioProject project, string file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!file.EndsWith(".mangalite.json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Проекты сохраняются только в *.mangalite.json. Изображения не перезаписываются.");
        string fullPath = Path.GetFullPath(file);
        if (!Path.IsPathFullyQualified(project.SourceDirectory)) throw new ArgumentException("Runtime sourceDirectory должен быть абсолютным путём.");
        if (project.Pages.Any(p => string.Equals(Path.Combine(Path.GetFullPath(project.SourceDirectory), p.SourceFile), fullPath, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Нельзя сохранять проект поверх исходной страницы.");
        string folder = Path.GetDirectoryName(fullPath)!;
        string persisted = await Task.Run(() => Serialize(project with { SourceDirectory = Path.GetRelativePath(folder, project.SourceDirectory) }), ct).ConfigureAwait(false);
        bool exists = File.Exists(fullPath);
        if (exists)
        {
            var existing = await LoadAsync(fullPath, ct).ConfigureAwait(false);
            if (!existing.CanOverwrite || existing.Project.Id != project.Id)
                throw new InvalidDataException("Этот файл содержит другой проект или данные, требующие восстановления. Сохраните под новым именем.");
        }
        string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, persisted, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, exists);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Validate(StudioProject project)
    {
        if (project.SchemaVersion != 1 || project.Id == Guid.Empty || project.Pages.Count == 0 || string.IsNullOrWhiteSpace(project.SourceDirectory) ||
            project.Title is null || project.SourceLanguage is null || project.TargetLanguage is null || project.Pages.Select(p => p.Id).Distinct().Count() != project.Pages.Count)
            throw new ArgumentException("Некорректный проект Studio.");
        foreach (var page in project.Pages)
        {
            ValidateFilename(page.SourceFile);
            if (page.Id == Guid.Empty || page.PixelWidth <= 0 || page.PixelHeight <= 0 || page.Regions.Select(r => r.Id).Distinct().Count() != page.Regions.Count)
                throw new ArgumentException("Некорректная страница Studio.");
            foreach (var region in page.Regions)
            {
                region.Bounds.ValidateWithin(page.PixelWidth, page.PixelHeight);
                if (region.Id == Guid.Empty || region.SourceText is null || region.Translation is null || !Enum.IsDefined(region.ReviewStatus) ||
                    (region.ReviewStatus == ReviewStatus.Approved && string.IsNullOrWhiteSpace(region.Translation)))
                    throw new ArgumentException("Некорректная область Studio.");
            }
        }
    }

    private static void ValidateFilename(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file is "." or ".." || file.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new ArgumentException("sourceFile должен содержать только имя изображения, без пути.");
        if (Path.GetExtension(file).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".avif"))
            throw new ArgumentException("sourceFile должен ссылаться на поддерживаемое изображение.");
    }
    private static bool Recoverable(Exception ex) => ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or InvalidDataException;
    private static Guid RequiredId(JsonElement item) => Guid.TryParse(RequiredString(item, "id"), out var id) && id != Guid.Empty ? id : throw new InvalidDataException("Некорректный ID.");
    private static string RequiredString(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"Отсутствует {key}.");
    private static JsonElement RequiredArray(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array ? value : throw new InvalidDataException($"Некорректный массив {key}.");
    private static string OptionalString(JsonElement item, string key, string fallback, List<string> warnings)
    {
        if (!item.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.String) return value.GetString()!;
        warnings.Add($"Некорректное поле {key} сброшено.");
        return fallback;
    }
    private static void Unknown(JsonElement item, string[] known, List<string> warnings)
    {
        foreach (var property in item.EnumerateObject())
            if (!known.Contains(property.Name, StringComparer.Ordinal)) warnings.Add($"Неизвестное поле {property.Name} сохранено в исходном файле; используйте «Сохранить как».");
    }
}
