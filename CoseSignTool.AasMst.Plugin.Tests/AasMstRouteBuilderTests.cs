// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin.Tests;

using CoseSignTool.AasMst.Plugin;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="AasMstRouteBuilder"/>.
/// </summary>
[TestClass]
public class AasMstRouteBuilderTests
{
    private const string Endpoint = "https://wus.codesigning.azure.net";
    private const string Account = "testwus";
    private const string Profile = "testWusCert1";

    /// <summary>
    /// A null or whitespace path template falls back to the documented default route.
    /// </summary>
    /// <param name="pathTemplate">The template supplied by the caller.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Build_WithNoPathTemplate_UsesDefaultRegisterPath(string? pathTemplate)
    {
        Uri result = AasMstRouteBuilder.Build(Endpoint, pathTemplate, Account, Profile, null);

        Assert.AreEqual("https://wus.codesigning.azure.net/mstregister", result.ToString());
    }

    /// <summary>
    /// A leading slash on the template is optional and does not produce a doubled separator.
    /// </summary>
    /// <param name="pathTemplate">The template supplied by the caller.</param>
    [TestMethod]
    [DataRow("mstregister")]
    [DataRow("/mstregister")]
    public void Build_WithOrWithoutLeadingSlash_ProducesSameUri(string pathTemplate)
    {
        Uri result = AasMstRouteBuilder.Build(Endpoint, pathTemplate, Account, Profile, null);

        Assert.AreEqual("https://wus.codesigning.azure.net/mstregister", result.ToString());
    }

    /// <summary>
    /// The account and profile placeholders are substituted, case-insensitively.
    /// </summary>
    /// <param name="pathTemplate">The template containing placeholders.</param>
    [TestMethod]
    [DataRow("codesigningaccounts/{account}/certificateprofiles/{profile}/mstregister")]
    [DataRow("codesigningaccounts/{Account}/certificateprofiles/{PROFILE}/mstregister")]
    public void Build_WithPlaceholders_SubstitutesAccountAndProfile(string pathTemplate)
    {
        Uri result = AasMstRouteBuilder.Build(Endpoint, pathTemplate, Account, Profile, null);

        Assert.AreEqual(
            "https://wus.codesigning.azure.net/codesigningaccounts/testwus/certificateprofiles/testWusCert1/mstregister",
            result.ToString());
    }

    /// <summary>
    /// An API version is appended as a query parameter when supplied.
    /// </summary>
    [TestMethod]
    public void Build_WithApiVersion_AppendsQueryParameter()
    {
        Uri result = AasMstRouteBuilder.Build(Endpoint, "mstregister", Account, Profile, "2023-06-15-preview");

        Assert.AreEqual("https://wus.codesigning.azure.net/mstregister?api-version=2023-06-15-preview", result.ToString());
    }

    /// <summary>
    /// A null or whitespace API version leaves the query string empty.
    /// </summary>
    /// <param name="apiVersion">The API version supplied by the caller.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void Build_WithoutApiVersion_OmitsQueryString(string? apiVersion)
    {
        Uri result = AasMstRouteBuilder.Build(Endpoint, "mstregister", Account, Profile, apiVersion);

        Assert.AreEqual(string.Empty, result.Query);
    }

    /// <summary>
    /// A path prefix on the endpoint is preserved rather than replaced by the register path.
    /// </summary>
    [TestMethod]
    public void Build_WithEndpointPathPrefix_PreservesPrefix()
    {
        Uri result = AasMstRouteBuilder.Build("https://gateway.example.com/aas/", "mstregister", Account, Profile, null);

        Assert.AreEqual("https://gateway.example.com/aas/mstregister", result.ToString());
    }

    /// <summary>
    /// Account and profile values are URL-escaped so reserved characters cannot alter the route.
    /// </summary>
    [TestMethod]
    public void Build_WithReservedCharactersInAccount_EscapesThem()
    {
        Uri result = AasMstRouteBuilder.Build(
            Endpoint,
            "codesigningaccounts/{account}/mstregister",
            "acct/../evil",
            Profile,
            null);

        Assert.IsTrue(
            result.AbsoluteUri.Contains("acct%2F..%2Fevil", StringComparison.Ordinal),
            $"Expected the account segment to be escaped, but got '{result.AbsoluteUri}'.");
    }

    /// <summary>
    /// An endpoint that is missing or not an absolute URI is rejected with a clear message.
    /// </summary>
    /// <param name="endpoint">The invalid endpoint.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("not-a-uri")]
    [DataRow("/relative/path")]
    public void Build_WithInvalidEndpoint_Throws(string endpoint)
    {
        Assert.ThrowsException<ArgumentException>(
            () => AasMstRouteBuilder.Build(endpoint, "mstregister", Account, Profile, null));
    }
}
