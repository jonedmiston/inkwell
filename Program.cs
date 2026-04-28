using Inkwell;
using Inkwell.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

if (args.Length == 0 && !Console.IsInputRedirected)
{
    args = await InteractivePrompt.RunAsync();
    if (args is null)
    {
        return 1;
    }
}

var app = new CommandApp<TranscribeCommand>();
app.Configure(config =>
{
    config.SetApplicationName("inkwell");
    config.SetApplicationVersion("1.0.0");
});
return await app.RunAsync(args);
