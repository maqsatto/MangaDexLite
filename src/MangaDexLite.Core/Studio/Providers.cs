namespace MangaDexLite.Core.Studio;

public sealed record TranslationRequest(string Text, string SourceLanguage, string TargetLanguage);
public sealed record TranslationResult(string Text, string Provider);

public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

/// <summary>Offline deterministic placeholder. This is deliberately not a real translation engine.</summary>
public sealed class MockTranslationProvider : ITranslationProvider
{
    public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Сначала введите исходный текст области.");
        return Task.FromResult(new TranslationResult($"[MOCK → {request.TargetLanguage}] {request.Text}", "mock"));
    }
}

public sealed record ImageInfo(int PixelWidth, int PixelHeight);
public sealed record OcrRegion(PixelBounds Bounds, string Text);

/// <summary>
/// Worker boundary. Inspect reads metadata; future OCR and inpaint workers must accept cancellation.
/// Inpaint must write to a separate output file and must never mutate sourceFile.
/// </summary>
public interface IImageProcessor
{
    Task<ImageInfo> InspectAsync(string sourceFile, CancellationToken ct);
    Task<IReadOnlyList<OcrRegion>> RecognizeAsync(string sourceFile, CancellationToken ct);
    Task InpaintAsync(string sourceFile, IReadOnlyList<PixelBounds> regions, string outputFile, CancellationToken ct);
}
