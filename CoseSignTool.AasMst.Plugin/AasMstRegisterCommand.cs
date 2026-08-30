// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Cose;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

/// <summary>
/// Registers a COSE_Sign1 message with Microsoft's Signing Transparency (MST) by way of the Azure
/// Artifact Signing (AAS) proxy.
/// </summary>
/// <remarks>
/// <para>
/// This command is the transparency counterpart to signing with the <c>azure-artifact-signing</c>
/// certificate provider. Rather than authenticating directly to an MST ledger, the caller presents
/// an AAS token together with the account and certificate profile names, and AAS performs
/// authorization using the same policy that governs signing before forwarding the statement to MST.
/// </para>
/// <para>
/// The proxy route and the placement of the account and profile names are configurable because the
/// service contract is still being finalized. See <c>--register-path</c> and <c>--param-location</c>.
/// </para>
/// </remarks>
public class AasMstRegisterCommand : PluginCommandBase
{
    /// <summary>
    /// The default OAuth 2.0 scope requested for the AAS service. This is the same scope used for
    /// signing, which is what allows AAS to authorize transparency registration as signing.
    /// </summary>
    internal const string DefaultScope = "https://codesigning.azure.net/.default";

    /// <summary>
    /// The default environment variable consulted for a pre-acquired access token.
    /// </summary>
    internal const string DefaultTokenEnvVarName = "AAS_MST_TOKEN";

    /// <summary>
    /// The default operation timeout, in seconds.
    /// </summary>
    internal const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// The maximum number of characters of a non-COSE response body echoed to the log on failure.
    /// </summary>
    private const int MaxLoggedBodyLength = 2048;

    /// <summary>
    /// The command options, keyed by option name. The CLI host projects these into <c>--name</c> switches.
    /// </summary>
    private static readonly Dictionary<string, string> CommandOptions = new()
    {
        { "endpoint", "The Azure Artifact Signing service endpoint URL (for example https://wus.codesigning.azure.net)" },
        { "account-name", "The Azure Artifact Signing account name used for authorization" },
        { "cert-profile-name", "The Azure Artifact Signing certificate profile name used for authorization" },
        { "payload", "The file path to the payload file" },
        { "signature", "The file path to the COSE Sign1 signature file to register" },
        { "output", "The file path where the JSON result will be written (optional)" },
        { "transparent-statement", "The file path where the returned transparent statement will be written (optional)" },
        { "register-path", "The proxy register path. Supports the {account} and {profile} placeholders (default: mstregister)" },
        { "param-location", "Where the account and profile names travel: body (default), header, or path" },
        { "api-version", "The service API version, appended as an api-version query parameter (optional)" },
        { "scope", "The OAuth 2.0 scope requested for the AAS token (default: " + DefaultScope + ")" },
        { "token-env", "The name of an environment variable containing a pre-acquired access token (default: " + DefaultTokenEnvVarName + ")" },
        { "timeout", "Timeout in seconds for the whole operation, including polling (default: 30)" }
    };

    /// <inheritdoc/>
    public override string Name => "aas_mst_register";

    /// <inheritdoc/>
    public override string Description => "Register a COSE Sign1 message with Microsoft's Signing Transparency (MST) through the Azure Artifact Signing proxy";

    /// <inheritdoc/>
    public override IDictionary<string, string> Options => CommandOptions;

