using System.Net;
using System.Net.Http;
using Muralis.Models;
using Muralis.Services.Sources;

namespace Muralis.Core.Tests;

public sealed class HttpJsonSourceSafetyTests
{
    [Fact]
    public async Task OversizedJsonResponse_IsRejected()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[2 * 1024 * 1024 + 1]),
        });
        using var client = new HttpClient(handler);
        var source = Source("https://api.example.test/json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new HttpJsonSource(client, source).GetImageUrlAsync(CancellationToken.None));

        Assert.Contains("exceeds", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiKey_IsNeverSentOverHttp()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Request should not be sent."));
        using var client = new HttpClient(handler);
        var source = Source("http://api.example.test/json");
        source.ApiKeyHeader = "X-API-Key";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new HttpJsonSource(client, source, "secret").GetImageUrlAsync(CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AuthenticatedRedirect_ToDifferentOrigin_IsRejected()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://other.example.test/steal") },
        });
        using var client = new HttpClient(handler);
        var source = Source("https://api.example.test/json");
        source.ApiKeyHeader = "X-API-Key";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new HttpJsonSource(client, source, "secret").GetImageUrlAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task AuthenticatedSameOriginRedirect_IsFollowed()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/json")
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("/final", UriKind.Relative) },
                };

            Assert.Equal("secret", request.Headers.GetValues("X-API-Key").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "url": "/wallpaper.jpg" }"""),
            };
        });
        using var client = new HttpClient(handler);
        var source = Source("https://api.example.test/json");
        source.ApiKeyHeader = "X-API-Key";

        var result = await new HttpJsonSource(client, source, "secret").GetImageUrlAsync(CancellationToken.None);

        Assert.Equal("https://api.example.test/wallpaper.jpg", result.AbsoluteUri);
        Assert.Equal(2, handler.RequestCount);
    }

    private static WallpaperSourceConfig Source(string url) => new()
    {
        Id = "custom-source",
        Name = "Test",
        RequestUrl = url,
        ImageUrlJsonPath = "url",
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }
}
