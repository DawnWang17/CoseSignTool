// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

/// <summary>
/// The outcome of an Azure Artifact Signing (AAS) MST proxy registration request.
/// </summary>
public sealed class AasMstRegisterResult
{
    /// <summary>
    /// Gets or sets the final HTTP status code returned by the service, after any asynchronous
    /// operation polling has completed.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Gets or sets the transparent statement returned by MST, or <see langword="null"/> when the
    /// service did not return a COSE payload.
    /// </summary>
    /// <remarks>
    /// MST returns the full transparent statement, which is the submitted COSE_Sign1 message with
    /// receipts added to its unprotected header bucket.
    /// </remarks>
    public byte[]? TransparentStatement { get; set; }

    /// <summary>
    /// Gets or sets the MST entry identifier reported by the service, when one was provided.
    /// </summary>
    public string? EntryId { get; set; }

    /// <summary>
    /// Gets or sets the operation identifier used to poll for asynchronous completion, when the
    /// service returned one.
    /// </summary>
    public string? OperationId { get; set; }

    /// <summary>
    /// Gets or sets the response body decoded as text, when the response was not a COSE payload.
    /// </summary>
    /// <remarks>
    /// This is populated for diagnostic purposes on both success and failure. It is truncated by the
    /// caller before logging so that large error documents do not flood the console.
    /// </remarks>
    public string? ResponseBody { get; set; }

    /// <summary>
    /// Gets a value indicating whether the service reported success.
    /// </summary>
    public bool IsSuccess => this.StatusCode >= 200 && this.StatusCode < 300;
}
