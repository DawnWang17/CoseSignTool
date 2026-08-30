// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin;

/// <summary>
/// CoseSignTool plugin that registers COSE_Sign1 messages with Microsoft's Signing Transparency
/// (MST) through the Azure Artifact Signing (AAS) proxy.
/// </summary>
/// <remarks>
/// This plugin complements the <c>azure-artifact-signing</c> certificate provider. Signing and
/// transparency registration then share a single credential and a single authorization policy: the
/// AAS account and certificate profile.
/// </remarks>
public class AasMstPlugin : ICoseSignToolPlugin
{
    private readonly List<IPluginCommand> commands;

    /// <summary>
    /// Initializes a new instance of the <see cref="AasMstPlugin"/> class.
    /// </summary>
    public AasMstPlugin()
    {
        this.commands = new List<IPluginCommand>
        {
            new AasMstRegisterCommand()
        };
    }

    /// <inheritdoc/>
    public string Name => "Azure Artifact Signing MST Proxy";

    /// <inheritdoc/>
    public string Version =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString() ?? "1.0.0";

    /// <inheritdoc/>
    public string Description => "Registers COSE Sign1 messages with Microsoft's Signing Transparency (MST) through the Azure Artifact Signing proxy, authorizing with an AAS account and certificate profile.";

    /// <inheritdoc/>
    public IEnumerable<IPluginCommand> Commands => this.commands;

    /// <inheritdoc/>
    public void Initialize(IConfiguration? configuration = null)
    {
        // No plugin-scoped initialization is required. All configuration is read per command
        // execution from the command line.
    }
}
