// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

/// <summary>
/// Describes a single Azure Artifact Signing (AAS) MST proxy registration request.
/// </summary>
public sealed class AasMstRegisterRequest
{
    /// <summary>
    /// Gets or sets the absolute URI of the AAS MST proxy register route.
    /// </summary>
    public required Uri RequestUri { get; set; }

    /// <summary>
    /// Gets or sets the AAS account name used by the service for authorization.
    /// </summary>
    public required string AccountName { get; set; }

    /// <summary>
    /// Gets or sets the AAS certificate profile name used by the service for authorization.
    /// </summary>
    public required string CertificateProfileName { get; set; }

    /// <summary>
    /// Gets or sets the encoded COSE_Sign1 message to register with MST.
    /// </summary>
    public required byte[] SignatureBytes { get; set; }

    /// <summary>
    /// Gets or sets where the account and certificate profile names are carried on the request.
    /// </summary>
    public AasMstParameterLocation ParameterLocation { get; set; } = AasMstParameterLocation.Body;

    /// <summary>
    /// Gets or sets the OAuth 2.0 bearer token presented to the service, or <see langword="null"/>
    /// to send the request without an <c>Authorization</c> header.
    /// </summary>
    public string? AccessToken { get; set; }
}
