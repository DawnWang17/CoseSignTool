// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Calls the Azure Artifact Signing (AAS) MST proxy to register a COSE_Sign1 message with
/// Microsoft's Signing Transparency service.
/// </summary>
/// <remarks>
/// <para>
/// The AAS proxy fronts MST so that callers authorize with the same account and certificate profile
/// they already use for signing, rather than holding a separate MST credential. Registration is
/// therefore a single authenticated HTTP call whose body is the encoded COSE_Sign1 message.
/// </para>
/// <para>
/// The service may answer synchronously (a 2xx response carrying the transparent statement) or
/// asynchronously (a 202 response carrying an operation location, which this client polls until the
/// operation reaches a terminal state). Both shapes are handled so the CLI behaves identically
/// regardless of which the service chooses.
/// </para>
/// </remarks>
public sealed class AasMstProxyClient
{
    /// <summary>
    /// The media type used for raw COSE_Sign1 request and response bodies.
    /// </summary>
    internal const string CoseMediaType = "application/cose";

    /// <summary>
    /// The media type used when the account and profile travel in a JSON request body.
    /// </summary>
    internal const string JsonMediaType = "application/json";

    /// <summary>
    /// The request header carrying the AAS account name when using
    /// <see cref="AasMstParameterLocation.Header"/>.
    /// </summary>
    internal const string AccountNameHeader = "x-ms-codesigning-account-name";

    /// <summary>
    /// The request header carrying the AAS certificate profile name when using
    /// <see cref="AasMstParameterLocation.Header"/>.
    /// </summary>
    internal const string CertificateProfileNameHeader = "x-ms-codesigning-certificate-profile-name";

    /// <summary>
    /// The default delay applied between operation polls when the service does not send a
    /// <c>Retry-After</c> header.
    /// </summary>
    internal static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Response header names, in priority order, consulted to discover the polling location of an
    /// asynchronous registration operation.
    /// </summary>
    private static readonly string[] OperationLocationHeaders = new[] { "Operation-Location", "Azure-AsyncOperation", "Location" };

    /// <summary>
    /// JSON property names, checked case-insensitively, that may carry an MST entry identifier.
    /// </summary>
    private static readonly string[] EntryIdPropertyNames = new[] { "entryId", "entry_id", "id" };

    /// <summary>
    /// JSON status values that indicate an asynchronous operation has not yet finished.
    /// </summary>
    private static readonly string[] NonTerminalStatuses = new[] { "running", "pending", "notstarted", "inprogress", "accepted" };

    private readonly HttpClient HttpClient;
    private readonly IPluginLogger? Logger;
    private readonly TimeSpan PollingInterval;

    /// <summary>
    /// Initializes a new instance of the <see cref="AasMstProxyClient"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used to issue requests. The caller owns the client's lifetime, which allows
    /// tests to inject a stub handler and the CLI to reuse a single pooled client.
    /// </param>
    /// <param name="logger">An optional logger used for verbose progress reporting.</param>
    /// <param name="pollingInterval">
    /// The delay between operation polls when the service does not supply a <c>Retry-After</c>
    /// header. Defaults to <see cref="DefaultPollingInterval"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="httpClient"/> is null.</exception>
    public AasMstProxyClient(HttpClient httpClient, IPluginLogger? logger = null, TimeSpan? pollingInterval = null)
    {
        this.HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.Logger = logger;
        this.PollingInterval = pollingInterval ?? DefaultPollingInterval;
    }

    /// <summary>
    /// Registers a COSE_Sign1 message with MST through the AAS proxy.
    /// </summary>
    /// <param name="request">The registration request.</param>
    /// <param name="cancellationToken">
    /// A cancellation token. The caller is expected to bind the command's <c>--timeout</c> to this
    /// token so that both the initial call and any operation polling share a single deadline.
    /// </param>
    /// <returns>The registration result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    public async Task<AasMstRegisterResult> RegisterAsync(AasMstRegisterRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        using HttpRequestMessage httpRequest = BuildRegisterRequest(request);
        this.Logger?.LogVerbose($"POST {request.RequestUri}");

