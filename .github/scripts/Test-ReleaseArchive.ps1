# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

param(
    [Parameter(Mandatory = $true)]
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
$archive = Get-Item -LiteralPath $ArchivePath
$smokeDirectory = Join-Path ([IO.Path]::GetTempPath()) "cose-release-smoke-$([Guid]::NewGuid().ToString('N'))"
$originalBundleDirectory = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR

try {
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $smokeDirectory
    $executables = @(Get-ChildItem $smokeDirectory -Filter CoseSignTool.exe -File -Recurse)
    if ($executables.Count -ne 1) {
        throw "Expected exactly one CoseSignTool.exe in $($archive.Name)."
    }

    $executable = $executables[0]
    if (Test-Path (Join-Path $executable.DirectoryName 'plugins')) {
        throw "Expected bundled plugins, not an external plugins directory in $($archive.Name)."
    }

    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $smokeDirectory 'bundle'
    $process = Start-Process -FilePath $executable.FullName -ArgumentList '--help' `
        -WorkingDirectory $smokeDirectory -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $smokeDirectory 'stdout.txt') `
        -RedirectStandardError (Join-Path $smokeDirectory 'stderr.txt')
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id
        throw "Command discovery timed out for $($archive.Name)."
    }
    $process.WaitForExit()

    $help = (Get-Content (Join-Path $smokeDirectory 'stdout.txt') -Raw) +
        (Get-Content (Join-Path $smokeDirectory 'stderr.txt') -Raw)
    # General help intentionally returns the usage exit code.
    if ($process.ExitCode -ne 9999) {
        throw "Unexpected help exit code $($process.ExitCode) for $($archive.Name):`n$help"
    }

    foreach ($name in @('aas_sign_mst_register', 'mst_register', 'mst_verify', 'indirect-sign', 'indirect-verify', 'azure-artifact-signing')) {
        if ($help -notmatch "(?m)^\s+$([regex]::Escape($name))\s+") {
            throw "Missing bundled plugin command/provider '$name' in $($archive.Name):`n$help"
        }
    }

    Write-Host "Verified bundled plugin discovery in $($archive.Name)."
}
finally {
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $originalBundleDirectory
    if (Test-Path $smokeDirectory) {
        Remove-Item -LiteralPath $smokeDirectory -Recurse -Force
    }
}