    /// <inheritdoc/>
    public override string Usage =>
        $"CoseSignTool {this.Name} --endpoint <aas-endpoint> --account-name <account> --cert-profile-name <profile> --payload <payload-file> --signature <signature-file> [options]{Environment.NewLine}" +
        $"{Environment.NewLine}" +
        $"Required arguments:{Environment.NewLine}" +
        $"  --endpoint              The Azure Artifact Signing service endpoint URL{Environment.NewLine}" +
        $"  --account-name          The Azure Artifact Signing account name{Environment.NewLine}" +
        $"  --cert-profile-name     The Azure Artifact Signing certificate profile name{Environment.NewLine}" +
        $"  --payload               The file path to the payload that was signed{Environment.NewLine}" +
        $"  --signature             The file path to the COSE Sign1 signature file to register{Environment.NewLine}" +
        $"{Environment.NewLine}" +
        $"Optional arguments:{Environment.NewLine}" +
        $"  --output                File path where the JSON result will be written{Environment.NewLine}" +
        $"  --transparent-statement File path where the returned transparent statement will be written{Environment.NewLine}" +
        $"  --register-path         Proxy register path (default: {AasMstRouteBuilder.DefaultRegisterPath}).{Environment.NewLine}" +
        $"                          Supports the {{account}} and {{profile}} placeholders.{Environment.NewLine}" +
        $"  --param-location        Where the account and profile names travel (default: body):{Environment.NewLine}" +
        $"                            body   JSON body with accountName / certificateProfileName / signature{Environment.NewLine}" +
        $"                            header x-ms-codesigning-* headers, raw COSE body{Environment.NewLine}" +
        $"                            path   {{account}} / {{profile}} in --register-path, raw COSE body{Environment.NewLine}" +
        $"  --api-version           Service API version, appended as ?api-version={Environment.NewLine}" +
        $"  --scope                 OAuth 2.0 scope for the AAS token (default: {DefaultScope}){Environment.NewLine}" +
        $"  --token-env             Environment variable holding a pre-acquired token (default: {DefaultTokenEnvVarName}){Environment.NewLine}" +
        $"  --timeout               Timeout in seconds for the whole operation (default: {DefaultTimeoutSeconds}){Environment.NewLine}" +
        $"{Environment.NewLine}" +
        $"Authentication:{Environment.NewLine}" +
        $"  A token is taken from the environment variable named by --token-env when that variable is{Environment.NewLine}" +
        $"  set; otherwise DefaultAzureCredential is used (Azure CLI, managed identity, environment{Environment.NewLine}" +
        $"  variables, and so on). Azure Artifact Signing authorizes the registration using the same{Environment.NewLine}" +
        $"  account and certificate profile permissions that govern signing.{Environment.NewLine}" +
        $"{Environment.NewLine}" +
        $"Examples:{Environment.NewLine}" +
        $"  CoseSignTool {this.Name} --endpoint https://wus.codesigning.azure.net --account-name testwus --cert-profile-name testWusCert1 --payload sample_payload.txt --signature sample_payload.cose{Environment.NewLine}" +
        $"  CoseSignTool {this.Name} --endpoint https://wus.codesigning.azure.net --account-name testwus --cert-profile-name testWusCert1 --payload sample_payload.txt --signature sample_payload.cose --transparent-statement sample_payload.transparent.cose{Environment.NewLine}" +
        $"  CoseSignTool {this.Name} --endpoint https://wus.codesigning.azure.net --account-name testwus --cert-profile-name testWusCert1 --payload sample_payload.txt --signature sample_payload.cose --param-location path --register-path codesigningaccounts/{{account}}/certificateprofiles/{{profile}}/mstregister";

    /// <inheritdoc/>
    public override async Task<PluginExitCode> ExecuteAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        try
        {
            string endpoint = GetRequiredValue(configuration, "endpoint");
            string accountName = GetRequiredValue(configuration, "account-name");
            string certProfileName = GetRequiredValue(configuration, "cert-profile-name");
            string payloadPath = GetRequiredValue(configuration, "payload");
            string signaturePath = GetRequiredValue(configuration, "signature");

            string? outputPath = GetOptionalValue(configuration, "output");
            string? transparentStatementPath = GetOptionalValue(configuration, "transparent-statement");
            string? registerPath = GetOptionalValue(configuration, "register-path", AasMstRouteBuilder.DefaultRegisterPath);
            string? apiVersion = GetOptionalValue(configuration, "api-version");
            string scope = GetOptionalValue(configuration, "scope", DefaultScope) ?? DefaultScope;
            string? tokenEnvVarName = GetOptionalValue(configuration, "token-env");

            if (!TryParseTimeout(configuration, out int timeoutSeconds))
            {
                this.Logger.LogError("Invalid --timeout value. Must be a positive integer.");
                return PluginExitCode.InvalidArgumentValue;
            }

            if (!TryParseParameterLocation(configuration, out AasMstParameterLocation parameterLocation))
            {
                this.Logger.LogError("Invalid --param-location value. Must be one of: body, header, path.");
                return PluginExitCode.InvalidArgumentValue;
            }

            foreach (KeyValuePair<string, string> file in new Dictionary<string, string> { { "Payload", payloadPath }, { "Signature", signaturePath } })
            {
                if (!File.Exists(file.Value))
                {
                    this.Logger.LogError($"{file.Key} file not found: {file.Value}");
                    return PluginExitCode.UserSpecifiedFileNotFound;
                }
            }

            byte[] signatureBytes = await File.ReadAllBytesAsync(signaturePath, cancellationToken).ConfigureAwait(false);

            // Decode before sending so an unusable file fails fast with a clear message instead of a
            // service-side error.
            try
            {
                CoseMessage.DecodeSign1(signatureBytes);
            }
            catch (CryptographicException ex)
            {
                this.Logger.LogError($"Failed to decode COSE Sign1 message from {signaturePath}: {ex.Message}");
                return PluginExitCode.InvalidArgumentValue;
            }

            Uri requestUri = AasMstRouteBuilder.Build(endpoint, registerPath, accountName, certProfileName, apiVersion);

            this.Logger.LogInformation("Registering COSE Sign1 message with MST via Azure Artifact Signing...");
            this.Logger.LogVerbose($"  Endpoint: {endpoint}");
            this.Logger.LogVerbose($"  Account: {accountName}");
            this.Logger.LogVerbose($"  Certificate profile: {certProfileName}");
            this.Logger.LogVerbose($"  Payload: {payloadPath}");
            this.Logger.LogVerbose($"  Signature: {signaturePath} ({signatureBytes.Length} bytes)");
            this.Logger.LogVerbose($"  Request URI: {requestUri}");
            this.Logger.LogVerbose($"  Parameter location: {parameterLocation}");

            // A single deadline covers token acquisition, the register call, and any polling, so a
            // slow credential chain cannot silently exceed --timeout.
            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(timeoutSeconds));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            string? accessToken = await this.AcquireAccessTokenAsync(tokenEnvVarName, scope, linkedCts.Token).ConfigureAwait(false);

