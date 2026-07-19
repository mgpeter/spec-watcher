using Spectre.Console.Cli;
using SpecWatcher;

var app = new CommandApp<WatchCommand>();
app.Configure(config =>
{
    config.SetApplicationName("spec-watcher");
    config.SetApplicationVersion("1.0.0");
    config.AddExample("C:\\repos\\personal\\car-tracker");
    config.AddExample(".", "-s", "docs/specs", "-i", "30");
});
return await app.RunAsync(args);