        using HttpResponseMessage response = await this.HttpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        AasMstRegisterResult result = await ReadResultAsync(response, cancellationToken).ConfigureAwait(false);

        // A 202 means MST accepted the statement but the receipt is not yet available. Follow the
        // operation location until the service reports a terminal state.
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            Uri? operationUri = ResolveOperationUri(response, request.RequestUri);
            if (operationUri is null)
            {
                this.Logger?.LogWarning(
                    "The service returned 202 Accepted but did not supply an operation location header, so the transparent statement could not be retrieved.");
                return result;
            }

            result = await this.PollOperationAsync(operationUri, request.AccessToken, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Builds the HTTP request for a registration call, placing the account and certificate profile
    /// names according to <see cref="AasMstRegisterRequest.ParameterLocation"/>.
    /// </summary>
    /// <param name="request">The registration request.</param>
    /// <returns>The constructed <see cref="HttpRequestMessage"/>.</returns>
    internal static HttpRequestMessage BuildRegisterRequest(AasMstRegisterRequest request)
    {
        HttpRequestMessage httpRequest = new(HttpMethod.Post, request.RequestUri);

        if (!string.IsNullOrWhiteSpace(request.AccessToken))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.AccessToken);
        }

        if (request.ParameterLocation == AasMstParameterLocation.Body)
        {
            // The COSE_Sign1 message is not valid UTF-8, so it is base64-encoded to travel inside JSON.
            string json = JsonSerializer.Serialize(new AasMstRegisterBody
            {
                AccountName = request.AccountName,
                CertificateProfileName = request.CertificateProfileName,
                Signature = Convert.ToBase64String(request.SignatureBytes)
            });

            httpRequest.Content = new StringContent(json, Encoding.UTF8, JsonMediaType);
        }
        else
        {
            ByteArrayContent content = new(request.SignatureBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(CoseMediaType);
            httpRequest.Content = content;

            if (request.ParameterLocation == AasMstParameterLocation.Header)
            {
                httpRequest.Headers.TryAddWithoutValidation(AccountNameHeader, request.AccountName);
                httpRequest.Headers.TryAddWithoutValidation(CertificateProfileNameHeader, request.CertificateProfileName);
            }
        }

        // Advertise that a transparent statement is the preferred response shape.
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(CoseMediaType));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

