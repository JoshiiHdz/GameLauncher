using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GameLauncher.Tests.Installer;

/// <summary>installer/Code-Signing.ps1 is what release.yml uses to sign a release when a certificate is configured. These run that very
/// script through real PowerShell and the real signtool, with a throwaway self-signed certificate (never trusted by anything, removed again).</summary>
public sealed class CodeSigningScriptTests : IDisposable
{
    private const string Password = "test-password-123";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-Signing-" + Guid.NewGuid());
    private string? _thumbprint;

    public CodeSigningScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (_thumbprint is not null)
            Ps($"Remove-Item Cert:\\CurrentUser\\My\\{_thumbprint} -ErrorAction SilentlyContinue");
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "installer", "Code-Signing.ps1");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("installer/Code-Signing.ps1 was not found above the test binaries.");
    }

    private static (int ExitCode, string Output) RunProcess(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass" }.Concat(args))
            start.ArgumentList.Add(a);

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("powershell.exe did not finish within 120 s.");
        }

        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static (int ExitCode, string Output) Script(params string[] args) =>
        RunProcess(new[] { "-File", ScriptPath() }.Concat(args));

    private static (int ExitCode, string Output) Ps(string command) => RunProcess(["-Command", command]);

    /// <summary>A self-signed code-signing certificate exported to base64 .pfx text, the way the repository secret would hold it. Built
    /// in-process rather than with PowerShell's PKI cmdlets, which are not reliably there on a hosted build machine.</summary>
    private string MakeCertificateBase64(string subject = "CN=Axis Test Signing", string eku = "1.3.6.1.5.5.7.3.3", int days = 30, string password = Password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], critical: false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(days));
        _thumbprint = cert.Thumbprint;
        return Convert.ToBase64String(cert.Export(X509ContentType.Pfx, password));
    }

    /// <summary>A real, unsigned PE file that no Windows catalog vouches for (system files are catalog-signed, which would hide a new signature).</summary>
    private static string UnsignedSample => typeof(MainWindow).Assembly.Location;

    /// <summary>A real file with an embedded signature from a publisher Windows trusts: the Windows SDK's own signtool.</summary>
    private static string TrustedSample => Directory.GetFiles(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin"), "signtool.exe", SearchOption.AllDirectories)
        .First(f => f.Contains("x64", StringComparison.OrdinalIgnoreCase));

    private string EnvFile => Path.Combine(_dir, "github-env.txt");

    private string PathFile => Path.Combine(_dir, "github-path.txt");

    private (int ExitCode, string Output) Prepare(string? base64, string? password = Password, string? pfxPath = null) =>
        Script("-Mode", "Prepare", "-PfxBase64", base64 ?? "", "-PfxPassword", password ?? "",
            "-PfxPath", pfxPath ?? Path.Combine(_dir, "signing.pfx"), "-EnvFile", EnvFile, "-PathFile", PathFile, "-NoTimestamp");

    // ---- no certificate: release stays unsigned, exactly as before ---------------------------------

    [Fact]
    public void WithNoCertificate_ItSaysTheReleaseIsUnsigned_AndSucceedsWritingNothing()
    {
        var (code, output) = Prepare(null, null);

        Assert.Equal(0, code);
        Assert.Contains("UNSIGNED", output);
        Assert.False(File.Exists(EnvFile));
        Assert.False(File.Exists(Path.Combine(_dir, "signing.pfx")));
    }

    // ---- a bad certificate must fail the release, loudly -------------------------------------------

    [Fact]
    public void ACertificateWithoutItsPassword_IsRefused()
    {
        var (code, output) = Prepare(MakeCertificateBase64(), password: "");

        Assert.NotEqual(0, code);
        Assert.Contains("without its password", output);
    }

    [Fact]
    public void ATextThatIsNotBase64_IsRefused()
    {
        var (code, output) = Prepare("this is not base64 !!!");

        Assert.NotEqual(0, code);
        Assert.Contains("not valid base64", output);
    }

    [Fact]
    public void BytesThatAreNotACertificate_AreRefused_AndNothingIsLeftBehind()
    {
        var (code, output) = Prepare(Convert.ToBase64String("just some bytes"u8.ToArray()));

        Assert.NotEqual(0, code);
        Assert.Contains("could not be opened", output);
        Assert.False(File.Exists(Path.Combine(_dir, "signing.pfx")));
    }

    [Fact]
    public void TheWrongPassword_IsRefused_AndTheDecodedFileIsRemoved()
    {
        var (code, output) = Prepare(MakeCertificateBase64(), password: "not-the-password");

        Assert.NotEqual(0, code);
        Assert.Contains("could not be opened", output);
        Assert.False(File.Exists(Path.Combine(_dir, "signing.pfx")));
    }

    [Fact]
    public void ACertificateNotForCodeSigning_IsRefused()
    {
        var (code, output) = Prepare(MakeCertificateBase64(eku: "1.3.6.1.5.5.7.3.2")); // client authentication only

        Assert.NotEqual(0, code);
        Assert.Contains("not a code-signing certificate", output);
    }

    [Fact]
    public void ADoubleQuoteInThePassword_IsRefused_BecauseItWouldBreakTheQuotedParameter()
    {
        var (code, output) = Prepare(Convert.ToBase64String("x"u8.ToArray()), password: "pass\"word");

        Assert.NotEqual(0, code);
        Assert.Contains("double quote", output);
    }

    // ---- a good certificate: prepared, used, cleaned up --------------------------------------------

    [Fact]
    public void AGoodCertificate_IsPrepared_ForVpkAndForLaterSteps()
    {
        var pfx = Path.Combine(_dir, "signing.pfx");
        var (code, output) = Prepare(MakeCertificateBase64());

        Assert.True(code == 0, output);
        Assert.Contains("Code signing is ready", output);
        Assert.True(File.Exists(pfx));

        var env = File.ReadAllLines(EnvFile);
        Assert.Contains($"AXIS_SIGN_PFX={pfx}", env);
        Assert.Contains($"AXIS_SIGN_PASSWORD={Password}", env);
        var parameters = env.Single(l => l.StartsWith("AXIS_SIGN_PARAMS=")).Substring("AXIS_SIGN_PARAMS=".Length);
        Assert.Contains($"/f \"{pfx}\"", parameters);
        Assert.Contains($"/p \"{Password}\"", parameters);
        Assert.Contains("/fd sha256", parameters);
        Assert.EndsWith("signtool.exe", Directory.GetFiles(File.ReadAllLines(PathFile).Single(), "signtool.exe").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, output); // the secret is never echoed
    }

    [Fact]
    public void SigningAFile_PutsTheCertificatesSignatureOnIt_AndLeavesTheOriginalUntouched()
    {
        var pfx = Path.Combine(_dir, "signing.pfx");
        Assert.Equal(0, Prepare(MakeCertificateBase64()).ExitCode);
        var original = Path.Combine(_dir, "tool.dll");
        File.Copy(UnsignedSample, original);
        var before = File.ReadAllBytes(original);

        var (code, output) = Script("-Mode", "SignFile", "-File", original, "-PfxPath", pfx, "-PfxPassword", Password, "-NoTimestamp");

        Assert.True(code == 0, output);
        Assert.NotEqual(before.Length, new FileInfo(original).Length); // the signature block was appended
        var (_, signer) = Ps($"(Get-AuthenticodeSignature '{original}').SignerCertificate.Thumbprint");
        Assert.Equal(_thumbprint, signer.Trim());
    }

    [Fact]
    public void Verifying_ASelfSignedFile_FailsOnPurpose_BecauseNoOneTrustsThatCertificate()
    {
        var pfx = Path.Combine(_dir, "signing.pfx");
        Assert.Equal(0, Prepare(MakeCertificateBase64()).ExitCode);
        var file = Path.Combine(_dir, "tool.dll");
        File.Copy(UnsignedSample, file);
        Assert.Equal(0, Script("-Mode", "SignFile", "-File", file, "-PfxPath", pfx, "-PfxPassword", Password, "-NoTimestamp").ExitCode);

        var (code, output) = Script("-Mode", "VerifyFile", "-File", file);

        Assert.NotEqual(0, code);
        Assert.Contains("does not carry a signature Windows trusts", output);
    }

    [Fact]
    public void Verifying_AFileSignedByARealTrustedPublisher_Passes()
    {
        // signtool.exe itself carries Microsoft's embedded signature - a publisher this machine trusts.
        var (code, output) = Script("-Mode", "VerifyFile", "-File", TrustedSample);

        Assert.True(code == 0, output);
        Assert.Contains("Verified", output);
    }

    [Fact]
    public void Verifying_AnUnsignedFile_Fails()
    {
        var file = Path.Combine(_dir, "unsigned.txt");
        File.WriteAllText(file, "hello");

        var (code, _) = Script("-Mode", "VerifyFile", "-File", file);

        Assert.NotEqual(0, code);
    }

    [Fact]
    public void SigningWithoutPreparing_OrAFileThatIsNotThere_IsRefused()
    {
        var (code, output) = Script("-Mode", "SignFile", "-File", Path.Combine(_dir, "missing.exe"), "-NoTimestamp");
        Assert.NotEqual(0, code);
        Assert.Contains("does not exist", output);

        var real = Path.Combine(_dir, "a.exe");
        File.WriteAllText(real, "x");
        var (code2, output2) = Script("-Mode", "SignFile", "-File", real, "-PfxPath", Path.Combine(_dir, "nope.pfx"), "-NoTimestamp");
        Assert.NotEqual(0, code2);
        Assert.Contains("No prepared code-signing certificate", output2);
    }

    [Fact]
    public void Cleanup_RemovesTheCertificateFile_AndIsHarmlessWhenThereIsNone()
    {
        var pfx = Path.Combine(_dir, "signing.pfx");
        File.WriteAllText(pfx, "x");

        Assert.Equal(0, Script("-Mode", "Cleanup", "-PfxPath", pfx).ExitCode);
        Assert.False(File.Exists(pfx));
        Assert.Equal(0, Script("-Mode", "Cleanup", "-PfxPath", pfx).ExitCode);
    }
}
