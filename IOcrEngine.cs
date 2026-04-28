namespace Inkwell.Services;

public interface IOcrEngine
{
    Task<string> TranscribeAsync(string imagePath, CancellationToken ct = default);

    // Used for PDF page rendering: caller hands us PNG bytes already in memory.
    Task<string> TranscribePngAsync(byte[] pngBytes, CancellationToken ct = default);
}
