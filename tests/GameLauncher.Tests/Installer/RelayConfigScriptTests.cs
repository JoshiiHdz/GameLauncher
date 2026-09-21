using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using GameLauncher.Services;

namespace GameLauncher.Tests.Installer;

/// <summary>Audit finding: a release built with no relay address used to SUCCEED and quietly produce another SteamGridDB-only installer. release.yml now
/// runs installer/Check-RelayConfig.ps1 before the build (refusing a missing, plain-http or pin-less configuration) and again on the real package
/// (reading what is actually embedded in the packaged launcher). These tests run that very script, through real PowerShell.</summary>
public class RelayConfigScriptTests : IDisposable
{
    private const string Url = "https://relay.example.test";
    private const string PinA = "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string PinB = "sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GameLauncherTests-RelayScript-" + Guid.NewGuid());

    public RelayConfigScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static string ScriptPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "installer", "Check-RelayConfig.ps1");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("installer/Check-RelayConfig.ps1 was not found above the test binaries.");
    }

    private static (int ExitCode, string Output) Run(params string[] args)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath() }.Concat(args))
            start.ArgumentList.Add(a);

        using var process = Process.Start(start)!;
        process.StandardInput.Close();                        // nothing to read: PowerShell must never sit waiting on a console that isn't there
        // Both streams are drained CONCURRENTLY: reading one to the end before the other deadlocks when the process fills the other pipe first
        // (fresh CI machines make PowerShell write progress/CLIXML to stderr), and a hung child must fail the test, never hang the run.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("powershell.exe did not finish within 120 s: " + args.FirstOrDefault());
        }

        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private (int ExitCode, string Output, string OutFile) Write(string? url, string? pin)
    {
        var outFile = Path.Combine(_dir, "default-igdb-relay.txt");
        var (code, output) = Run("-Mode", "Write", "-Url", url ?? "", "-Pin", pin ?? "", "-OutFile", outFile);
        return (code, output, outFile);
    }

    // ---- before the build: a release may not proceed without a valid configuration ---------------------------------------------

    [Fact]
    public void AValidAddressAndPin_AreWritten_AsExactlyTwoLines()
    {
        var (code, _, outFile) = Write(Url, PinA);

        Assert.Equal(0, code);
        Assert.Equal(new[] { Url, PinA }, File.ReadAllLines(outFile));
    }

    [Fact]
    public void SeveralPins_ForKeyRotation_AreAccepted()
    {
        var (code, _, outFile) = Write(Url + ":8443", PinA + "," + PinB);

        Assert.Equal(0, code);
        Assert.Equal(new[] { Url + ":8443", PinA + "," + PinB }, File.ReadAllLines(outFile));
    }

    [Theory]
    [InlineData(null, PinA, "relay address is empty")]                            // the variable is unset: this used to SUCCEED silently
    [InlineData("", PinA, "relay address is empty")]
    [InlineData("   ", PinA, "relay address is empty")]
    [InlineData(Url, null, "relay key pin is empty")]                             // an address without a pin would be an unauthenticated relay
    [InlineData(Url, "", "relay key pin is empty")]
    [InlineData("http://relay.example.test", PinA, "https")]                     // plain http is refused
    [InlineData("relay.example.test", PinA, "https")]
    [InlineData("https://relay.example.test/v4/games", PinA, "bare https")]      // no path
    [InlineData("https://user:pw@relay.example.test", PinA, "bare https")]       // no credentials
    [InlineData(Url, "sha256/short=", "sha256")]
    [InlineData(Url, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "sha256")]  // no prefix
    [InlineData(Url, PinA + "," + PinA, "duplicate")]
    [InlineData(Url, PinA + "," + PinB + ",sha256/CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC=,sha256/DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD=", "between one and three")]
    public void AMissingOrInvalidConfiguration_FailsTheRelease_AndWritesNothing(string? url, string? pin, string reason)
    {
        var (code, output, outFile) = Write(url, pin);

        Assert.NotEqual(0, code);
        Assert.Contains(reason, output);
        Assert.False(File.Exists(outFile));
    }

    // ---- after packaging: the artifact users install is what gets checked ----------------------------------------------------------

    private string PackageWithTheRealLauncher()
    {
        var package = Path.Combine(_dir, "GameLauncher-9.9.9-full.nupkg");
        using var zip = ZipFile.Open(package, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(typeof(DefaultIgdbRelay).Assembly.Location, "lib/app/GameLauncher.dll");
        return package;
    }

    [Fact]
    public void ThePackageCheck_ReadsWhatIsReallyEmbedded_AndAgreesWithTheBuildsOwnConfiguration()
    {
        var package = PackageWithTheRealLauncher();
        var expected = Path.Combine(_dir, "expected.txt");
        var embedded = DefaultIgdbRelay.Current;                                  // what THIS build really embeds (or null: it has none)
        File.WriteAllLines(expected, embedded is null ? [Url, PinA] : [embedded.Address.AbsoluteUri.TrimEnd('/'), string.Join(",", embedded.Pins)]);

        var (code, output) = Run("-Mode", "VerifyPackage", "-Package", package, "-ExpectedFile", expected);

        if (embedded is null)
        {
            Assert.NotEqual(0, code);                                             // a package with no embedded configuration must FAIL the release
            Assert.Contains("contains no IGDB relay configuration", output);
        }
        else
        {
            Assert.Equal(0, code);
            Assert.Contains("Packaged relay configuration verified", output);
        }
    }

    [Fact]
    public void ThePackageCheck_FailsWhenTheEmbeddedConfigurationIsNotTheOneThatWasGiven()
    {
        if (DefaultIgdbRelay.Current is null)
            return;                                                               // the previous test covers a build with nothing embedded

        var package = PackageWithTheRealLauncher();
        var expected = Path.Combine(_dir, "expected.txt");
        File.WriteAllLines(expected, ["https://some-other-host.example.test", PinB]);

        var (code, output) = Run("-Mode", "VerifyPackage", "-Package", package, "-ExpectedFile", expected);

        Assert.NotEqual(0, code);
        Assert.Contains("differs", output);
    }

    [Fact]
    public void ThePackageCheck_FailsForAPackedLauncherThatHasNoRelayConfiguration_OnAnyMachine()
    {
        // A managed assembly that can never embed the relay file (the test assembly), presented as the packaged launcher: the ship-blind case.
        var package = Path.Combine(_dir, "GameLauncher-9.9.9-full.nupkg");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            zip.CreateEntryFromFile(typeof(RelayConfigScriptTests).Assembly.Location, "lib/app/GameLauncher.dll");
        var expected = Path.Combine(_dir, "expected.txt");
        File.WriteAllLines(expected, [Url, PinA]);

        var (code, output) = Run("-Mode", "VerifyPackage", "-Package", package, "-ExpectedFile", expected);

        Assert.NotEqual(0, code);
        Assert.Contains("contains no IGDB relay configuration", output);
    }

    [Fact]
    public void ThePackageCheck_ChecksThePackageOfTheVersionBuilt_NeverAnOlderOneThatSitsInTheSameFolder()
    {
        // The release build downloads the PREVIOUS release's packages into the same folder for the delta. "The first *-full.nupkg" was an old one with
        // no relay configuration - the guard failed the real v1.18.3 release for it. It must check exactly GameLauncher-<Version>-full.nupkg.
        var releases = Path.Combine(_dir, "Releases");
        Directory.CreateDirectory(releases);
        foreach (var name in new[] { "GameLauncher-1.0.0-full.nupkg", "GameLauncher-9.9.9-full.nupkg" })
        {
            using var zip = ZipFile.Open(Path.Combine(releases, name), ZipArchiveMode.Create);
            zip.CreateEntryFromFile(typeof(RelayConfigScriptTests).Assembly.Location, "lib/app/GameLauncher.dll");
        }

        var expected = Path.Combine(_dir, "expected.txt");
        File.WriteAllLines(expected, [Url, PinA]);

        var (_, output) = Run("-Mode", "VerifyPackage", "-PackageDirectory", releases, "-Version", "9.9.9", "-ExpectedFile", expected);

        Assert.Contains("Verifying package: GameLauncher-9.9.9-full.nupkg", output);
        Assert.DoesNotContain("1.0.0", output);
    }

    [Fact]
    public void ThePackageCheck_FailsWhenThePackageForThatVersionIsMissing_EvenIfOthersExist_AndWhenNothingIdentifiesAPackage()
    {
        var releases = Path.Combine(_dir, "Releases");
        Directory.CreateDirectory(releases);
        using (var zip = ZipFile.Open(Path.Combine(releases, "GameLauncher-1.0.0-full.nupkg"), ZipArchiveMode.Create))
            zip.CreateEntryFromFile(typeof(DefaultIgdbRelay).Assembly.Location, "lib/app/GameLauncher.dll");
        var expected = Path.Combine(_dir, "expected.txt");
        File.WriteAllLines(expected, [Url, PinA]);

        var missing = Run("-Mode", "VerifyPackage", "-PackageDirectory", releases, "-Version", "2.0.0", "-ExpectedFile", expected);
        var vague = Run("-Mode", "VerifyPackage", "-PackageDirectory", releases, "-ExpectedFile", expected);

        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("package not found", missing.Output);
        Assert.NotEqual(0, vague.ExitCode);
        Assert.Contains("-PackageDirectory together with -Version", vague.Output);
    }

    [Fact]
    public void ThePackageCheck_FailsForAPackageWithoutTheLauncher_AndForAMissingPackage()
    {
        var empty = Path.Combine(_dir, "empty-full.nupkg");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
            zip.CreateEntry("lib/app/readme.txt");
        var expected = Path.Combine(_dir, "expected.txt");
        File.WriteAllLines(expected, [Url, PinA]);

        var noDll = Run("-Mode", "VerifyPackage", "-Package", empty, "-ExpectedFile", expected);
        var missing = Run("-Mode", "VerifyPackage", "-Package", Path.Combine(_dir, "nope.nupkg"), "-ExpectedFile", expected);

        Assert.NotEqual(0, noDll.ExitCode);
        Assert.NotEqual(0, missing.ExitCode);
    }
}
