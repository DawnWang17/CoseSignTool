// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin.Tests;

using System.Net;
using System.Net.Http;

/// <summary>
/// An <see cref="HttpMessageHandler"/> that returns a scripted sequence of responses and records the
/// requests it received, so proxy behaviour can be asserted without a live service.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> Responders;

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHttpMessageHandler"/> class.
    /// </summary>
    /// <param name="responders">
    /// The responses to return, in order. The final responder is reused if more requests arrive than
    /// there are responders.
    /// </param>
    public StubHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        this.Responders = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responders);
        this.Requests = new List<RecordedRequest>();
    }

    /// <summary>
    /// Gets the requests observed by this handler, in order.
    /// </summary>
    public List<RecordedRequest> Requests { get; }

    /// <summary>
    /// Creates a handler that always returns the supplied response.
    /// </summary>
    /// <param name="statusCode">The status code to return.</param>
    /// <param name="content">The response content, or null for an empty body.</param>
    /// <returns>The configured handler.</returns>
    public static StubHttpMessageHandler Returning(HttpStatusCode statusCode, HttpContent? content = null)
    {
        return new StubHttpMessageHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = content ?? new ByteArrayContent(Array.Empty<byte>())
        });
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[] body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        this.Requests.Add(new RecordedRequest
        {
            Method = request.Method,
            RequestUri = request.RequestUri,
            ContentType = request.Content?.Headers.ContentType?.MediaType,
            AuthorizationScheme = request.Headers.Authorization?.Scheme,
            AuthorizationParameter = request.Headers.Authorization?.Parameter,
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            Body = body
        });

        Func<HttpRequestMessage, HttpResponseMessage> responder = this.Responders.Count > 1
            ? this.Responders.Dequeue()
            : this.Responders.Peek();

        return responder(request);
    }

    /// <summary>
    /// A request captured by <see cref="StubHttpMessageHandler"/>.
    /// </summary>
    internal sealed class RecordedRequest
    {
        /// <summary>
        /// Gets or sets the HTTP method.
        /// </summary>
        public HttpMethod? Method { get; set; }

        /// <summary>
        /// Gets or sets the request URI.
        /// </summary>
        public Uri? RequestUri { get; set; }

        /// <summary>
        /// Gets or sets the request content media type.
        /// </summary>
        public string? ContentType { get; set; }

        /// <summary>
        /// Gets or sets the authorization scheme, for example "Bearer".
        /// </summary>
        public string? AuthorizationScheme { get; set; }

        /// <summary>
        /// Gets or sets the authorization parameter, that is, the token.
        /// </summary>
        public string? AuthorizationParameter { get; set; }

        /// <summary>
        /// Gets or sets the request headers, excluding content headers.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the request body bytes.
        /// </summary>
        public byte[] Body { get; set; } = Array.Empty<byte>();
    }
}
