namespace MangaDexLite.Core.Studio;

public enum ReviewStatus { Draft, NeedsReview, Approved }

/// <summary>Source-image pixels, independent of viewport scale. Right/bottom edges are exclusive.</summary>
public sealed record PixelBounds(double X, double Y, double Width, double Height)
{
    public void ValidateWithin(int pageWidth, int pageHeight)
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height) ||
            X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > pageWidth || Y + Height > pageHeight)
            throw new ArgumentException("Область должна иметь положительный размер и находиться внутри страницы.");
    }
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
    public static PixelBounds FromPoints(double x1, double y1, double x2, double y2) => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    public PixelBounds MoveTo(double x, double y, int pageWidth, int pageHeight)
    {
        ValidateWithin(pageWidth, pageHeight);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Координаты должны быть конечными числами.");
        return this with { X = Math.Clamp(x, 0, pageWidth - Width), Y = Math.Clamp(y, 0, pageHeight - Height) };
    }
    public PixelBounds ResizeTo(double width, double height, int pageWidth, int pageHeight)
    {
        ValidateWithin(pageWidth, pageHeight);
        if (!double.IsFinite(width) || !double.IsFinite(height)) throw new ArgumentException("Некорректный размер области.");
        return this with { Width = Math.Clamp(width, Math.Min(1, pageWidth - X), pageWidth - X), Height = Math.Clamp(height, Math.Min(1, pageHeight - Y), pageHeight - Y) };
    }
    public PixelBounds Scale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentException("Масштаб должен быть положительным.");
        return new(X * scale, Y * scale, Width * scale, Height * scale);
    }
}

public sealed record TextRegion(Guid Id, PixelBounds Bounds, string SourceText = "", string Translation = "",
    ReviewStatus ReviewStatus = ReviewStatus.Draft, string? TranslationProvider = null);
public sealed record StudioPage(Guid Id, string SourceFile, int PixelWidth, int PixelHeight, IReadOnlyList<TextRegion> Regions);
public sealed record StudioProject(int SchemaVersion, Guid Id, string Title, string SourceDirectory,
    string SourceLanguage, string TargetLanguage, IReadOnlyList<StudioPage> Pages);
public sealed record StudioLoadResult(StudioProject Project, IReadOnlyList<string> Warnings, bool CanOverwrite);