            using HttpClient httpClient = new();
            AasMstProxyClient proxyClient = new(httpClient, this.Logger);

            AasMstRegisterResult result = await proxyClient.RegisterAsync(
                new AasMstRegisterRequest
                {
                    RequestUri = requestUri,
                    AccountName = accountName,
                    CertificateProfileName = certProfileName,
                    SignatureBytes = signatureBytes,
                    ParameterLocation = parameterLocation,
                    AccessToken = accessToken
                },
                linkedCts.Token).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                this.Logger.LogError($"MST registration failed with HTTP {result.StatusCode}.");
                if (!string.IsNullOrWhiteSpace(result.ResponseBody))
                {
                    this.Logger.LogError(Truncate(result.ResponseBody!, MaxLoggedBodyLength));
                }

                return PluginExitCode.UnknownError;
            }

            if (result.TransparentStatement is null)
            {
                this.Logger.LogWarning("The service reported success but did not return a transparent statement.");
            }
            else if (!string.IsNullOrWhiteSpace(transparentStatementPath))
            {
                await File.WriteAllBytesAsync(transparentStatementPath!, result.TransparentStatement, cancellationToken).ConfigureAwait(false);
                this.Logger.LogInformation($"Transparent statement written to: {transparentStatementPath}");
            }

            this.Logger.LogInformation("Registration completed successfully.");

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                object jsonResult = new
                {
                    Endpoint = endpoint,
                    RequestUri = requestUri.ToString(),
                    AccountName = accountName,
                    CertificateProfileName = certProfileName,
                    PayloadPath = payloadPath,
                    SignaturePath = signaturePath,
                    RegistrationTime = DateTime.UtcNow,
                    StatusCode = result.StatusCode,
                    result.EntryId,
                    TransparentStatement = result.TransparentStatement is null ? null : Convert.ToBase64String(result.TransparentStatement)
                };

                string json = JsonSerializer.Serialize(jsonResult, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(outputPath!, json, cancellationToken).ConfigureAwait(false);
                this.Logger.LogInformation($"Result written to: {outputPath}");
            }

            return PluginExitCode.Success;
        }
        catch (ArgumentNullException ex)
        {
            this.Logger.LogError($"Missing required argument - {ex.ParamName}");
            return PluginExitCode.MissingRequiredOption;
        }
        catch (ArgumentException ex)
        {
            this.Logger.LogError(ex.Message);
            return PluginExitCode.InvalidArgumentValue;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            this.Logger.LogError("Operation was cancelled.");
            return PluginExitCode.UnknownError;
        }
        catch (OperationCanceledException)
        {
            this.Logger.LogError($"Operation timed out after {GetOptionalValue(configuration, "timeout", DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture))} seconds.");
            return PluginExitCode.UnknownError;
        }
        catch (HttpRequestException ex)
        {
            this.Logger.LogError($"Failed to reach the Azure Artifact Signing MST proxy: {ex.Message}");
            this.Logger.LogException(ex);
            return PluginExitCode.UnknownError;
        }
        catch (IOException ex)
        {
            this.Logger.LogError($"File operation failed: {ex.Message}");
            this.Logger.LogException(ex);
            return PluginExitCode.UnknownError;
        }
        catch (AuthenticationFailedException ex)
        {
            this.Logger.LogError($"Failed to acquire an Azure Artifact Signing access token: {ex.Message}");
            this.Logger.LogException(ex);
            return PluginExitCode.UnknownError;
        }
    }

    /// <summary>
    /// Acquires the bearer token presented to the AAS proxy.
    /// </summary>
    /// <param name="tokenEnvVarName">
    /// An optional environment variable name supplied via <c>--token-env</c>. When supplied
    /// explicitly, the variable must hold a non-whitespace value or the call fails.
    /// </param>
    /// <param name="scope">The OAuth 2.0 scope requested when falling back to Azure credentials.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The access token.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tokenEnvVarName"/> was supplied but the variable is missing or empty.
    /// </exception>
    private async Task<string> AcquireAccessTokenAsync(string? tokenEnvVarName, string scope, CancellationToken cancellationToken)
    {
        bool explicitlyRequested = !string.IsNullOrWhiteSpace(tokenEnvVarName);
        string envVarName = explicitlyRequested ? tokenEnvVarName! : DefaultTokenEnvVarName;
        string? token = Environment.GetEnvironmentVariable(envVarName);

        if (!string.IsNullOrWhiteSpace(token))
        {
            this.Logger.LogVerbose($"AAS auth: using access token from environment variable '{envVarName}'");
            return token!;
        }

        if (explicitlyRequested)
        {
            throw new ArgumentException(
                $"--token-env was set to '{envVarName}' but the environment variable is missing, empty, or whitespace.");
        }

        this.Logger.LogVerbose($"AAS auth: using DefaultAzureCredential for scope '{scope}'");

        // Interactive browser auth is excluded so unattended pipelines fail fast instead of hanging
        // on a prompt that nobody can answer.
        DefaultAzureCredential credential = new(new DefaultAzureCredentialOptions // CodeQL [SM02196] DefaultAzureCredential is the recommended approach for client applications and libraries to authenticate to Azure services
        {
            ExcludeInteractiveBrowserCredential = true
        });

        AccessToken accessToken = await credential
            .GetTokenAsync(new TokenRequestContext(new[] { scope }), cancellationToken)
            .ConfigureAwait(false);

        return accessToken.Token;
    }

    /// <summary>
    /// Parses the <c>--timeout</c> option.
    /// </summary>
    /// <param name="configuration">The command configuration.</param>
    /// <param name="timeoutSeconds">The parsed timeout, in seconds.</param>
    /// <returns><see langword="true"/> when the value is a positive integer.</returns>
    internal static bool TryParseTimeout(IConfiguration configuration, out int timeoutSeconds)
    {
        string value = GetOptionalValue(configuration, "timeout", DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture))
            ?? DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeoutSeconds) && timeoutSeconds > 0;
    }

    /// <summary>
    /// Parses the <c>--param-location</c> option.
    /// </summary>
    /// <param name="configuration">The command configuration.</param>
    /// <param name="parameterLocation">The parsed parameter location.</param>
    /// <returns><see langword="true"/> when the value is absent or names a known location.</returns>
    internal static bool TryParseParameterLocation(IConfiguration configuration, out AasMstParameterLocation parameterLocation)
    {
        parameterLocation = AasMstParameterLocation.Body;
        string? value = GetOptionalValue(configuration, "param-location");

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return Enum.TryParse(value!.Trim(), ignoreCase: true, out parameterLocation)
            && Enum.IsDefined(typeof(AasMstParameterLocation), parameterLocation);
    }

    /// <summary>
    /// Truncates text to a maximum length, appending an ellipsis when characters were removed.
    /// </summary>
    /// <param name="value">The text to truncate.</param>
    /// <param name="maxLength">The maximum number of characters to keep.</param>
    /// <returns>The truncated text.</returns>
    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
    }
}
