using Anthropic;
using Spectre.Console;

namespace Inkwell;

/// <summary>
/// Fetches the live model list from the Anthropic API. Shared by the
/// post-arg-parsing model picker and the interactive entry flow.
/// </summary>
internal static class ModelCatalog
{
    public static async Task<List<string>?> FetchAsync(AnthropicClient client)
    {
        List<string>? modelIds = null;
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

        if (modelIds is null || modelIds.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No models returned from the API.[/]");
            return null;
        }

        return modelIds;
    }
}
