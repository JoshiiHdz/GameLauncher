<#
.SYNOPSIS
  Optional code signing for a RELEASE build (release.yml calls it). With no certificate configured it does nothing and the release is
  unsigned exactly as before; with one, the launcher, the Velopack setup and the wrapped installer all carry a signature.

.DESCRIPTION
    -Mode Prepare     Given the certificate as base64 text (-PfxBase64) and its password (-PfxPassword), writes it to a temporary .pfx, checks it
                      is a usable code-signing certificate (has its private key, has the Code Signing purpose, is in date), finds signtool.exe,
                      and publishes what later steps need: AXIS_SIGN_PFX, AXIS_SIGN_PARAMS (the single string `vpk pack --signParams` takes) and
                      AXIS_SIGN_PASSWORD go to the GitHub Actions environment file, and signtool's folder to its PATH file. With -PfxBase64
                      empty it says so and exits cleanly: signing is optional.
    -Mode SignFile    Signs -File with the prepared certificate (SHA-256, RFC 3161 timestamp).
    -Mode VerifyFile  Fails unless -File carries a signature Windows trusts (signtool verify /pa). A self-signed test certificate fails this
                      on purpose: the check is real, not a rubber stamp.
    -Mode Cleanup     Deletes the temporary .pfx.

  The certificate and password are secrets: they come in through step environment variables, are never echoed, and the .pfx lives only in the
  runner's temp folder until Cleanup.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'SignFile', 'VerifyFile', 'Cleanup')][string]$Mode,
    [string]$PfxBase64,
    [string]$PfxPassword,
    [string]$PfxPath,
    [string]$File,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$NoTimestamp,                 # tests only: no network
    [string]$EnvFile = $env:GITHUB_ENV,
    [string]$PathFile = $env:GITHUB_PATH
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Fail([string]$Message) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::error::$Message" } else { Write-Host "ERROR: $Message" -ForegroundColor Red }
    exit 1
}

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kits)) { return $null }
    # Newest SDK first; 10.0.x folders sort by version, not by text.
    Get-ChildItem -Path $kits -Directory |
        Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

function Tool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $found = Find-SignTool
    if (-not $found) { Fail 'signtool.exe was not found (install the Windows SDK, or put signtool on PATH).' }
    return $found
}

$defaultPfx = Join-Path ($(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() })) 'axis-signing.pfx'

switch ($Mode) {
    'Prepare' {
        if ([string]::IsNullOrWhiteSpace($PfxBase64)) {
            Write-Host 'No code-signing certificate is configured: this release will be UNSIGNED (Windows SmartScreen will warn users).'
            exit 0
        }
        if ([string]::IsNullOrEmpty($PfxPassword)) { Fail 'A code-signing certificate was given without its password.' }
        if ($PfxPassword.Contains('"')) { Fail 'The code-signing password must not contain a double quote (it is passed to vpk inside a quoted string).' }

        if (-not $PfxPath) { $PfxPath = $defaultPfx }
        try { [IO.File]::WriteAllBytes($PfxPath, [Convert]::FromBase64String(($PfxBase64 -replace '\s', ''))) }
        catch { Fail 'The code-signing certificate is not valid base64 text.' }

        try {
            $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $PfxPassword)
        }
        catch {
            Remove-Item $PfxPath -Force -ErrorAction SilentlyContinue
            Fail 'The code-signing certificate could not be opened - wrong password, or not a .pfx file.'
        }
        if (-not $cert.HasPrivateKey) { Remove-Item $PfxPath -Force; Fail 'The code-signing certificate has no private key.' }
        if ($cert.NotAfter -lt (Get-Date)) { Remove-Item $PfxPath -Force; Fail "The code-signing certificate expired on $($cert.NotAfter.ToString('yyyy-MM-dd'))." }
        $usages = $cert.Extensions | Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value }
        if ($usages -and ($usages -notcontains '1.3.6.1.5.5.7.3.3')) { Remove-Item $PfxPath -Force; Fail 'That certificate is not a code-signing certificate.' }

        $tool = Tool
        $timestamp = if ($NoTimestamp) { '' } else { " /tr $TimestampUrl /td sha256" }
        $params = "/f `"$PfxPath`" /p `"$PfxPassword`" /fd sha256$timestamp"

        if ($EnvFile) {
            Add-Content -Path $EnvFile -Value "AXIS_SIGN_PFX=$PfxPath"
            Add-Content -Path $EnvFile -Value "AXIS_SIGN_PASSWORD=$PfxPassword"
            Add-Content -Path $EnvFile -Value "AXIS_SIGN_PARAMS=$params"
        }
        if ($PathFile) { Add-Content -Path $PathFile -Value (Split-Path $tool) }

        Write-Host "Code signing is ready: $($cert.Subject), valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))."
    }

    'SignFile' {
        if (-not $File -or -not (Test-Path $File)) { Fail "Nothing to sign: '$File' does not exist." }
        $pfx = if ($PfxPath) { $PfxPath } else { $env:AXIS_SIGN_PFX }
        $password = if ($PfxPassword) { $PfxPassword } else { $env:AXIS_SIGN_PASSWORD }
        if (-not $pfx -or -not (Test-Path $pfx)) { Fail 'No prepared code-signing certificate (run -Mode Prepare first).' }

        $arguments = @('sign', '/f', $pfx, '/p', $password, '/fd', 'sha256')
        if (-not $NoTimestamp) { $arguments += @('/tr', $TimestampUrl, '/td', 'sha256') }
        $arguments += $File
        & (Tool) @arguments | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "signtool could not sign '$File'." }
        Write-Host "Signed $File"
    }

    'VerifyFile' {
        if (-not $File -or -not (Test-Path $File)) { Fail "Nothing to verify: '$File' does not exist." }
        & (Tool) verify /pa /q $File | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "'$File' does not carry a signature Windows trusts." }
        Write-Host "Verified $File"
    }

    'Cleanup' {
        $pfx = if ($PfxPath) { $PfxPath } elseif ($env:AXIS_SIGN_PFX) { $env:AXIS_SIGN_PFX } else { $defaultPfx }
        if (Test-Path $pfx) { Remove-Item $pfx -Force }
    }
}
