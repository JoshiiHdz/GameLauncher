<#
.SYNOPSIS
  Guards the IGDB relay configuration of a RELEASE build (release.yml calls it twice).

.DESCRIPTION
  A build without default-igdb-relay.txt is valid for development: the launcher then just has no built-in IGDB access. A published
  release must never be that build by accident - it would ship as another SteamGridDB-only installer with nothing failing - so:

    -Mode Write          before the build: requires a valid https address AND key pin(s), and writes src/GameLauncher/default-igdb-relay.txt.
                         Empty, malformed, plain-http or pin-less values FAIL the release.
    -Mode VerifyPackage  after `vpk pack`: opens the real package (.nupkg), loads the packaged GameLauncher.dll, reads the configuration that
                         is actually embedded in it, and fails unless it is present, valid, and equal to what was written.

  The rules mirror DefaultIgdbRelay.Parse in the launcher (which independently refuses anything else at runtime).
  Neither the address nor the pin is secret.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Write', 'VerifyPackage')][string]$Mode,
    [string]$Url,
    [string]$Pin,
    [string]$OutFile,
    [string]$Package,
    [string]$ExpectedFile
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress records are noise in a build log (and can flood a captured stderr)

function Fail([string]$Message) {
    if ($env:GITHUB_ACTIONS) { Write-Host "::error::$Message" } else { Write-Host "ERROR: $Message" -ForegroundColor Red }
    exit 1
}

# $null when valid, otherwise the reason it is not.
function Test-RelayConfig([string]$Address, [string]$Pins) {
    if ([string]::IsNullOrWhiteSpace($Address)) { return 'the relay address is empty' }
    if ([string]::IsNullOrWhiteSpace($Pins)) { return 'the relay key pin is empty' }
    if ($Address.Trim() -notmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/?$') { return "the relay address must be a bare https address (https://host[:port]); plain http, paths, credentials and anything else are refused: '$($Address.Trim())'" }
    $list = @($Pins.Split(',') | ForEach-Object { $_.Trim() })
    if ($list.Count -lt 1 -or $list.Count -gt 3) { return 'between one and three key pins are required' }
    foreach ($p in $list) { if ($p -notmatch '^sha256/[A-Za-z0-9+/]{43}=$') { return "the key pin '$p' is not of the form sha256/<44-character base64>" } }
    if (($list | Select-Object -Unique).Count -ne $list.Count) { return 'duplicate key pins' }
    return $null
}

switch ($Mode) {
    'Write' {
        if ([string]::IsNullOrWhiteSpace($OutFile)) { Fail '-OutFile is required in Write mode' }
        $problem = Test-RelayConfig $Url $Pin
        if ($problem) {
            Fail "A release needs the IGDB relay configuration, and $problem. Set the repository VARIABLES IGDB_RELAY_URL and IGDB_RELAY_PIN (Settings > Secrets and variables > Actions > Variables); the pin is what igdb-relay-make-cert.sh prints. Development builds may omit them; releases may not."
        }
        [System.IO.File]::WriteAllText($OutFile, ($Url.Trim() + "`n" + ($Pin.Trim()) + "`n"), (New-Object System.Text.UTF8Encoding($false)))
        Write-Host "Relay configuration is valid and was written to $OutFile."
    }

    'VerifyPackage' {
        if (-not (Test-Path -LiteralPath $Package)) { Fail "package not found: $Package" }
        if (-not (Test-Path -LiteralPath $ExpectedFile)) { Fail "expected configuration file not found: $ExpectedFile" }
        $expected = @((Get-Content -LiteralPath $ExpectedFile) | ForEach-Object { $_.Trim() } | Where-Object { $_ })

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $work = Join-Path ([System.IO.Path]::GetTempPath()) ("relay-verify-" + [guid]::NewGuid())
        New-Item -ItemType Directory -Path $work | Out-Null
        try {
            $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
            try {
                $entry = $zip.Entries | Where-Object { $_.Name -eq 'GameLauncher.dll' } | Select-Object -First 1
                if (-not $entry) { Fail "GameLauncher.dll is not inside the package $Package" }
                $dll = Join-Path $work 'GameLauncher.dll'
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll)
            } finally { $zip.Dispose() }

            $assembly = [System.Reflection.Assembly]::LoadFile($dll)
            $resource = $assembly.GetManifestResourceNames() | Where-Object { $_ -like '*default-igdb-relay.txt' } | Select-Object -First 1
            if (-not $resource) { Fail 'The PACKAGED launcher contains no IGDB relay configuration (default-igdb-relay.txt is not embedded) - this build would ship SteamGridDB-only.' }

            $stream = $assembly.GetManifestResourceStream($resource)
            $embedded = @((New-Object System.IO.StreamReader($stream)).ReadToEnd() -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            if ($embedded.Count -ne 2) { Fail "The packaged relay configuration must have exactly two lines (address, pin); it has $($embedded.Count)." }
            $problem = Test-RelayConfig $embedded[0] $embedded[1]
            if ($problem) { Fail "The packaged relay configuration is invalid: $problem." }
            if ($embedded[0] -ne $expected[0] -or $embedded[1] -ne $expected[1]) { Fail 'The packaged relay configuration differs from the one this build was given.' }
            Write-Host "Packaged relay configuration verified: $($embedded[0]) with $((($embedded[1]) -split ',').Count) pin(s)."
        } finally {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
