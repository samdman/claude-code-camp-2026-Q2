using System.Text.Json;
using Boukensha.Core;
using Xunit;

namespace Boukensha.Core.Tests;

public class LoggerTests
{
    private static (Logger Logger, string Path) NewLogger()
    {
        var dir = Directory.CreateTempSubdirectory("boukensha_logger_test").FullName;
        var logger = new Logger(dir, sessionId: "sess-1");
        return (logger, logger.Path);
    }

    private static List<Dictionary<string, JsonElement>> ReadEvents(string path) =>
        File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line)!)
            .ToList();

    [Fact]
    public void Response_IncludesDurationMs()
    {
        var (logger, path) = NewLogger();
        logger.Response("hello", usage: null, stopReason: "end_turn", task: "player", backend: "anthropic", costUsd: null, durationMs: 1234);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal(1234, evt["duration_ms"].GetInt32());
    }

    [Fact]
    public void ToolCall_IncludesTask()
    {
        var (logger, path) = NewLogger();
        logger.ToolCall("move", new Dictionary<string, object?> { ["direction"] = "south" }, "player");
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("player", evt["task"].GetString());
    }

    [Fact]
    public void ExplorationStep_IncludesConfidenceAndExploredFlag()
    {
        var (logger, path) = NewLogger();
        logger.ExplorationStep(step: 1, roomId: 7, direction: "east", hint: "Bakery", confidence: 1.0, explored: true);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("exploration_step", evt["phase"].GetString());
        Assert.Equal(7, evt["room_id"].GetInt32());
        Assert.Equal("east", evt["direction"].GetString());
        Assert.Equal("Bakery", evt["hint"].GetString());
        Assert.Equal(1.0, evt["confidence"].GetDouble());
        Assert.True(evt["explored"].GetBoolean());
    }

    [Fact]
    public void ExplorationRetreat_IncludesReasonAndRecalledFlag()
    {
        var (logger, path) = NewLogger();
        logger.ExplorationRetreat(reason: "stuck", stepsUsed: 3, discovered: 2, frontiersRemaining: 4, recalled: true);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("exploration_retreat", evt["phase"].GetString());
        Assert.Equal("stuck", evt["reason"].GetString());
        Assert.Equal(3, evt["steps_used"].GetInt32());
        Assert.Equal(2, evt["discovered"].GetInt32());
        Assert.Equal(4, evt["frontiers_remaining"].GetInt32());
        Assert.True(evt["recalled"].GetBoolean());
    }

    [Fact]
    public void ToolResult_IncludesTaskAndDurationMs()
    {
        var (logger, path) = NewLogger();
        logger.ToolResult("move", "You walk south.", task: "player", durationMs: 42);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("player", evt["task"].GetString());
        Assert.Equal(42, evt["duration_ms"].GetInt32());
    }

    [Fact]
    public void ToolCatalog_ListsToolNameDescriptionAndParameters()
    {
        var (logger, path) = NewLogger();
        var tools = new Dictionary<string, ToolDefinition>
        {
            ["look"] = new("look", "Look around", new Dictionary<string, ToolParameter>
            {
                ["target"] = new("string", "what to look at"),
            }, _ => Task.FromResult("")),
        };
        logger.ToolCatalog(tools);
        logger.Dispose();

        var evt = ReadEvents(path).Last();
        Assert.Equal("tool_catalog", evt["phase"].GetString());
        var toolsArray = evt["tools"];
        Assert.Equal(1, toolsArray.GetArrayLength());
        Assert.Equal("look", toolsArray[0].GetProperty("name").GetString());
    }

    [Fact]
    public void GenerateSessionId_IsPubliclyAccessible()
    {
        var id = Logger.GenerateSessionId();
        Assert.NotEmpty(id);
    }
}
