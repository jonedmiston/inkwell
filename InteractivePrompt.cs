using Anthropic;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace Inkwell;

/// <summary>
/// Walks the user through the most common settings and emits an equivalent
/// command-line argv. Triggered when inkwell is launched with no arguments.
/// </summary>
internal static class InteractivePrompt
{
    private const string UseDefaultSentinel = "(use config default / pick later)";

    public static async Task<string[]?> RunAsync()
    {
        AnsiConsole.Write(new Rule("[cyan]inkwell[/]").LeftJustified());
        AnsiConsole.MarkupLine("[dim]Interactive mode. Press Enter to accept defaults, Ctrl+C to cancel.[/]");
        AnsiConsole.WriteLine();

        // Engine
        var engine = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Engine[/]")
                .AddChoices("claude", "tesseract"));

        // Source
        var source = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Source directory:[/]")
                .Validate(path =>
                    Directory.Exists(path)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Directory does not exist[/]")));
        source = source.Trim().Trim('"');

        // Output
        var output = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Output directory[/] [dim](blank = <source>/transcriptions)[/]:")
                .AllowEmpty());
        output = output.Trim().Trim('"');

        // Recurse
        var recurse = AnsiConsole.Confirm("[bold]Recurse into subdirectories?[/]", false);

        // Skip existing
        var skipExisting = AnsiConsole.Confirm(
            "[bold]Skip images that already have an output file?[/] [dim](resume mode)[/]",
            false);

        var args = new List<string> { source };
        args.Add("--engine");
        args.Add(engine);
        if (!string.IsNullOrWhiteSpace(output))
        {
            args.Add("-o");
            args.Add(output);
        }
        if (recurse) args.Add("-r");
        if (skipExisting) args.Add("-s");

        if (engine == "claude")
        {
            await CollectClaudeArgsAsync(args);
        }
        else
        {
            CollectTesseractArgs(args);
        }

        AnsiConsole.WriteLine();
        return args.ToArray();
    }

    private static async Task CollectClaudeArgsAsync(List<string> args)
    {
        // Book mode
        var bookMode = AnsiConsole.Confirm(
            "[bold]Book mode?[/] [dim](merges multi-column pages into single-flow text)[/]",
            false);
        if (bookMode) args.Add("--book-mode");

        // Custom prompt file
        var promptFile = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Custom prompt file[/] [dim](blank = built-in)[/]:")
                .AllowEmpty());
        promptFile = promptFile.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(promptFile))
        {
            args.Add("-p");
            args.Add(promptFile);
        }

        // Model — fetch live list from the API and let the user pick. Falls back to a
        // free-text prompt if the API key isn't set or the call fails.
        var model = await PromptForModelAsync();
        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("-m");
            args.Add(model);
        }

        // Effort
        var effort = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Effort[/]")
                .AddChoices("default", "low", "medium", "high", "max"));
        if (effort != "default")
        {
            args.Add("-e");
            args.Add(effort);
        }

        // Timeout
        var timeoutText = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]HTTP timeout in seconds[/] [dim](blank = SDK default)[/]:")
                .AllowEmpty()
                .Validate(s =>
                    string.IsNullOrWhiteSpace(s) || (int.TryParse(s, out var n) && n > 0)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Must be a positive integer or blank[/]")));
        timeoutText = timeoutText.Trim();
        if (!string.IsNullOrWhiteSpace(timeoutText))
        {
            args.Add("-t");
            args.Add(timeoutText);
        }
    }

    private static async Task<string?> PromptForModelAsync()
    {
        var apiKey = LoadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AnsiConsole.MarkupLine("[dim]No ANTHROPIC_API_KEY found yet — skipping live model list.[/]");
            var typed = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Model[/] [dim](blank = config default or pick later)[/]:")
                    .AllowEmpty());
            typed = typed.Trim();
            return string.IsNullOrWhiteSpace(typed) ? null : typed;
        }

        var client = new AnthropicClient { ApiKey = apiKey };
        var modelIds = await ModelCatalog.FetchAsync(client);
        if (modelIds is null)
        {
            // Fetch failed — fall back to free text rather than blocking the flow.
            var typed = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]Model[/] [dim](blank = config default or pick later)[/]:")
                    .AllowEmpty());
            typed = typed.Trim();
            return string.IsNullOrWhiteSpace(typed) ? null : typed;
        }

        var choices = new List<string> { UseDefaultSentinel };
        choices.AddRange(modelIds);

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Model[/]")
                .PageSize(15)
                .MoreChoicesText("[grey](Move up and down to reveal more models)[/]")
                .AddChoices(choices));

        return picked == UseDefaultSentinel ? null : picked;
    }

    private static string? LoadApiKey()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddUserSecrets(typeof(InteractivePrompt).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();

        return config["ANTHROPIC_API_KEY"]
            ?? config["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    }

    private static void CollectTesseractArgs(List<string> args)
    {
        var language = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Language code(s)[/] [dim](e.g. eng, deu, eng+fra; blank = config default or eng)[/]:")
                .AllowEmpty());
        language = language.Trim();
        if (!string.IsNullOrWhiteSpace(language))
        {
            args.Add("-l");
            args.Add(language);
        }
    }
}
