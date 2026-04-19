using Inkwell.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<TranscribeCommand>();
app.Configure(config =>
{
    config.SetApplicationName("inkwell");
    config.SetApplicationVersion("1.0.0");
});
return await app.RunAsync(args);
