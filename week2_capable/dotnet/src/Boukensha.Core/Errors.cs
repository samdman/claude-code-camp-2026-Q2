namespace Boukensha.Core;

public sealed class UnknownToolException(string message) : Exception(message);

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
    public ApiException(string message, Exception inner) : base(message, inner) { }
}

public sealed class LoopException(string message) : Exception(message);

public sealed class UnsupportedModelException(string message) : Exception(message);