        return httpRequest;
    }

    /// <summary>
    /// Polls an asynchronous registration operation until it reaches a terminal state.
    /// </summary>
    /// <param name="operationUri">The operation location to poll.</param>
    /// <param name="accessToken">The bearer token to present, if any.</param>
    /// <param name="cancellationToken">A cancellation token bounding the total polling time.</param>
    /// <returns>The final registration result.</returns>
    private async Task<AasMstRegisterResult> PollOperationAsync(Uri operationUri, string? accessToken, CancellationToken cancellationToken)
    {
        this.Logger?.LogVerbose($"Registration accepted; polling operation at {operationUri}");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using HttpRequestMessage pollRequest = new(HttpMethod.Get, operationUri);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                pollRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            pollRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(CoseMediaType));
            pollRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

            using HttpResponseMessage pollResponse = await this.HttpClient
                .SendAsync(pollRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            AasMstRegisterResult pollResult = await ReadResultAsync(pollResponse, cancellationToken).ConfigureAwait(false);

            bool stillRunning = pollResponse.StatusCode == System.Net.HttpStatusCode.Accepted
                || (pollResult.IsSuccess && IsNonTerminalStatus(pollResult.ResponseBody));

            if (!stillRunning)
            {
                return pollResult;
            }

            TimeSpan delay = GetRetryAfter(pollResponse) ?? this.PollingInterval;
            this.Logger?.LogVerbose($"Operation still running; retrying in {delay.TotalMilliseconds:F0} ms");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads an HTTP response into an <see cref="AasMstRegisterResult"/>, preserving a COSE body as
    /// bytes and any other body as text.
    /// </summary>
    /// <param name="response">The response to read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The populated result.</returns>
    private static async Task<AasMstRegisterResult> ReadResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        AasMstRegisterResult result = new()
        {
            StatusCode = (int)response.StatusCode
        };

        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string? mediaType = response.Content.Headers.ContentType?.MediaType;

        if (body.Length > 0 && IsCoseMediaType(mediaType))
        {
            result.TransparentStatement = body;
        }
        else if (body.Length > 0)
        {
            result.ResponseBody = Encoding.UTF8.GetString(body);
            result.EntryId = TryReadJsonString(result.ResponseBody, EntryIdPropertyNames);
        }

        if (response.Headers.TryGetValues("x-ms-request-id", out IEnumerable<string>? requestIds))
        {
            foreach (string requestId in requestIds)
            {
                result.OperationId ??= requestId;
            }
        }

        return result;
    }

    /// <summary>
    /// Determines whether a media type denotes a COSE or CBOR payload.
    /// </summary>
    /// <param name="mediaType">The media type to inspect.</param>
    /// <returns><see langword="true"/> when the payload should be treated as binary COSE.</returns>
    private static bool IsCoseMediaType(string? mediaType)
    {
        return mediaType is not null
            && (mediaType.Contains("cose", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("cbor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the absolute URI used to poll an asynchronous operation.
    /// </summary>
    /// <param name="response">The 202 response.</param>
    /// <param name="requestUri">The original request URI, used to resolve relative locations.</param>
    /// <returns>The operation URI, or <see langword="null"/> when the service supplied none.</returns>
    private static Uri? ResolveOperationUri(HttpResponseMessage response, Uri requestUri)
    {
        foreach (string headerName in OperationLocationHeaders)
        {
            if (!response.Headers.TryGetValues(headerName, out IEnumerable<string>? values))
            {
                continue;
            }

            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (Uri.TryCreate(requestUri, value.Trim(), out Uri? resolved))
                {
                    return resolved;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>Retry-After</c> header as a delay, supporting both delta-seconds and HTTP-date forms.
    /// </summary>
    /// <param name="response">The response to inspect.</param>
    /// <returns>The requested delay, or <see langword="null"/> when the header is absent or in the past.</returns>
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta.HasValue)
        {
            return retryAfter.Delta.Value > TimeSpan.Zero ? retryAfter.Delta.Value : null;
        }

        if (retryAfter.Date.HasValue)
        {
            TimeSpan delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : null;
        }

        return null;
    }

    /// <summary>
    /// Determines whether a JSON operation document reports a non-terminal status.
    /// </summary>
    /// <param name="json">The response body, which may be null or non-JSON.</param>
    /// <returns><see langword="true"/> when the operation is still running.</returns>
    private static bool IsNonTerminalStatus(string? json)
    {
        string? status = TryReadJsonString(json, new[] { "status" });
        if (status is null)
        {
            return false;
        }

        string normalized = status.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        return Array.Exists(NonTerminalStatuses, s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the first matching top-level string property from a JSON document.
    /// </summary>
    /// <param name="json">The JSON text, which may be null or malformed.</param>
    /// <param name="propertyNames">The candidate property names, matched case-insensitively.</param>
    /// <returns>The property value, or <see langword="null"/> when not found.</returns>
    private static string? TryReadJsonString(string? json, string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (Array.Exists(propertyNames, name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // The body was not JSON. Callers treat this as "no structured data available".
        }

        return null;
    }

    /// <summary>
    /// The JSON request body used by <see cref="AasMstParameterLocation.Body"/>.
    /// </summary>
    private sealed class AasMstRegisterBody
    {
        /// <summary>
        /// Gets or sets the AAS account name.
        /// </summary>
        public string AccountName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the AAS certificate profile name.
        /// </summary>
        public string CertificateProfileName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the base64-encoded COSE_Sign1 message.
        /// </summary>
        public string Signature { get; set; } = string.Empty;
    }
}
