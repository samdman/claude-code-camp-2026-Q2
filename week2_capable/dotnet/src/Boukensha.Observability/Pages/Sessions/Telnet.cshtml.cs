using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Sessions;

public class TelnetModel(SessionLogReader sessionReader, TelnetLogReader telnetReader, ObservabilityPaths paths) : PageModel
{
    public string SessionId { get; private set; } = string.Empty;
    public IReadOnlyList<TelnetEntry> Entries { get; private set; } = [];

    public IActionResult OnGet(string id)
    {
        var filePath = Path.Combine(paths.SessionsDir, $"{id}.jsonl");
        if (!System.IO.File.Exists(filePath)) return NotFound();

        SessionId = id;
        var events = sessionReader.ReadEvents(filePath);
        if (events.Count == 0)
        {
            Entries = [];
            return Page();
        }

        var start = events[0].At;
        var end = events[^1].At;
        Entries = telnetReader.ReadEntries(paths.TelnetLogPath)
            .Where(e => e.At >= start && e.At <= end)
            .ToList();
        return Page();
    }
}
