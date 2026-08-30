// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CoseSignTool.AasMst.Plugin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="AasMstProxyClient"/>.
/// </summary>
[TestClass]
public class AasMstProxyClientTests
{
    private static readonly Uri RegisterUri = new("https://wus.codesigning.azure.net/mstregister");
    private static readonly byte[] SignatureBytes = new byte[] { 0xD2, 0x84, 0x43, 0xA1, 0x01, 0x26 };
    private const string Account = "testwus";
    private const string Profile = "testWusCert1";
    private const string Token = "fake-token";

    /// <summary>
    /// Creates a register request with the supplied parameter location.
    /// </summary>
    /// <param name="location">Where the account and profile names should travel.</param>
    /// <returns>The request.</returns>
    private static AasMstRegisterRequest CreateRequest(AasMstParameterLocation location = AasMstParameterLocation.Body)
    {
        return new AasMstRegisterRequest
        {
            RequestUri = RegisterUri,
            AccountName = Account,
            CertificateProfileName = Profile,
            SignatureBytes = SignatureBytes,
            ParameterLocation = location,
            AccessToken = Token
        };
    }

    /// <summary>
    /// Creates a COSE response body.
    /// </summary>
    /// <param name="bytes">The bytes to return.</param>
    /// <returns>The content.</returns>
    private static ByteArrayContent CoseContent(byte[] bytes)
    {
        ByteArrayContent content = new(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(AasMstProxyClient.CoseMediaType);
        return content;
    }

    /// <summary>
    /// Body placement sends JSON carrying the account, profile, and base64 signature.
    /// </summary>
    [TestMethod]
    public void BuildRegisterRequest_WithBodyLocation_SendsJsonWithAccountAndProfile()
    {
        using HttpRequestMessage request = AasMstProxyClient.BuildRegisterRequest(CreateRequest(AasMstParameterLocation.Body));

        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(AasMstProxyClient.JsonMediaType, request.Content!.Headers.ContentType!.MediaType);

        string json = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.AreEqual(Account, document.RootElement.GetProperty("AccountName").GetString());
        Assert.AreEqual(Profile, document.RootElement.GetProperty("CertificateProfileName").GetString());
        Assert.AreEqual(Convert.ToBase64String(SignatureBytes), document.RootElement.GetProperty("Signature").GetString());
    }

    /// <summary>
    /// Header placement sends the raw COSE bytes and the account/profile headers.
    /// </summary>
    [TestMethod]
    public void BuildRegisterRequest_WithHeaderLocation_SendsRawCoseAndHeaders()
    {
        using HttpRequestMessage request = AasMstProxyClient.BuildRegisterRequest(CreateRequest(AasMstParameterLocation.Header));

        Assert.AreEqual(AasMstProxyClient.CoseMediaType, request.Content!.Headers.ContentType!.MediaType);
        CollectionAssert.AreEqual(SignatureBytes, request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        Assert.AreEqual(Account, request.Headers.GetValues(AasMstProxyClient.AccountNameHeader).Single());
        Assert.AreEqual(Profile, request.Headers.GetValues(AasMstProxyClient.CertificateProfileNameHeader).Single());
    }

    /// <summary>
    /// Path placement sends the raw COSE bytes and does not duplicate the values into headers.
    /// </summary>
    [TestMethod]
    public void BuildRegisterRequest_WithPathLocation_SendsRawCoseWithoutHeaders()
    {
        using HttpRequestMessage request = AasMstProxyClient.BuildRegisterRequest(CreateRequest(AasMstParameterLocation.Path));

        Assert.AreEqual(AasMstProxyClient.CoseMediaType, request.Content!.Headers.ContentType!.MediaType);
        Assert.IsFalse(request.Headers.Contains(AasMstProxyClient.AccountNameHeader));
        Assert.IsFalse(request.Headers.Contains(AasMstProxyClient.CertificateProfileNameHeader));
    }

    /// <summary>
    /// The access token is presented as a bearer token.
    /// </summary>
    [TestMethod]
    public void BuildRegisterRequest_WithAccessToken_SetsBearerAuthorization()
    {
        using HttpRequestMessage request = AasMstProxyClient.BuildRegisterRequest(CreateRequest());

        Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
        Assert.AreEqual(Token, request.Headers.Authorization.Parameter);
    }

    /// <summary>
    /// No token means no authorization header, rather than an empty one.
    /// </summary>
    [TestMethod]
    public void BuildRegisterRequest_WithoutAccessToken_OmitsAuthorization()
    {
        AasMstRegisterRequest registerRequest = CreateRequest();
        registerRequest.AccessToken = null;

        using HttpRequestMessage request = AasMstProxyClient.BuildRegisterRequest(registerRequest);

        Assert.IsNull(request.Headers.Authorization);
    }

    /// <summary>
    /// A synchronous 200 response carrying COSE yields the transparent statement.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithSynchronousCoseResponse_ReturnsTransparentStatement()
    {
        byte[] statement = new byte[] { 0xD2, 0x84, 0x01, 0x02 };
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, CoseContent(statement));
        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient);

        AasMstRegisterResult result = await client.RegisterAsync(CreateRequest());

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(200, result.StatusCode);
        CollectionAssert.AreEqual(statement, result.TransparentStatement);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    /// <summary>
    /// A 202 response is followed to its operation location until a terminal response arrives.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithAcceptedResponse_PollsOperationLocation()
    {
        byte[] statement = new byte[] { 0xD2, 0x84, 0x0A, 0x0B };
        using StubHttpMessageHandler handler = new(
            _ =>
            {
                HttpResponseMessage accepted = new(HttpStatusCode.Accepted);
                accepted.Headers.Add("Operation-Location", "https://wus.codesigning.azure.net/operations/abc123");
                accepted.Headers.Add("Retry-After", "0");
                return accepted;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = CoseContent(statement) });

        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient, logger: null, pollingInterval: TimeSpan.Zero);

        AasMstRegisterResult result = await client.RegisterAsync(CreateRequest());

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(statement, result.TransparentStatement);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
        Assert.AreEqual("https://wus.codesigning.azure.net/operations/abc123", handler.Requests[1].RequestUri!.ToString());
        Assert.AreEqual(Token, handler.Requests[1].AuthorizationParameter);
    }

