using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages;

public class IndexModel(SessionLogReader reader, ObservabilityPaths paths) : PageModel
{
    public IReadOnlyList<SessionSummary> Sessions { get; private set; } = [];

    public void OnGet() => Sessions = reader.ListSessions(paths.SessionsDir);
}
