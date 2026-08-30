// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

/// <summary>
/// Identifies where the Azure Artifact Signing (AAS) account name and certificate profile name are
/// carried on an MST proxy register request.
/// </summary>
/// <remarks>
/// The AAS MST proxy performs authorization using the same account/certificate-profile pair that
/// governs signing, so both values must reach the service on every call. The exact transport for
/// those values is a property of the service contract rather than of COSE, so it is configurable
/// via <c>--param-location</c> to avoid pinning the CLI to a contract that may still change.
/// </remarks>
public enum AasMstParameterLocation
{
    /// <summary>
    /// The account and certificate profile names are sent as properties of a JSON request body,
    /// alongside the base64-encoded COSE_Sign1 message. This is the default.
    /// </summary>
    Body = 0,

    /// <summary>
    /// The account and certificate profile names are sent as HTTP request headers
    /// (<c>x-ms-codesigning-account-name</c> and <c>x-ms-codesigning-certificate-profile-name</c>)
    /// and the request body is the raw COSE_Sign1 message.
    /// </summary>
    Header = 1,

    /// <summary>
    /// The account and certificate profile names are substituted into the request path via the
    /// <c>{account}</c> and <c>{profile}</c> placeholders, and the request body is the raw
    /// COSE_Sign1 message. This mirrors the existing AAS sign route shape,
    /// <c>/codesigningaccounts/{account}/certificateprofiles/{profile}/sign</c>.
    /// </summary>
    Path = 2
}
