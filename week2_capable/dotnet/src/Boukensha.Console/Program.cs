using Boukensha.Core;
using Boukensha.Console;
using Boukensha.Console.Tui;

const string Version = "0.1.0";

var noTui = args.Contains("--no-tui") || Environment.GetEnvironmentVariable("BOUKENSHA_TUI") == "0";

await using var session = await BoukenshaHost.BuildAsync(new BoukenshaOptions());
var config = new Config();
var repl = new Repl(session, session.Provider, session.Model, Version, config.Dir, session.McpServerNames);

if (noTui)
{
    await repl.StartAsync();
}
else
{
    using var tui = new TuiOutputSink(session.Context);
    tui.Start();
    session.Logger.Subscribe(tui.OnLogEvent);
    repl.OnOutput(tui.Output);
    await repl.StartAsync();
}
