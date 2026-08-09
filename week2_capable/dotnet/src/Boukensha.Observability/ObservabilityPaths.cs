namespace Boukensha.Observability;

public sealed record ObservabilityPaths(string SessionsDir, string KnowledgeDbPath, string ChangeLogPath, string TelnetLogPath);
