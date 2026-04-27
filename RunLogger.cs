using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Inkwell.Services;

// Per-run text log. When enabled, also redirects native (C runtime) stderr to
// the same file so warnings printed by Leptonica/Tesseract (e.g. "Error in
// boxClipToRectangle: box outside rectangle") get captured alongside our own
// per-file status entries.
public sealed class RunLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _lock = new();
    private static FileStream? _stderrStream; // kept alive for native SetStdHandle target

    public string? Path { get; }
    public bool Enabled => _writer is not null;

    private RunLogger(StreamWriter? writer, string? path)
    {
        _writer = writer;
        Path = path;
    }

    public static RunLogger Disabled() => new(null, null);

    public static RunLogger Open(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // FileShare.ReadWrite so a parallel native handle (from freopen below) can
        // append to the same file without conflict.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream) { AutoFlush = true };

        TryRedirectNativeStderr(path);
        TryRedirectOsStderr(path);

        return new RunLogger(writer, path);
    }

    public void WriteLine(string line)
    {
        if (_writer is null) return;
        lock (_lock) _writer.WriteLine(line);
    }

    public void WriteHeader(IEnumerable<(string Key, string Value)> settings)
    {
        WriteLine($"# inkwell run {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        foreach (var (k, v) in settings)
        {
            WriteLine($"# {k}: {v}");
        }
        WriteLine("# ---");
    }

    public void WriteSuccess(string relPath, long elapsedMs, int chars, int? pages = null)
    {
        var pageInfo = pages is int p ? $" pages={p}" : "";
        WriteLine($"OK   {relPath}\telapsed={elapsedMs}ms\tchars={chars}{pageInfo}");
    }

    public void WriteFailure(string relPath, long elapsedMs, string error)
    {
        WriteLine($"FAIL {relPath}\telapsed={elapsedMs}ms\terror={error.Replace('\r', ' ').Replace('\n', ' ')}");
    }

    public void WriteFooter(int succeeded, int failed)
    {
        WriteLine("# ---");
        WriteLine($"# done {DateTime.Now:yyyy-MM-dd HH:mm:ss}  succeeded={succeeded}  failed={failed}");
    }

    public void Dispose()
    {
        _writer?.Dispose();
        FlushNativeStderr();
    }

    // Windows-only: redirect the C runtime's stderr stream so any native fprintf(stderr, ...)
    // (from Leptonica, Tesseract, PDFium) is appended to our log file. Best-effort; silent on failure.
    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern IntPtr freopen(string filename, string mode, IntPtr stream);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "__acrt_iob_func")]
    private static extern IntPtr __acrt_iob_func(uint index);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int setvbuf(IntPtr stream, IntPtr buffer, int mode, UIntPtr size);

    [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int fflush(IntPtr stream);

    private const int _IONBF = 4; // No buffering, so writes hit the file immediately.

    private static void TryRedirectNativeStderr(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            // stderr is index 2 in the iob table.
            var stderrFile = __acrt_iob_func(2);
            if (stderrFile == IntPtr.Zero) return;
            if (freopen(path, "a", stderrFile) == IntPtr.Zero) return;
            // The new stream is fully-buffered by default for files; force unbuffered so messages
            // appear in the log in real time and survive abnormal termination.
            setvbuf(stderrFile, IntPtr.Zero, _IONBF, UIntPtr.Zero);
        }
        catch
        {
            // Couldn't reach the CRT entry points; skip redirection.
        }
    }

    private static void FlushNativeStderr()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var stderrFile = __acrt_iob_func(2);
            if (stderrFile != IntPtr.Zero) fflush(stderrFile);
        }
        catch { /* best-effort */ }
        try { _stderrStream?.Flush(); } catch { /* best-effort */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    private const int STD_ERROR_HANDLE = -12;

    // Some native libraries call WriteFile on STD_ERROR_HANDLE directly, bypassing the C runtime's
    // FILE* stderr. Redirect the OS-level handle too so those writes also land in the log.
    private static void TryRedirectOsStderr(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            _stderrStream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            SetStdHandle(STD_ERROR_HANDLE, _stderrStream.SafeFileHandle.DangerousGetHandle());
        }
        catch
        {
            // Best-effort.
        }
    }
}
