using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Boukensha.Core;

public sealed class Client(PromptBuilder builder, HttpClient httpClient)
{
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
    [
        HttpStatusCode.RequestTimeout, HttpStatusCode.Conflict, (HttpStatusCode)429,
        HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout,
    ];

    private const int MaxRetries = 3;
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(500);

    public async Task<JsonNode> CallAsync(int maxOutputTokens = 1024, JsonArray? tools = null, CancellationToken cancellationToken = default)
    {
        var payload = builder.ToApiPayload(maxOutputTokens, tools);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxRetries + 1; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, builder.Url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                foreach (var (key, value) in builder.Headers)
                {
                    request.Headers.TryAddWithoutValidation(key, value);
                }
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                lastError = e;
                if (attempt > MaxRetries) throw new ApiException($"request failed after {attempt} attempts: {e.Message}", e);
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            if (RetryableStatusCodes.Contains(response.StatusCode) && attempt <= MaxRetries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay(attempt), cancellationToken);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiException($"request failed after {attempt} attempt(s): {(int)response.StatusCode} {body}");
            }
            return JsonNode.Parse(body) ?? throw new ApiException("received empty response body");
        }

        throw new ApiException($"request failed after {MaxRetries + 1} attempts", lastError ?? new InvalidOperationException("unknown error"));
    }

    private static TimeSpan RetryDelay(int attempt) => BaseRetryDelay * Math.Pow(2, attempt - 1);
}
