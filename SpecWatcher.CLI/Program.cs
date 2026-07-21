using System.Text;
using Spectre.Console.Cli;
using SpecWatcher;

// Render Unicode glyphs (status badges, progress blocks, attention/branch icons, box drawing)
// regardless of the console's default code page — otherwise they degrade to '?'. Use a BOM-less
// UTF-8 so redirected output (headless json/md) stays byte-clean.
try { Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
catch { /* no attached console / redirected in a way that rejects it: ignore */ }

var app = new CommandApp<WatchCommand>();
app.Configure(config =>
{
    config.SetApplicationName("spec-watcher");
    config.SetApplicationVersion("1.0.0");
    config.AddExample("C:\\repos\\personal\\car-tracker");
    config.AddExample(".", "-s", "docs/specs", "-i", "30");
    config.AddExample("--once", "--format", "json");
    config.AddExample("--once", "--format", "md");
    config.AddExample("--fail-on", "planning,in-progress");
    config.AddExample("--min-progress", "80");
});
return await app.RunAsync(args);
