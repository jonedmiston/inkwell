using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using Tesseract;

namespace Inkwell.Services;

public class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly TesseractEngine _engine;

    // Leptonica (bundled with the Tesseract NuGet) doesn't include webp support,
    // so we decode these formats via ImageSharp and hand Tesseract a PNG buffer.
    private static readonly HashSet<string> ReencodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webp",
    };

    public TesseractOcrEngine(string tessdataPath, string language)
    {
        _engine = new TesseractEngine(tessdataPath, language, EngineMode.Default);
    }

    public Task<string> TranscribeAsync(string imagePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            using var pix = ReencodeExtensions.Contains(Path.GetExtension(imagePath))
                ? LoadViaImageSharp(imagePath)
                : Pix.LoadFromFile(imagePath);

            using var page = _engine.Process(pix);
            return page.GetText();
        }, ct);
    }

    public Task<string> TranscribePngAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var pix = Pix.LoadFromMemory(pngBytes);
            using var page = _engine.Process(pix);
            return page.GetText();
        }, ct);
    }

    private static Pix LoadViaImageSharp(string imagePath)
    {
        using var image = Image.Load(imagePath);
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return Pix.LoadFromMemory(ms.ToArray());
    }

    public void Dispose() => _engine.Dispose();
}
