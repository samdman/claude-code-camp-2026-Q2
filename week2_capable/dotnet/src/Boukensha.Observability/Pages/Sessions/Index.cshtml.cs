using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Sessions;

public class IndexModel(SessionLogReader reader, ObservabilityPaths paths) : PageModel
{
    public string SessionId { get; private set; } = string.Empty;
    public IReadOnlyList<SessionEvent> Events { get; private set; } = [];
    public IReadOnlyList<SessionEvent> SlowestFirst => Events
        .Where(e => e.Raw["duration_ms"] is not null)
        .OrderByDescending(e => e.Raw["duration_ms"]!.GetValue<int>())
        .ToList();

    public IActionResult OnGet(string id)
    {
        var filePath = Path.Combine(paths.SessionsDir, $"{id}.jsonl");
        if (!System.IO.File.Exists(filePath)) return NotFound();

        SessionId = id;
        Events = reader.ReadEvents(filePath);
        return Page();
    }
}
