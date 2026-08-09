using Boukensha.Core;

namespace Boukensha.Console;

public sealed class Repl(
    BoukenshaSession session,
    string provider,
    string model,
    string version,
    string configDir,
    IReadOnlyList<string> mcpServerNames)
{
    private const string Prompt = "boukensha> ";
    private static readonly string Help = string.Join('\n',
        "Commands:",
        "  /help     show this message",
        "  /clear    clear the conversation",
        "  /compact  manually compact the context",
        "  /exit     quit (also /quit)");

    private int _turn;
    private Action<string>? _outputSink;

    public void OnOutput(Action<string> sink) => _outputSink = sink;

    public string Banner()
    {
        var configStatus = Directory.Exists(configDir) ? "found" : "missing";
        var servers = mcpServerNames.Count > 0 ? string.Join(", ", mcpServerNames) : "(none configured)";
        return string.Join('\n',
            $"boukensha v{version}",
            $"config: {configDir} ({configStatus})",
            $"provider/model: {provider}/{model}",
            $"mcp servers: {servers}");
    }

    public string? HandleCommand(string input) => input switch
    {
        "/exit" or "/quit" => Quit(),
        "/help" => Command(Help),
        "/clear" => ClearContext(),
        "/compact" => CompactContext(),
        _ => null,
    };

    private string Quit()
    {
        Output("Goodbye.");
        return "quit";
    }

    private string Command(string text)
    {
        Output(text);
        return "command";
    }

    private string ClearContext()
    {
        session.Context.ClearMessages();
        _turn = 0;
        return Command("(cleared)");
    }

    private string CompactContext()
    {
        var dropped = session.Context.CompactMessages();
        return Command($"(compacted context — {dropped} messages dropped)");
    }

    public async Task RunTurnAsync(string input)
    {
        _turn++;
        session.Logger.Turn(_turn);
        session.Context.AddMessage("user", input);
        var agent = session.AgentFactory();
        try
        {
            var result = await agent.RunAsync();
            Output(string.Empty);
            Output(result);
        }
        catch (Exception e) when (e is ApiException or LoopException)
        {
            Output($"[error] {e.Message}");
        }
    }

    public async Task StartAsync()
    {
        Output(Banner());
        while (true)
        {
            if (_outputSink is null) System.Console.Write(Prompt);
            var line = System.Console.ReadLine();
            if (line is null) break;

            line = line.Trim();
            if (line.Length == 0) continue;

            var commandResult = HandleCommand(line);
            if (commandResult == "quit") break;
            if (commandResult is not null) continue;

            await RunTurnAsync(line);
        }
    }

    private void Output(string text)
    {
        if (_outputSink is not null) _outputSink(text);
        else System.Console.WriteLine(text);
    }
}
