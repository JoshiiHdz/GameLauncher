# Code signing

Releases are unsigned until a certificate is added, which is why Windows SmartScreen shows "Windows protected your PC". The release workflow
([.github/workflows/release.yml](../../.github/workflows/release.yml)) already knows how to sign; it only needs a certificate.

## What you need

A **code-signing certificate** as a `.pfx` file with a password. Options, cheapest first:

| Option | Notes |
| --- | --- |
| OV certificate from a certificate authority | Removes the "unknown publisher" wording. SmartScreen still warns until the certificate has built reputation (downloads over time). |
| EV certificate | Builds SmartScreen reputation immediately, costs more, and usually comes on a hardware token - so it can't be used by GitHub-hosted runners as a plain `.pfx`. |
| Azure Trusted Signing | Not covered by `Code-Signing.ps1` (that script handles `.pfx` certificates). Would need a different signing step. |

Nothing here can be done without buying or being issued a certificate - that is the one part that is not code.

## Turning it on

Add two **repository secrets** (Settings > Secrets and variables > Actions):

- `CODE_SIGNING_PFX_BASE64` - the `.pfx` file as base64 text: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`
- `CODE_SIGNING_PFX_PASSWORD` - its password (no double-quote characters in it)

That is all. On the next release the workflow:

1. checks the certificate (opens, has its private key, is in date, is a code-signing certificate) and fails the release if not;
2. signs the launcher and the Velopack setup through `vpk pack --signParams` (SHA-256, RFC 3161 timestamp);
3. verifies the setup carries a signature Windows trusts;
4. signs and verifies the wrapped installer (`GameLauncher-Setup.exe`);
5. deletes the decoded certificate from the runner, even if something failed.

With the secrets absent, every one of those steps is skipped and the release is unsigned exactly as before.

## Checking it locally

`installer/Code-Signing.ps1` can be run by hand (`-Mode Prepare | SignFile | VerifyFile | Cleanup`) and is covered by
`tests/GameLauncher.Tests/Installer/CodeSigningScriptTests.cs`, which signs a copy of a file with a throwaway self-signed certificate.
