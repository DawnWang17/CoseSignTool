// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CoseSignTool.AasMst.Plugin.Tests;

using CoseSignTool.AasMst.Plugin;
using CoseSignTool.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="AasMstRegisterCommand"/> and <see cref="AasMstPlugin"/>.
/// </summary>
[TestClass]
public class AasMstRegisterCommandTests
{
    /// <summary>
    /// Builds an in-memory configuration from the supplied key/value pairs.
    /// </summary>
    /// <param name="values">The configuration values.</param>
    /// <returns>The configuration.</returns>
    private static IConfiguration BuildConfiguration(params (string Key, string? Value)[] values)
    {
        Dictionary<string, string?> data = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in values)
        {
            data[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>
    /// The command advertises the name the CLI dispatches on.
    /// </summary>
    [TestMethod]
    public void Name_IsAasMstRegister()
    {
        Assert.AreEqual("aas_mst_register", new AasMstRegisterCommand().Name);
    }

    /// <summary>
    /// Every option the usage text documents is also declared, so the CLI parser accepts it.
    /// </summary>
    /// <param name="optionName">The option expected to be declared.</param>
    [TestMethod]
    [DataRow("endpoint")]
    [DataRow("account-name")]
    [DataRow("cert-profile-name")]
    [DataRow("payload")]
    [DataRow("signature")]
    [DataRow("output")]
    [DataRow("transparent-statement")]
    [DataRow("register-path")]
    [DataRow("param-location")]
    [DataRow("api-version")]
    [DataRow("scope")]
    [DataRow("token-env")]
    [DataRow("timeout")]
    public void Options_DeclareExpectedOption(string optionName)
    {
        Assert.IsTrue(
            new AasMstRegisterCommand().Options.ContainsKey(optionName),
            $"Expected the command to declare the '{optionName}' option.");
    }

    /// <summary>
    /// The usage text names the command and its required arguments.
    /// </summary>
    [TestMethod]
    public void Usage_MentionsRequiredArguments()
    {
        string usage = new AasMstRegisterCommand().Usage;

        StringAssert.Contains(usage, "aas_mst_register");
        StringAssert.Contains(usage, "--account-name");
        StringAssert.Contains(usage, "--cert-profile-name");
    }

    /// <summary>
    /// The timeout defaults to 30 seconds when unspecified.
    /// </summary>
    [TestMethod]
    public void TryParseTimeout_WithNoValue_UsesDefault()
    {
        Assert.IsTrue(AasMstRegisterCommand.TryParseTimeout(BuildConfiguration(), out int timeout));
        Assert.AreEqual(AasMstRegisterCommand.DefaultTimeoutSeconds, timeout);
    }

    /// <summary>
    /// A positive integer timeout is accepted.
    /// </summary>
    [TestMethod]
    public void TryParseTimeout_WithPositiveValue_IsAccepted()
    {
        Assert.IsTrue(AasMstRegisterCommand.TryParseTimeout(BuildConfiguration(("timeout", "120")), out int timeout));
        Assert.AreEqual(120, timeout);
    }

    /// <summary>
    /// Zero, negative, and non-numeric timeouts are rejected.
    /// </summary>
    /// <param name="value">The invalid timeout value.</param>
    [TestMethod]
    [DataRow("0")]
    [DataRow("-5")]
    [DataRow("abc")]
    [DataRow("")]
    public void TryParseTimeout_WithInvalidValue_IsRejected(string value)
    {
        Assert.IsFalse(AasMstRegisterCommand.TryParseTimeout(BuildConfiguration(("timeout", value)), out _));
    }

    /// <summary>
    /// The parameter location defaults to the JSON body form.
    /// </summary>
    [TestMethod]
    public void TryParseParameterLocation_WithNoValue_DefaultsToBody()
    {
        Assert.IsTrue(AasMstRegisterCommand.TryParseParameterLocation(BuildConfiguration(), out AasMstParameterLocation location));
        Assert.AreEqual(AasMstParameterLocation.Body, location);
    }

    /// <summary>
    /// Each supported location name is parsed, case-insensitively and ignoring surrounding space.
    /// </summary>
    /// <param name="value">The option value.</param>
    /// <param name="expected">The expected parsed location.</param>
    [TestMethod]
    [DataRow("body", AasMstParameterLocation.Body)]
    [DataRow("BODY", AasMstParameterLocation.Body)]
    [DataRow("header", AasMstParameterLocation.Header)]
    [DataRow("Header", AasMstParameterLocation.Header)]
    [DataRow("path", AasMstParameterLocation.Path)]
    [DataRow("  path  ", AasMstParameterLocation.Path)]
    public void TryParseParameterLocation_WithSupportedValue_IsParsed(string value, AasMstParameterLocation expected)
    {
        Assert.IsTrue(AasMstRegisterCommand.TryParseParameterLocation(BuildConfiguration(("param-location", value)), out AasMstParameterLocation location));
        Assert.AreEqual(expected, location);
    }

    /// <summary>
    /// An unrecognized location, including a bare numeric value, is rejected.
    /// </summary>
    /// <param name="value">The invalid option value.</param>
    [TestMethod]
    [DataRow("query")]
    [DataRow("cookie")]
    [DataRow("99")]
    public void TryParseParameterLocation_WithUnknownValue_IsRejected(string value)
    {
        Assert.IsFalse(AasMstRegisterCommand.TryParseParameterLocation(BuildConfiguration(("param-location", value)), out _));
    }

    /// <summary>
    /// A missing signature file is reported as a file-not-found error rather than a network failure.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithMissingSignatureFile_ReturnsFileNotFound()
    {
        string payloadPath = Path.Combine(Path.GetTempPath(), $"aasmst-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(payloadPath, "payload");

        try
        {
            IConfiguration configuration = BuildConfiguration(
                ("endpoint", "https://wus.codesigning.azure.net"),
                ("account-name", "testwus"),
                ("cert-profile-name", "testWusCert1"),
                ("payload", payloadPath),
                ("signature", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.cose")));

            PluginExitCode exitCode = await new AasMstRegisterCommand().ExecuteAsync(configuration);

            Assert.AreEqual(PluginExitCode.UserSpecifiedFileNotFound, exitCode);
        }
        finally
        {
            File.Delete(payloadPath);
        }
    }

    /// <summary>
    /// A missing required option is reported before any network activity.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithMissingRequiredOption_ReturnsMissingRequiredOption()
    {
        IConfiguration configuration = BuildConfiguration(("endpoint", "https://wus.codesigning.azure.net"));

        PluginExitCode exitCode = await new AasMstRegisterCommand().ExecuteAsync(configuration);

        Assert.AreEqual(PluginExitCode.MissingRequiredOption, exitCode);
    }

    /// <summary>
    /// An invalid timeout is rejected before any network activity.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithInvalidTimeout_ReturnsInvalidArgumentValue()
    {
        IConfiguration configuration = BuildConfiguration(
            ("endpoint", "https://wus.codesigning.azure.net"),
            ("account-name", "testwus"),
            ("cert-profile-name", "testWusCert1"),
            ("payload", "payload.txt"),
            ("signature", "signature.cose"),
            ("timeout", "nope"));

        PluginExitCode exitCode = await new AasMstRegisterCommand().ExecuteAsync(configuration);

        Assert.AreEqual(PluginExitCode.InvalidArgumentValue, exitCode);
    }

    /// <summary>
    /// A signature file that is not a COSE_Sign1 message is rejected with a clear argument error.
    /// </summary>
    [TestMethod]
    public async Task ExecuteAsync_WithUndecodableSignature_ReturnsInvalidArgumentValue()
    {
        string payloadPath = Path.Combine(Path.GetTempPath(), $"aasmst-{Guid.NewGuid():N}.txt");
        string signaturePath = Path.Combine(Path.GetTempPath(), $"aasmst-{Guid.NewGuid():N}.cose");
        await File.WriteAllTextAsync(payloadPath, "payload");
        await File.WriteAllTextAsync(signaturePath, "this is not a COSE message");

        try
        {
            IConfiguration configuration = BuildConfiguration(
                ("endpoint", "https://wus.codesigning.azure.net"),
                ("account-name", "testwus"),
                ("cert-profile-name", "testWusCert1"),
                ("payload", payloadPath),
                ("signature", signaturePath));

            PluginExitCode exitCode = await new AasMstRegisterCommand().ExecuteAsync(configuration);

            Assert.AreEqual(PluginExitCode.InvalidArgumentValue, exitCode);
        }
        finally
        {
            File.Delete(payloadPath);
            File.Delete(signaturePath);
        }
    }

    /// <summary>
    /// The plugin exposes the register command.
    /// </summary>
    [TestMethod]
    public void Plugin_ExposesRegisterCommand()
    {
        AasMstPlugin plugin = new();

        Assert.AreEqual(1, plugin.Commands.Count());
        Assert.IsInstanceOfType(plugin.Commands.Single(), typeof(AasMstRegisterCommand));
        Assert.IsFalse(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.IsFalse(string.IsNullOrWhiteSpace(plugin.Description));
        Assert.IsFalse(string.IsNullOrWhiteSpace(plugin.Version));
    }

    /// <summary>
    /// Initialization is a no-op and must not throw, including with a null configuration.
    /// </summary>
    [TestMethod]
    public void Plugin_Initialize_DoesNotThrow()
    {
        new AasMstPlugin().Initialize(null);
    }
}
