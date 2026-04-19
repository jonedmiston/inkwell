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

        [CommandOption("-o|--output <dir>")]
        [Description("Output directory (default: <source>/transcriptions)")]
        public string? OutputDirectory { get; set; }

        [CommandOption("-p|--prompt <file>")]
        [Description("Path to a prompt file (default: built-in OCR prompt)")]
        public string? PromptFile { get; set; }

        [CommandOption("-m|--model <model>")]
        [Description("Claude model ID (if omitted, you'll pick from the live list)")]
        public string? Model { get; set; }

        [CommandOption("-e|--effort <level>")]
        [Description("Effort: low, medium, high, max (default: high, or DefaultEffort from config)")]
        public string? EffortLevel { get; set; }

        [CommandOption("-r|--recurse")]
        [Description("Recurse into subdirectories (default: off). Output mirrors source structure.")]
        public bool Recurse { get; set; }

        [CommandOption("-t|--timeout <seconds>")]
        [Description("HTTP request timeout in seconds (default: SDK default)")]
        public int? TimeoutSeconds { get; set; }
    }

    private static readonly string[] ValidEffortLevels = ["low", "medium", "high", "max"];

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private const string DefaultPrompt = """
        You are an expert OCR engine specialized in precise, character-by-character transcription of typed or printed text from images.

        Your task is to transcribe the text in the provided image(s) with maximum accuracy and fidelity to the original document.

        Rules:
        - Perform an exact transcription of all visible typed/printed text. Do not summarize, interpret, correct spelling, or add any commentary.
        - Output PLAIN TEXT ONLY. Do not use any Markdown syntax: no `#` for headings, no `**` or `_` for emphasis, no `-` or `*` for bullets unless those exact characters appear in the original, no `|` table syntax, no code fences, no links.
        - Preserve the original layout using whitespace only: match line breaks, paragraph spacing, indentation, and column alignment using spaces and newlines as they appear in the source.
        - Transcribe tables as aligned plain text using spaces to line up columns — do not use pipe characters or separator rows unless those literally appear in the image.
        - Preserve all punctuation, numbers, symbols, and formatting characters exactly as they appear.
        - For any text that is unclear, illegible, or cut off, mark it clearly as [illegible] or [unclear: description] — do not guess or invent content.
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

        var apiKey = config["ANTHROPIC_API_KEY"]
            ?? config["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ANTHROPIC_API_KEY not found.");
            AnsiConsole.MarkupLine("Set it via [yellow]appsettings.local.json[/], user secrets, or the [yellow]ANTHROPIC_API_KEY[/] environment variable.");
            AnsiConsole.MarkupLine("See [blue]README.md[/] for setup instructions.");
            return 1;
        }

        if (settings.TimeoutSeconds is <= 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --timeout must be greater than 0 (got [yellow]{settings.TimeoutSeconds}[/]).");
            return 1;
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

        var effortRaw = settings.EffortLevel ?? config["DefaultEffort"] ?? "high";
        var effortLower = effortRaw.ToLowerInvariant();
        if (!ValidEffortLevels.Contains(effortLower))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Invalid effort level: [yellow]{Markup.Escape(effortRaw)}[/]");
            AnsiConsole.MarkupLine($"Valid options: [green]{string.Join("[/], [green]", ValidEffortLevels)}[/]");
            return 1;
        }

        var model = settings.Model ?? config["DefaultModel"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = await PromptForModelAsync(client);
            if (model is null) return 1;
        }

        string prompt;
        if (!string.IsNullOrWhiteSpace(settings.PromptFile))
        {
            if (!File.Exists(settings.PromptFile))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Prompt file not found: [yellow]{Markup.Escape(settings.PromptFile)}[/]");
                return 1;
            }
            prompt = await File.ReadAllTextAsync(settings.PromptFile);
        }
        else
        {
            prompt = DefaultPrompt;
        }

        var sourceDir = Path.GetFullPath(settings.SourceDirectory);
        var outputDir = Path.GetFullPath(settings.OutputDirectory ?? Path.Combine(sourceDir, "transcriptions"));
        Directory.CreateDirectory(outputDir);

        var searchOption = settings.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var outputDirWithSep = outputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var images = Directory.EnumerateFiles(sourceDir, "*", searchOption)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetFullPath(f).StartsWith(outputDirWithSep, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (images.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No supported image files found in {Markup.Escape(settings.SourceDirectory)}[/]");
            AnsiConsole.MarkupLine($"Supported: [green]{string.Join("[/], [green]", SupportedExtensions)}[/]");
            return 0;
        }

        var summary = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]");
        summary.AddRow("Source", Markup.Escape(sourceDir));
        summary.AddRow("Output", Markup.Escape(outputDir));
        summary.AddRow("Recurse", settings.Recurse ? "[green]on[/]" : "[dim]off[/]");
        summary.AddRow("Model", Markup.Escape(model));
        summary.AddRow("Effort", effortLower);
        summary.AddRow("Prompt", string.IsNullOrWhiteSpace(settings.PromptFile) ? "[dim]built-in[/]" : Markup.Escape(settings.PromptFile));
        summary.AddRow("Timeout", settings.TimeoutSeconds is int t ? $"{t}s" : "[dim]SDK default[/]");
        summary.AddRow("Images", images.Count.ToString());
        AnsiConsole.Write(summary);
        AnsiConsole.WriteLine();

        var service = new TranscriptionService(client, model, prompt, effortLower);

        var succeeded = 0;
        var failed = 0;
        var failures = new List<(string File, string Error)>();

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

                foreach (var imagePath in images)
                {
                    var relPath = Path.GetRelativePath(sourceDir, imagePath);
                    task.Description = $"[green]Transcribing[/] [blue]{Markup.Escape(relPath)}[/]";

                    try
                    {
                        var transcription = await service.TranscribeAsync(imagePath);
                        var outPath = Path.Combine(outputDir, Path.ChangeExtension(relPath, ".txt"));
                        var outParent = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(outParent)) Directory.CreateDirectory(outParent);
                        await File.WriteAllTextAsync(outPath, transcription);
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        failures.Add((relPath, ex.Message));
                    }

                    task.Increment(1);
                }

                task.Description = "[green]Done[/]";
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓ {succeeded} succeeded[/]   [red]✗ {failed} failed[/]");
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
        List<string> modelIds = [];
        try
        {
            await AnsiConsole.Status()
                .StartAsync("Fetching available models...", async _ =>
                {
                    var page = await client.Models.List();
                    modelIds = page.Items.Select(m => m.ID).ToList();
                });
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error fetching models:[/] {Markup.Escape(ex.Message)}");
            return null;
        }

        if (modelIds.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No models returned from the API.[/]");
            return null;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a [green]model[/]:")
                .PageSize(15)
                .MoreChoicesText("[grey](Move up and down to reveal more models)[/]")
                .AddChoices(modelIds));
    }
}
