using MangaDexLite.Core.Studio;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace MangaDexLite.Studio;

/// <summary>Windows image metadata/preview only. Future worker methods intentionally do not process pixels.</summary>
public sealed class WindowsImageProcessor : IImageProcessor
{
    public async Task<ImageInfo> InspectAsync(string sourceFile, CancellationToken ct)
    {
        await using var file = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        return new(checked((int)decoder.OrientedPixelWidth), checked((int)decoder.OrientedPixelHeight));
    }

    public async Task<SoftwareBitmapSource> ReadPreviewAsync(string sourceFile, StudioPage page, CancellationToken ct)
    {
        await using var file = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        if (decoder.OrientedPixelWidth != page.PixelWidth || decoder.OrientedPixelHeight != page.PixelHeight)
            throw new InvalidDataException("Размер исходного изображения изменился. Импортируйте главу заново, чтобы области не сместились.");
        // Bound preview memory for tall webtoon pages; overlays still use full source-pixel coordinates.
        double scale = Math.Min(1, Math.Min(2048.0 / decoder.PixelWidth, 8192.0 / decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)), ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap).AsTask(ct);
        return source;
    }

    public Task<IReadOnlyList<OcrRegion>> RecognizeAsync(string sourceFile, CancellationToken ct) => throw new NotSupportedException("OCR ещё не подключён. Создайте область и введите текст вручную.");
    public Task InpaintAsync(string sourceFile, IReadOnlyList<PixelBounds> regions, string outputFile, CancellationToken ct) => throw new NotSupportedException("Inpainting ещё не подключён. Исходные изображения остаются неизменными.");
}
