using System.IO.Compression;
using System.Text.Json;

namespace MangaDexLite.Core;

/// <summary>Serial chapter writer. Final names are only published after a successful write.</summary>
public sealed class ChapterDownloader(MangaDexClient api)
{
    public async Task<string> DownloadAsync(Manga manga, Chapter chapter, DownloadOptions options,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!chapter.CanDownload) throw new InvalidOperationException("Эта глава не размещена на серверах MangaDex.");
        if (!Path.IsPathFullyQualified(options.Directory)) throw new ArgumentException("Выберите абсолютный путь папки.");
        // Full IDs prevent collisions between releases, languages and identically named groups.
        string parent = Path.Combine(options.Directory, OutputNames.Safe(manga.Title) + "_" + OutputNames.Safe(manga.Id));
        string stem = $"v{OutputNames.Safe(chapter.Volume ?? "0")}_ch{OutputNames.Safe(chapter.Number ?? "extra")}_{OutputNames.Safe(chapter.Id)}" + (options.DataSaver ? "_small" : "_original");
        string folder = Path.Combine(parent, stem);
        string archive = folder + ".cbz";
        if (options.Cbz && File.Exists(archive))
        {
            using var zip = ZipFile.OpenRead(archive);
            // Archives are written in page order; lexical sorting breaks page 1000 onward.
            var entries = zip.Entries;
            if (entries.Count == chapter.Pages && entries.Select((e, i) => e.Length > 0 &&
                e.FullName == $"{i + 1:000}{Extension(e.FullName)}").All(valid => valid))
            {
                progress?.Report(new(chapter.Pages, chapter.Pages, "Уже скачано"));
                return archive;
            }
            throw new IOException("CBZ уже существует, но повреждён. Переместите его и повторите загрузку.");
        }
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, ".complete.json");
        var images = await api.GetImagesAsync(chapter.Id, options.DataSaver, ct);
        var pageNames = images.Files.Select((file, i) => $"{i + 1:000}{Extension(file)}").ToArray();
        // Bind partial pages to the exact CDN generation; changed remote pages are never mixed.
        string identityPath = Path.Combine(folder, ".source.json");
        string identity = JsonSerializer.Serialize(new { images.Hash, images.Files });
        if (File.Exists(identityPath))
        {
            if (await File.ReadAllTextAsync(identityPath, ct) != identity)
                throw new IOException("Состав главы изменился на MangaDex. Переместите прежнюю папку главы и повторите.");
        }
        else
        {
            if (Directory.EnumerateFiles(folder).Any()) throw new IOException("Папка главы уже содержит неизвестные файлы. Выберите другую папку.");
            await AtomicTextAsync(identityPath, identity, ct);
        }
        for (int index = 0; index < images.Files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            string destination = Path.Combine(folder, pageNames[index]);
            if (!File.Exists(destination))
            {
                string partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    string url = images.BaseUrl + Uri.EscapeDataString(images.Hash) + "/" + Uri.EscapeDataString(images.Files[index]);
                    await api.DownloadImageAsync(url, partial, ct);
                    File.Move(partial, destination, false);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            if (new FileInfo(destination).Length == 0) throw new IOException("Обнаружена пустая страница. Переместите её и повторите загрузку.");
            progress?.Report(new(index + 1, images.Files.Count, $"Страница {index + 1} из {images.Files.Count}"));
        }
        await AtomicTextAsync(marker, JsonSerializer.Serialize(pageNames), ct);
        if (!options.Cbz) return folder;
        string tempArchive = archive + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(tempArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, true);
                foreach (string name in pageNames)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    await using var input = new FileStream(Path.Combine(folder, name), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await using var target = entry.Open();
                    await input.CopyToAsync(target, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(tempArchive, archive, false);
        }
        finally { if (File.Exists(tempArchive)) File.Delete(tempArchive); }
        return archive;
    }

    private static string Extension(string name)
    {
        string extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" or ".avif" ? extension : ".img";
    }

    private static async Task AtomicTextAsync(string path, string text, CancellationToken ct)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await File.WriteAllTextAsync(temp, text, ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
