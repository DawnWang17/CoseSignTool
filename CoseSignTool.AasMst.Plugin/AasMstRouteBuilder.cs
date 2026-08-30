// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

using System.Globalization;

/// <summary>
/// Builds the absolute request URI used to call the Azure Artifact Signing (AAS) MST proxy.
/// </summary>
/// <remarks>
/// The route is assembled from an endpoint (the AAS service base address), a path template, and an
/// optional API version. The template may contain the case-insensitive placeholders
/// <c>{account}</c> and <c>{profile}</c>, which are replaced with URL-escaped values. This keeps the
/// CLI usable against both a flat proxy route (<c>/mstregister</c>) and an account-scoped route
/// (<c>/codesigningaccounts/{account}/certificateprofiles/{profile}/mstregister</c>).
/// </remarks>
public static class AasMstRouteBuilder
{
    /// <summary>
    /// The default register path used when the caller does not supply <c>--register-path</c>.
    /// </summary>
    public const string DefaultRegisterPath = "mstregister";

    /// <summary>
    /// The path template placeholder replaced by the AAS account name.
    /// </summary>
    private const string AccountPlaceholder = "{account}";

    /// <summary>
    /// The path template placeholder replaced by the AAS certificate profile name.
    /// </summary>
    private const string ProfilePlaceholder = "{profile}";

    /// <summary>
    /// Builds the absolute URI for an AAS MST proxy request.
    /// </summary>
    /// <param name="endpoint">The AAS service endpoint, for example <c>https://wus.codesigning.azure.net</c>.</param>
    /// <param name="pathTemplate">
    /// The register path, optionally containing the <c>{account}</c> and <c>{profile}</c> placeholders.
    /// A leading slash is optional.
    /// </param>
    /// <param name="accountName">The AAS account name substituted for <c>{account}</c>.</param>
    /// <param name="certificateProfileName">The certificate profile name substituted for <c>{profile}</c>.</param>
    /// <param name="apiVersion">
    /// An optional service API version. When supplied, it is appended as an <c>api-version</c> query
    /// parameter. When <see langword="null"/> or whitespace, no query string is added.
    /// </param>
    /// <returns>The absolute request <see cref="Uri"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endpoint"/> is not a valid absolute URI.</exception>
    /// <example>
    /// A flat proxy route with no API version:
    /// <code>
    /// AasMstRouteBuilder.Build("https://wus.codesigning.azure.net", "mstregister", "testwus", "testWusCert1", null);
    /// // https://wus.codesigning.azure.net/mstregister
    /// </code>
    /// An account-scoped route:
    /// <code>
    /// AasMstRouteBuilder.Build(
    ///     "https://wus.codesigning.azure.net",
    ///     "codesigningaccounts/{account}/certificateprofiles/{profile}/mstregister",
    ///     "testwus",
    ///     "testWusCert1",
    ///     "2023-06-15-preview");
    /// // https://wus.codesigning.azure.net/codesigningaccounts/testwus/certificateprofiles/testWusCert1/mstregister?api-version=2023-06-15-preview
    /// </code>
    /// </example>
    public static Uri Build(
        string endpoint,
        string? pathTemplate,
        string accountName,
        string certificateProfileName,
        string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("The AAS endpoint must be a non-empty absolute URI.", nameof(endpoint));
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? baseUri))
        {
            throw new ArgumentException($"The AAS endpoint '{endpoint}' is not a valid absolute URI.", nameof(endpoint));
        }

        string template = string.IsNullOrWhiteSpace(pathTemplate) ? DefaultRegisterPath : pathTemplate!.Trim();

        // Replace the placeholders case-insensitively so '{Account}' and '{account}' behave the same.
        string resolvedPath = ReplaceIgnoreCase(template, AccountPlaceholder, Uri.EscapeDataString(accountName));
        resolvedPath = ReplaceIgnoreCase(resolvedPath, ProfilePlaceholder, Uri.EscapeDataString(certificateProfileName));

        // Join the endpoint path and the register path with exactly one separating slash. The
        // endpoint may legitimately carry its own path prefix (for example a test gateway), so the
        // endpoint path is preserved rather than replaced.
        string basePath = baseUri.AbsolutePath.TrimEnd('/');
        string combinedPath = string.Concat(basePath, "/", resolvedPath.TrimStart('/'));

        UriBuilder builder = new(baseUri)
        {
            Path = combinedPath
        };

        if (!string.IsNullOrWhiteSpace(apiVersion))
        {
            builder.Query = string.Format(
                CultureInfo.InvariantCulture,
                "api-version={0}",
                Uri.EscapeDataString(apiVersion!.Trim()));
        }

        return builder.Uri;
    }

    /// <summary>
    /// Performs a case-insensitive replacement of every occurrence of <paramref name="oldValue"/>.
    /// </summary>
    /// <param name="source">The string to search.</param>
    /// <param name="oldValue">The token to replace.</param>
    /// <param name="newValue">The replacement text.</param>
    /// <returns>The string with all occurrences replaced.</returns>
    private static string ReplaceIgnoreCase(string source, string oldValue, string newValue)
    {
        return source.Replace(oldValue, newValue, StringComparison.OrdinalIgnoreCase);
    }
}
