using System.ComponentModel;
using Anthropic;
using Anthropic.Models.Messages;
using Inkwell.Services;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Inkwell.Commands;

public class TranscribeCommand : AsyncCommand<TranscribeCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<source>")]
        [Description("Directory containing images to transcribe")]
        public string SourceDirectory { get; set; } = "";

        [CommandOption("--engine <name>")]
        [Description("OCR engine: claude or tesseract (default: claude, or DefaultEngine from config)")]
        public string? Engine { get; set; }

        [CommandOption("-o|--output <dir>")]
        [Description("Output directory (default: <source>/transcriptions)")]
        public string? OutputDirectory { get; set; }

        [CommandOption("-p|--prompt <file>")]
        [Description("[[claude]] Path to a prompt file (default: built-in OCR prompt)")]
        public string? PromptFile { get; set; }

        [CommandOption("-b|--book-mode")]
        [Description("[[claude]] Use the book-mode prompt: merges multi-column pages into single-flow text. Ignored if --prompt is set.")]
        public bool BookMode { get; set; }

        [CommandOption("-m|--model <model>")]
        [Description("[[claude]] Model ID (if omitted, you'll pick from the live list)")]
        public string? Model { get; set; }

        [CommandOption("-e|--effort <level>")]
        [Description("[[claude]] Effort: low, medium, high, max (default: high, or DefaultEffort from config)")]
        public string? EffortLevel { get; set; }

        [CommandOption("-l|--language <code>")]
        [Description("[[tesseract]] Language code(s), e.g. eng, deu, eng+fra (default: eng, or DefaultLanguage from config)")]
        public string? Language { get; set; }

        [CommandOption("--tessdata <path>")]
        [Description("[[tesseract]] Path to tessdata directory (default: TESSDATA_PREFIX env, TessDataPath in config, or ./tessdata)")]
        public string? TessDataPath { get; set; }

        [CommandOption("--tessdata-source <variant>")]
        [Description("[[tesseract]] Source for auto-downloaded trained data: best, fast, or main (default: best)")]
        public string? TessDataSource { get; set; }

        [CommandOption("--no-download")]
        [Description("[[tesseract]] Disable auto-download of missing trained data files")]
        public bool NoDownload { get; set; }

        [CommandOption("-r|--recurse")]
        [Description("Recurse into subdirectories (default: off). Output mirrors source structure.")]
        public bool Recurse { get; set; }

        [CommandOption("-s|--skip-existing")]
        [Description("Skip images whose output file already exists (resume mode)")]
        public bool SkipExisting { get; set; }

        [CommandOption("--pdf-dpi <dpi>")]
        [Description("Render resolution for PDF pages (default: 300)")]
        public int? PdfDpi { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Skip the confirmation prompt and start processing immediately")]
        public bool Yes { get; set; }

        [CommandOption("--no-log")]
        [Description("Disable per-run log file (default: log to ./inkwell-<timestamp>.log in the current directory)")]
        public bool NoLog { get; set; }

        [CommandOption("--log <path>")]
        [Description("Custom log file path (default: ./inkwell-<timestamp>.log)")]
        public string? LogPath { get; set; }

        [CommandOption("-t|--timeout <seconds>")]
        [Description("[[claude]] HTTP request timeout in seconds (default: SDK default)")]
        public int? TimeoutSeconds { get; set; }
    }

    private static readonly string[] ValidEffortLevels = ["low", "medium", "high", "max"];
    private static readonly string[] ValidEngines = ["claude", "tesseract"];

    // Effort parameter is GA on Opus 4.5/4.6/4.7 and Sonnet 4.6 only.
    // Sending it to Sonnet 4.5, Haiku 4.5, or older models returns 400.
    // Match by substring so date-suffixed variants (e.g. claude-opus-4-7-20251015) work.
    private static readonly string[] EffortSupportedPatterns =
    [
        "opus-4-5", "opus-4-6", "opus-4-7", "sonnet-4-6",
    ];

    private static bool ModelSupportsEffort(string modelId) =>
        EffortSupportedPatterns.Any(p => modelId.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static readonly Dictionary<string, string> TessDataSources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["best"] = "https://github.com/tesseract-ocr/tessdata_best/raw/main/{0}.traineddata",
        ["fast"] = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/{0}.traineddata",
        ["main"] = "https://github.com/tesseract-ocr/tessdata/raw/main/{0}.traineddata",
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        // Tesseract also accepts these via Leptonica; Claude does not.
        ".tif", ".tiff", ".bmp",
        // PDFs are rendered to PNG pages and then OCR'd.
        ".pdf",
    };

    private static readonly HashSet<string> ClaudeSupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".pdf",
    };

    private const string BookModePrompt = """
        You are an expert OCR engine specialized in transcribing text from book pages with maximum accuracy.

        Your task is to transcribe the visible printed text in the provided image.

        Rules:
        - Perform an exact transcription of all visible printed text. Do not summarize, interpret, correct spelling, or add any commentary.
        - Output PLAIN TEXT ONLY. Do not use any Markdown syntax: no `#` for headings, no `**` or `_` for emphasis, no `-` or `*` for bullets unless those exact characters appear in the original, no `|` table syntax, no code fences, no links.
        - This page may have multiple columns. Read all columns in natural reading order: top-to-bottom within each column, left-to-right across columns. Merge the columns into a single continuous flow of text.
        - Do not preserve column structure or per-column line breaks. Let paragraphs flow naturally as if the text had been typeset in a single column. Use a single blank line to separate paragraphs.
        - When a word is hyphenated across a line break, join the parts back into one word (e.g. "compre-\nhensive" becomes "comprehensive"). Preserve hyphens that are part of the actual word.
        - Skip page numbers, running headers, and running footers unless they are part of the body content (e.g. a chapter title that opens the body text).
        - Preserve all in-body punctuation, numbers, symbols, italics intent (as plain text), and footnote markers exactly as they appear.
        - For unclear, illegible, or cut-off text, mark clearly as [illegible] or [unclear: description] - do not guess or invent content.
        - Output ONLY the transcription. Do not include any explanations, introductions, conclusions, or extra text outside the transcription itself.

        Begin the transcription now.
        """;

    private const string DefaultPrompt = """
        You are an expert OCR engine specialized in precise, character-by-character transcription of typed or printed text from images.

        Your task is to transcribe the text in the provided image(s) with maximum accuracy and fidelity to the original document.

        Rules:
        - Perform an exact transcription of all visible typed/printed text. Do not summarize, interpret, correct spelling, or add any commentary.
        - Output PLAIN TEXT ONLY. Do not use any Markdown syntax: no `#` for headings, no `**` or `_` for emphasis, no `-` or `*` for bullets unless those exact characters appear in the original, no `|` table syntax, no code fences, no links.
        - Preserve the original layout using whitespace only: match line breaks, paragraph spacing, indentation, and column alignment using spaces and newlines as they appear in the source.
        - Transcribe tables as aligned plain text using spaces to line up columns, do not use pipe characters or separator rows unless those literally appear in the image.
        - Preserve all punctuation, numbers, symbols, and formatting characters exactly as they appear.
        - For any text that is unclear, illegible, or cut off, mark it clearly as [illegible] or [unclear: description] - do not guess or invent content.
        - Output ONLY the transcription. Do not include any explanations, introductions, conclusions, or extra text outside the transcription itself.

        Begin the transcription now.
        """;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!Directory.Exists(settings.SourceDirectory))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Source directory does not exist: [yellow]{Markup.Escape(settings.SourceDirectory)}[/]");
            return 1;
        }

        var config = LoadConfig();

        // Open the log file (and install the native stderr redirect) BEFORE constructing the OCR
        // engine. Native libraries (Tesseract/Leptonica/PDFium) cache the stderr handle when their
        // CRT initializes during DLL load, so the redirect must already be in place.
        var logPath = settings.NoLog
            ? null
            : Path.GetFullPath(settings.LogPath ?? Path.Combine(
                Environment.CurrentDirectory,
                $"inkwell-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        using var logger = logPath is null ? RunLogger.Disabled() : RunLogger.Open(logPath);

        var engineRaw = settings.Engine ?? config["DefaultEngine"] ?? "claude";
        var engineName = engineRaw.ToLowerInvariant();
        if (!ValidEngines.Contains(engineName))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid engine: [yellow]{Markup.Escape(engineRaw)}[/]");
            AnsiConsole.MarkupLine($"Valid options: [green]{string.Join("[/], [green]", ValidEngines)}[/]");
            return 1;
        }

        IOcrEngine ocrEngine;
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]");

        var sourceDir = Path.GetFullPath(settings.SourceDirectory);
        var outputDir = Path.GetFullPath(settings.OutputDirectory ?? Path.Combine(sourceDir, "transcriptions"));
        Directory.CreateDirectory(outputDir);

        if (engineName == "claude")
        {
            var (claudeEngine, claudeRows) = await BuildClaudeEngineAsync(settings, config);
            if (claudeEngine is null) return 1;
            ocrEngine = claudeEngine;
            foreach (var (k, v) in claudeRows) summary.AddRow(k, v);
        }
        else
        {
            var (tessEngine, tessRows) = await BuildTesseractEngineAsync(settings, config);
            if (tessEngine is null) return 1;
            ocrEngine = tessEngine;
            foreach (var (k, v) in tessRows) summary.AddRow(k, v);
        }

        var allowedExtensions = engineName == "claude" ? ClaudeSupportedExtensions : SupportedExtensions;
        var searchOption = settings.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var outputDirWithSep = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var images = Directory.EnumerateFiles(sourceDir, "*", searchOption)
            .Where(f => allowedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetFullPath(f).StartsWith(outputDirWithSep, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skipped = 0;
        if (settings.SkipExisting)
        {
            var before = images.Count;
            images = images.Where(f =>
            {
                var rel = Path.GetRelativePath(sourceDir, f);
                var outPath = Path.Combine(outputDir, Path.ChangeExtension(rel, ".txt"));
                return !File.Exists(outPath);
            }).ToList();
            skipped = before - images.Count;
        }

        if (images.Count == 0)
        {
            if (skipped > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]All {skipped} image(s) already have output. Nothing to do.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]No supported image files found in {Markup.Escape(settings.SourceDirectory)}[/]");
                AnsiConsole.MarkupLine($"Supported: [green]{string.Join("[/], [green]", allowedExtensions)}[/]");
            }
            (ocrEngine as IDisposable)?.Dispose();
            return 0;
        }

        summary.AddRow("Source", Markup.Escape(sourceDir));
        summary.AddRow("Output", Markup.Escape(outputDir));
        summary.AddRow("Recurse", settings.Recurse ? "[green]on[/]" : "[dim]off[/]");
        summary.AddRow("Skip existing", settings.SkipExisting ? "[green]on[/]" : "[dim]off[/]");
        summary.AddRow("Log", logPath is null ? "[dim]disabled[/]" : Markup.Escape(logPath));
        summary.AddRow("Images", skipped > 0 ? $"{images.Count} ([dim]{skipped} skipped[/])" : images.Count.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        // Confirmation gate. Skipped with -y/--yes or when stdin isn't a TTY (so piping/automation works).
        if (!settings.Yes && !Console.IsInputRedirected)
        {
            if (!AnsiConsole.Confirm("Proceed?", defaultValue: true))
            {
                AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
                (ocrEngine as IDisposable)?.Dispose();
                return 0;
            }
        }

        logger.WriteHeader(BuildLogSettings(sourceDir, outputDir, settings, ocrEngineLabel: ocrEngine.GetType().Name, imageCount: images.Count, skipped: skipped));

        var succeeded = 0;
        var failed = 0;
        var failures = new List<(string File, string Error)>();

        try
        {
            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                [
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn(),
                ])
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Transcribing[/]", maxValue: images.Count);

                    var pdfDpi = settings.PdfDpi ?? 300;

                    foreach (var imagePath in images)
                    {
                        var relPath = Path.GetRelativePath(sourceDir, imagePath);
                        task.Description = $"[green]Transcribing[/] [blue]{Markup.Escape(relPath)}[/]";

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var pageCount = 0;
                        try
                        {
                            string transcription;
                            if (Path.GetExtension(imagePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                            {
                                transcription = await TranscribePdfAsync(
                                    ocrEngine, imagePath, pdfDpi,
                                    page =>
                                    {
                                        pageCount = page;
                                        task.Description = $"[green]Transcribing[/] [blue]{Markup.Escape(relPath)}[/] [dim](page {page})[/]";
                                    });
                            }
                            else
                            {
                                transcription = await ocrEngine.TranscribeAsync(imagePath);
                            }
                            var outPath = Path.Combine(outputDir, Path.ChangeExtension(relPath, ".txt"));
                            var outParent = Path.GetDirectoryName(outPath);
                            if (!string.IsNullOrEmpty(outParent)) Directory.CreateDirectory(outParent);
                            await File.WriteAllTextAsync(outPath, transcription);
                            sw.Stop();
                            logger.WriteSuccess(relPath, sw.ElapsedMilliseconds, transcription.Length, pageCount > 0 ? pageCount : null);
                            succeeded++;
                        }
                        catch (Exception ex)
                        {
                            sw.Stop();
                            failed++;
                            failures.Add((relPath, ex.Message));
                            logger.WriteFailure(relPath, sw.ElapsedMilliseconds, ex.Message);
                        }

                        task.Increment(1);
                    }

                    task.Description = "[green]Done[/]";
                });
        }
        finally
        {
            (ocrEngine as IDisposable)?.Dispose();
        }

        logger.WriteFooter(succeeded, failed);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓ {succeeded} succeeded[/]   [red]✗ {failed} failed[/]");
        if (logger.Enabled)
        {
            AnsiConsole.MarkupLine($"[dim]Log:[/] {Markup.Escape(logger.Path!)}");
        }
        if (failures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Failures:[/]");
            foreach (var (file, error) in failures)
            {
                AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(file)}[/]: {Markup.Escape(error)}");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static async Task<(ClaudeOcrEngine? Engine, List<(string Key, string Value)> Rows)> BuildClaudeEngineAsync(
        Settings settings, IConfiguration config)
    {
        var apiKey = config["ANTHROPIC_API_KEY"]
            ?? config["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ANTHROPIC_API_KEY not found.");
            AnsiConsole.MarkupLine("Set it via [yellow]appsettings.local.json[/], user secrets, or the [yellow]ANTHROPIC_API_KEY[/] environment variable.");
            AnsiConsole.MarkupLine("See [blue]README.md[/] for setup instructions.");
            return (null, []);
        }

        if (settings.TimeoutSeconds is <= 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --timeout must be greater than 0 (got [yellow]{settings.TimeoutSeconds}[/]).");
            return (null, []);
        }

        // When a custom timeout is set, also disable HttpClient's own 100s default
        // so the SDK's Timeout property is authoritative (the SDK uses a separate
        // cancellation token, but HttpClient.Timeout still fires independently).
        var client = settings.TimeoutSeconds is int secs
            ? new AnthropicClient
            {
                ApiKey = apiKey,
                Timeout = TimeSpan.FromSeconds(secs),
                HttpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            }
            : new AnthropicClient { ApiKey = apiKey };

        // Effort is optional. If neither --effort nor DefaultEffort is set, omit the
        // parameter entirely (API default is "high"). This also avoids 400s on models
        // that don't accept the parameter at all (Haiku 4.5, Sonnet 4.5, older).
        var effortExplicit = settings.EffortLevel ?? config["DefaultEffort"];
        string? effortLower = effortExplicit?.ToLowerInvariant();
        if (effortLower is not null && !ValidEffortLevels.Contains(effortLower))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid effort level: [yellow]{Markup.Escape(effortExplicit!)}[/]");
            AnsiConsole.MarkupLine($"Valid options: [green]{string.Join("[/], [green]", ValidEffortLevels)}[/]");
            return (null, []);
        }

        var model = settings.Model ?? config["DefaultModel"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = await PromptForModelAsync(client);
            if (model is null) return (null, []);
        }

        // If the chosen model doesn't support effort, drop it. Warn only if the user
        // explicitly asked for a value (so we don't yell at people on Haiku for
        // letting effort default).
        if (effortLower is not null && !ModelSupportsEffort(model))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Note:[/] [cyan]{Markup.Escape(model)}[/] does not support the effort parameter; ignoring [yellow]--effort {Markup.Escape(effortLower)}[/].");
            effortLower = null;
        }

        string prompt;
        string promptLabel;
        if (!string.IsNullOrWhiteSpace(settings.PromptFile))
        {
            if (!File.Exists(settings.PromptFile))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Prompt file not found: [yellow]{Markup.Escape(settings.PromptFile)}[/]");
                return (null, []);
            }
            prompt = await File.ReadAllTextAsync(settings.PromptFile);
            promptLabel = Markup.Escape(settings.PromptFile);
        }
        else if (settings.BookMode)
        {
            prompt = BookModePrompt;
            promptLabel = "[cyan]book-mode[/] [dim](built-in)[/]";
        }
        else
        {
            prompt = DefaultPrompt;
            promptLabel = "[dim]built-in[/]";
        }

        var rows = new List<(string, string)>
        {
            ("Engine", "[cyan]claude[/]"),
            ("Model", Markup.Escape(model)),
            ("Effort", effortLower ?? "[dim]API default[/]"),
            ("Prompt", promptLabel),
            ("Timeout", settings.TimeoutSeconds is int t ? $"{t}s" : "[dim]SDK default[/]"),
        };

        return (new ClaudeOcrEngine(client, model, prompt, effortLower), rows);
    }

    private static async Task<(TesseractOcrEngine? Engine, List<(string Key, string Value)> Rows)> BuildTesseractEngineAsync(
        Settings settings, IConfiguration config)
    {
        var language = settings.Language ?? config["DefaultLanguage"] ?? "eng";

        var tessdata = settings.TessDataPath
            ?? Environment.GetEnvironmentVariable("TESSDATA_PREFIX")
            ?? config["TessDataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "tessdata");

        tessdata = Path.GetFullPath(tessdata);

        var sourceRaw = settings.TessDataSource ?? config["TessDataSource"] ?? "best";
        if (!TessDataSources.TryGetValue(sourceRaw, out var urlTemplate))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid --tessdata-source: [yellow]{Markup.Escape(sourceRaw)}[/]");
            AnsiConsole.MarkupLine($"Valid options: [green]{string.Join("[/], [green]", TessDataSources.Keys)}[/]");
            return (null, []);
        }

        var allowDownload = !settings.NoDownload;
        var languages = language.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missing = languages
            .Where(lang => !File.Exists(Path.Combine(tessdata, $"{lang}.traineddata")))
            .ToList();

        if (missing.Count > 0)
        {
            if (!allowDownload)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Missing trained data in [yellow]{Markup.Escape(tessdata)}[/]:");
                foreach (var lang in missing)
                {
                    AnsiConsole.MarkupLine($"  [yellow]{Markup.Escape(lang)}.traineddata[/]");
                }
                AnsiConsole.MarkupLine("Re-run without [yellow]--no-download[/] to fetch them automatically, or download from");
                AnsiConsole.MarkupLine($"[blue]https://github.com/tesseract-ocr/tessdata_{(sourceRaw == "main" ? "" : sourceRaw)}[/]");
                return (null, []);
            }

            try
            {
                Directory.CreateDirectory(tessdata);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error creating tessdata directory:[/] {Markup.Escape(ex.Message)}");
                return (null, []);
            }

            foreach (var lang in missing)
            {
                var url = string.Format(urlTemplate, lang);
                var dest = Path.Combine(tessdata, $"{lang}.traineddata");
                if (!await DownloadTrainedDataAsync(url, dest, lang, sourceRaw))
                {
                    return (null, []);
                }
            }
        }

        TesseractOcrEngine engine;
        try
        {
            engine = new TesseractOcrEngine(tessdata, language);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error initializing Tesseract:[/] {Markup.Escape(ex.Message)}");
            return (null, []);
        }

        var rows = new List<(string, string)>
        {
            ("Engine", "[cyan]tesseract[/]"),
            ("Language", Markup.Escape(language)),
            ("Tessdata", Markup.Escape(tessdata)),
            ("Data source", $"tessdata_{Markup.Escape(sourceRaw)}"),
        };

        return (engine, rows);
    }

    private static async Task<bool> DownloadTrainedDataAsync(string url, string destPath, string lang, string sourceLabel)
    {
        var tempPath = destPath + ".tmp";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(
                [
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new DownloadedColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn(),
                ])
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask(
                        $"[green]Downloading[/] [cyan]{Markup.Escape(lang)}.traineddata[/] [dim](tessdata_{Markup.Escape(sourceLabel)})[/]",
                        maxValue: totalBytes > 0 ? totalBytes : 1);
                    if (totalBytes <= 0) task.IsIndeterminate = true;

                    await using var src = await response.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(tempPath);
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await src.ReadAsync(buffer)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read));
                        if (totalBytes > 0) task.Increment(read);
                    }
                    if (totalBytes > 0) task.Value = totalBytes;
                });

            File.Move(tempPath, destPath, overwrite: true);
            AnsiConsole.MarkupLine($"[green]Downloaded[/] [cyan]{Markup.Escape(lang)}.traineddata[/]");
            return true;
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            AnsiConsole.MarkupLine($"[red]Failed to download[/] [cyan]{Markup.Escape(lang)}.traineddata[/]: {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine($"  URL: [blue]{Markup.Escape(url)}[/]");
            return false;
        }
    }

    private static IEnumerable<(string Key, string Value)> BuildLogSettings(
        string sourceDir, string outputDir, Settings settings, string ocrEngineLabel, int imageCount, int skipped)
    {
        yield return ("source", sourceDir);
        yield return ("output", outputDir);
        yield return ("engine", ocrEngineLabel);
        yield return ("recurse", settings.Recurse.ToString());
        yield return ("skip-existing", settings.SkipExisting.ToString());
        yield return ("images", $"{imageCount} (skipped={skipped})");
        if (settings.PdfDpi is int dpi) yield return ("pdf-dpi", dpi.ToString());
        if (!string.IsNullOrEmpty(settings.Model)) yield return ("model", settings.Model);
        if (!string.IsNullOrEmpty(settings.EffortLevel)) yield return ("effort", settings.EffortLevel);
        if (settings.BookMode) yield return ("book-mode", "true");
        if (!string.IsNullOrEmpty(settings.PromptFile)) yield return ("prompt", settings.PromptFile);
        if (!string.IsNullOrEmpty(settings.Language)) yield return ("language", settings.Language);
    }

    private static async Task<string> TranscribePdfAsync(
        IOcrEngine engine, string pdfPath, int dpi, Action<int> onPageStart)
    {
        var pages = new List<string>();
        var pageNumber = 0;
        var options = new PDFtoImage.RenderOptions { Dpi = dpi };

        // ToImages renders pages lazily; dispose each bitmap as we go.
        // The string overload of ToImages expects base64; use the Stream overload for file paths.
        using var pdfStream = File.OpenRead(pdfPath);
#pragma warning disable CA1416 // PDFtoImage is supported on all platforms we run on.
        foreach (var bitmap in PDFtoImage.Conversion.ToImages(pdfStream, leaveOpen: false, options: options))
#pragma warning restore CA1416
        {
            pageNumber++;
            onPageStart(pageNumber);
            byte[] pngBytes;
            using (bitmap)
            using (var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            {
                pngBytes = data.ToArray();
            }
            pages.Add(await engine.TranscribePngAsync(pngBytes));
        }

        return string.Join("\n\n", pages);
    }

    private static IConfiguration LoadConfig()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddUserSecrets<TranscribeCommand>(optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static async Task<string?> PromptForModelAsync(AnthropicClient client)
    {
        var modelIds = await ModelCatalog.FetchAsync(client);
        if (modelIds is null) return null;

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a [green]model[/]:")
                .PageSize(15)
                .MoreChoicesText("[grey](Move up and down to reveal more models)[/]")
                .AddChoices(modelIds));
    }
}
