using Boukensha.Core;
using Boukensha.Core.Knowledge;
using Boukensha.Observability;

var builder = WebApplication.CreateBuilder(args);

var config = new Config();
var paths = new ObservabilityPaths(
    Path.Combine(config.Dir, "sessions"),
    Path.Combine(config.Dir, "knowledge.db"),
    Path.Combine(config.Dir, "knowledge_changes.jsonl"),
    Path.Combine(config.Dir, "telnet.jsonl"));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<SessionLogReader>();
builder.Services.AddSingleton<TelnetLogReader>();
builder.Services.AddSingleton<ChangeLogReader>();
builder.Services.AddSingleton<JourneyReader>();
builder.Services.AddScoped(_ => new KnowledgeStore(paths.KnowledgeDbPath));
builder.Services.AddRazorPages();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.MapGet("/api/live", (SessionLogReader sessionReader, KnowledgeStore knowledge, ObservabilityPaths obsPaths) =>
{
    var latestSession = sessionReader.ListSessions(obsPaths.SessionsDir).FirstOrDefault();
    var recentEvents = latestSession is not null
        ? sessionReader.ReadEvents(latestSession.FilePath).TakeLast(10).ToList()
        : [];
    var currentRoom = knowledge.GetCurrentRoom();
    var exits = currentRoom is not null ? knowledge.ListExits(currentRoom.Id) : [];

    return Results.Json(new
    {
        session = latestSession,
        recentEvents = recentEvents.Select(e => new { e.Phase, at = e.At }),
        currentRoom,
        exits,
    });
});

app.Run();
