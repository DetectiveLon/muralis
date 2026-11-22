using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Muralis.Models;

namespace Muralis.Services.Sources;

/// <summary>
/// Generic JSON wallpaper source. Responses are streamed and capped before parsing so a
/// misconfigured endpoint cannot allocate unbounded memory.
/// </summary>
public class HttpJsonSource : IWallpaperSource
{
    internal const int MaxJsonResponseBytes = 2 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly WallpaperSourceConfig _config;
    private readonly string _apiKey;

    /// <param name="apiKey">Decrypted key resolved by the caller. Keys are never read from
    /// the persisted source model as plaintext.</param>
    public HttpJsonSource(HttpClient http, WallpaperSourceConfig config, string? apiKey = null)
    {
        _http = http;
        _config = config;
        _apiKey = apiKey ?? string.Empty;
    }

    public string Name => _config.Name;

    public async Task<Uri> GetImageUrlAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(_config.RequestUrl, UriKind.Absolute, out var requestUri)
            || requestUri.Scheme is not (Uri.UriSchemeHttp or Uri.UriSchemeHttps))
            throw new InvalidOperationException("Source URL must use HTTP or HTTPS.");

        bool hasApiKey = !string.IsNullOrWhiteSpace(_config.ApiKeyHeader)
            && !string.IsNullOrWhiteSpace(_apiKey);
        if (hasApiKey && requestUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("API keys may only be sent to HTTPS endpoints.");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (hasApiKey && !request.Headers.TryAddWithoutValidation(_config.ApiKeyHeader, _apiKey))
            throw new InvalidOperationException("The configured API-key header is invalid.");

        // The application supplies a no-redirect client for authenticated requests. This
        // prevents custom API-key headers from being forwarded to a redirect destination.
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long length && length > MaxJsonResponseBytes)
            throw new InvalidOperationException($"JSON response exceeds the {MaxJsonResponseBytes}-byte limit.");

        string json = await ReadLimitedAsync(response.Content, ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        string? imageUrl = Navigate(document.RootElement, _config.ImageUrlJsonPath).GetString();
        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new InvalidOperationException($"JSON path '{_config.ImageUrlJsonPath}': empty URL.");

        var resolved = new Uri(requestUri, imageUrl);
        if (resolved.Scheme is not (Uri.UriSchemeHttp or Uri.UriSchemeHttps))
            throw new InvalidOperationException("Image URL must use HTTP or HTTPS.");
        return resolved;
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var input = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxJsonResponseBytes)
                throw new InvalidOperationException($"JSON response exceeds the {MaxJsonResponseBytes}-byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    /// <summary>
    /// Minimal JSON navigation: dot-separated properties and array indices in brackets.
    /// Examples: <c>images[0].url</c>, <c>[0].file_url</c>, <c>urls.full</c>.
    /// </summary>
    internal static JsonElement Navigate(JsonElement element, string path)
    {
        var current = element;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string rest = segment;
            while (rest.Length > 0)
            {
                int bracket = rest.IndexOf('[');
                string name = bracket < 0 ? rest : rest[..bracket];

                if (name.Length > 0)
                {
                    if (!current.TryGetProperty(name, out current))
                        throw new InvalidOperationException($"JSON property '{name}' not found (path '{path}').");
                    rest = bracket < 0 ? string.Empty : rest[bracket..];
                    continue;
                }

                int close = rest.IndexOf(']');
                if (close < 2 || !int.TryParse(rest[1..close], out int index) || index < 0)
                    throw new InvalidOperationException($"Invalid array index in '{path}'.");
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                    throw new InvalidOperationException($"Index [{index}] out of range (path '{path}').");

                current = current[index];
                rest = rest[(close + 1)..];
            }
        }
        return current;
    }
}