    /// <summary>
    /// A relative operation location is resolved against the register URI.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithRelativeOperationLocation_ResolvesAgainstRequestUri()
    {
        using StubHttpMessageHandler handler = new(
            _ =>
            {
                HttpResponseMessage accepted = new(HttpStatusCode.Accepted);
                accepted.Headers.Add("Location", "/operations/xyz");
                return accepted;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = CoseContent(new byte[] { 0xD2 }) });

        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient, logger: null, pollingInterval: TimeSpan.Zero);

        await client.RegisterAsync(CreateRequest());

        Assert.AreEqual("https://wus.codesigning.azure.net/operations/xyz", handler.Requests[1].RequestUri!.ToString());
    }

    /// <summary>
    /// A 202 with no operation location returns rather than looping forever.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithAcceptedAndNoLocation_ReturnsWithoutPolling()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(HttpStatusCode.Accepted);
        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient, logger: null, pollingInterval: TimeSpan.Zero);

        AasMstRegisterResult result = await client.RegisterAsync(CreateRequest());

        Assert.AreEqual(202, result.StatusCode);
        Assert.IsNull(result.TransparentStatement);
        Assert.AreEqual(1, handler.Requests.Count);
    }

    /// <summary>
    /// A JSON operation document reporting a running status keeps polling until it reports success.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithRunningJsonStatus_KeepsPolling()
    {
        byte[] statement = new byte[] { 0xD2, 0x84 };
        using StubHttpMessageHandler handler = new(
            _ =>
            {
                HttpResponseMessage accepted = new(HttpStatusCode.Accepted);
                accepted.Headers.Add("Operation-Location", "https://wus.codesigning.azure.net/operations/abc");
                return accepted;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"running\"}", Encoding.UTF8, AasMstProxyClient.JsonMediaType)
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = CoseContent(statement) });

        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient, logger: null, pollingInterval: TimeSpan.Zero);

        AasMstRegisterResult result = await client.RegisterAsync(CreateRequest());

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(statement, result.TransparentStatement);
        Assert.AreEqual(3, handler.Requests.Count);
    }

    /// <summary>
    /// A JSON success response exposes the entry identifier and the raw body for diagnostics.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithJsonEntryId_CapturesEntryId()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.OK,
            new StringContent("{\"entryId\":\"4.32\"}", Encoding.UTF8, AasMstProxyClient.JsonMediaType));
        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient);

        AasMstRegisterResult result = await client.RegisterAsync(CreateRequest());

        Assert.AreEqual("4.32", result.EntryId);
        Assert.IsNull(result.TransparentStatement);
    }

    /// <summary>
    /// A failure status is reported with the response body preserved for the error message.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithErrorStatus_ReportsFailureAndBody()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            HttpStatusCode.Forbidden,
            new StringContent("{\"error\":\"forbidden\"}", Encoding.UTF8, AasMstProxyClient.JsonMediaType));
        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient);

        AasMstRegisterResult result = await client.RegisterAsync(CreateRequest());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(403, result.StatusCode);
        StringAssert.Contains(result.ResponseBody, "forbidden");
    }

    /// <summary>
    /// A null request is rejected.
    /// </summary>
    [TestMethod]
    public async Task RegisterAsync_WithNullRequest_Throws()
    {
        using StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK);
        using HttpClient httpClient = new(handler);
        AasMstProxyClient client = new(httpClient);

        await Assert.ThrowsExceptionAsync<ArgumentNullException>(() => client.RegisterAsync(null!));
    }

    /// <summary>
    /// A null HTTP client is rejected at construction.
    /// </summary>
    [TestMethod]
    public void Constructor_WithNullHttpClient_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new AasMstProxyClient(null!));
    }
}
