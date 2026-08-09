using Boukensha.Observability;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Boukensha.Observability.Pages.Knowledge;

public class ChangesModel(ChangeLogReader reader, ObservabilityPaths paths) : PageModel
{
    public IReadOnlyList<ChangeEntry> Changes { get; private set; } = [];

    public void OnGet() => Changes = reader.ReadEntries(paths.ChangeLogPath).OrderByDescending(c => c.At).ToList();
}
