using Xunit;

namespace RoboMouse.Service.Tests;

/// <summary>Who may use the control pipe (plans/uac-service.md, security boundary).</summary>
public class CallerPolicyTests
{
    private const string AppPath = @"C:\Program Files\RoboMouse\RoboMouse.App.exe";
    private const string Family = "12345TimDodd.RoboMouse_abcdefghjkmnp";
    private const string PackageDir = @"C:\Program Files\WindowsApps\12345TimDodd.RoboMouse_1.1.5.0_x64__abcdefghjkmnp";
    private const string Issuer = "CN=Microsoft ID Verified CS EOC CA 01, O=Microsoft Corporation, C=US";
    private const string ValidationOid = "1.3.6.1.4.1.311.97.123456789.1";
    private static readonly SignerIdentity Publisher = new("CN=Tim Dodd, O=Tim Dodd, C=US", Issuer, [ValidationOid]);
    private const uint Console = 2;
    private const long ConnectedAt = 1_000_000;

    private sealed class FakeProcess : IClientProcess
    {
        public uint SessionId { get; set; } = Console;
        public long CreationTime { get; set; } = ConnectedAt - 10;
        public bool HasExited { get; set; }
        public string? ImagePath { get; set; } = AppPath;
        public string? PackageFamilyName { get; set; }
        public string? PackageInstallPath { get; set; }
        /// <summary>Simulates the process changing under the check (read once, then different).</summary>
        public Action? OnImagePathRead { get; set; }
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeInspector(FakeProcess? process) : IProcessInspector
    {
        public IClientProcess? Open(uint pid) => process == null ? null : new ReadHook(process);

        // Wraps the fake so reading the image path can mutate it mid-check.
        private sealed class ReadHook(FakeProcess inner) : IClientProcess
        {
            public uint SessionId => inner.SessionId;
            public long CreationTime => inner.CreationTime;
            public bool HasExited => inner.HasExited;
            public string? ImagePath { get { var p = inner.ImagePath; inner.OnImagePathRead?.Invoke(); return p; } }
            public string? PackageFamilyName => inner.PackageFamilyName;
            public string? PackageInstallPath => inner.PackageInstallPath;
            public void Dispose() => inner.Dispose();
        }
    }

    private sealed class FakeSignatures(Dictionary<string, SignerIdentity> signers) : ISignatureReader
    {
        public Dictionary<string, SignatureCheck> Results { get; } = new();

        public SignatureCheck Check(string path) =>
            Results.TryGetValue(path, out var result) ? result
            : signers.TryGetValue(path, out var s) ? SignatureCheck.SignedBy(s) : SignatureCheck.Unsigned;
    }

    private static readonly PipeCaller Caller = new(ProcessId: 4242, PipeSessionId: Console, ConsoleSessionId: Console, ConnectedAt: ConnectedAt);

    private static CallerPolicy Policy(FakeProcess? process, SignerIdentity? serviceSigner = null, string? family = Family,
        Dictionary<string, SignerIdentity>? signers = null, bool unsignedService = false) =>
        new(AppPath, family, unsignedService ? null : serviceSigner ?? Publisher, new FakeInspector(process),
            new FakeSignatures(signers ?? new() { [AppPath] = Publisher }));

    private static FakeProcess StoreProcess() => new()
    {
        ImagePath = PackageDir + @"\RoboMouse.App.exe",
        PackageFamilyName = Family,
        PackageInstallPath = PackageDir
    };

    private static void AssertRejected(CallerPolicy policy, PipeCaller caller, string because)
    {
        Assert.False(policy.IsAllowed(caller, out var reason));
        Assert.Contains(because, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstalledApp_SignedByTheServicePublisher_IsAllowed()
    {
        var process = new FakeProcess();
        Assert.True(Policy(process).IsAllowed(Caller, out var reason), reason);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void InstalledApp_PathComparedCaseInsensitively()
    {
        var process = new FakeProcess { ImagePath = AppPath.ToUpperInvariant() };
        Assert.True(Policy(process, signers: new() { [AppPath.ToUpperInvariant()] = Publisher }).IsAllowed(Caller, out var reason), reason);
    }

    [Fact]
    public void StoreApp_InsideItsPackage_IsAllowed_WithoutAFileSignature()
    {
        Assert.True(Policy(StoreProcess()).IsAllowed(Caller, out var reason), reason);
    }

    [Fact]
    public void ExitedClient_IsRejected() =>
        AssertRejected(Policy(process: null), Caller, "exited");

    [Fact]
    public void ReusedPid_CreatedAfterTheConnection_IsRejected() =>
        AssertRejected(Policy(new FakeProcess { CreationTime = ConnectedAt + 1 }), Caller, "started after the connection");

    [Fact]
    public void ProcessReplacedDuringTheCheck_IsRejected()
    {
        var process = new FakeProcess();
        process.OnImagePathRead = () => process.CreationTime += 1;
        AssertRejected(Policy(process), Caller, "exited during the check");
    }

    [Fact]
    public void ClientThatExitsDuringTheCheck_IsRejected()
    {
        var process = new FakeProcess();
        process.OnImagePathRead = () => process.HasExited = true;
        AssertRejected(Policy(process), Caller, "exited during the check");
    }

    [Fact]
    public void ClientInAnotherSession_IsRejected() =>
        AssertRejected(Policy(new FakeProcess { SessionId = 3 }), Caller with { PipeSessionId = 3 }, "not the console session");

    [Fact]
    public void PipeSessionThatDisagreesWithTheProcess_IsRejected() =>
        AssertRejected(Policy(new FakeProcess()), Caller with { PipeSessionId = 5 }, "pipe reports session 5");

    [Theory]
    [InlineData(@"C:\Users\tim\Downloads\RoboMouse.App.exe")]
    [InlineData(@"C:\Program Files\RoboMouse\Other.exe")]
    [InlineData(@"C:\Program Files\RoboMouse\RoboMouse.App.exe.bak")]
    public void WrongPath_IsRejected(string path) =>
        AssertRejected(Policy(new FakeProcess { ImagePath = path }), Caller, "expected");

    [Fact]
    public void UnreadablePath_IsRejected() =>
        AssertRejected(Policy(new FakeProcess { ImagePath = null }), Caller, "could not be read");

    [Fact]
    public void UnsignedApp_IsRejected_WhenTheServiceIsSigned() =>
        AssertRejected(Policy(new FakeProcess(), signers: new()), Caller, "no valid signature");

    [Fact]
    public void AppSignedBySomeoneElse_IsRejected() =>
        AssertRejected(Policy(new FakeProcess(), signers: new() { [AppPath] = Publisher with { Subject = "CN=Mallory" } }), Caller, "signed by 'CN=Mallory'");

    [Fact]
    public void UnsignedService_ChecksThePathOnly()
    {
        // Install-DevService.ps1 builds are unsigned; the path is still enforced.
        Assert.True(Policy(new FakeProcess(), unsignedService: true, signers: new()).IsAllowed(Caller, out var reason), reason);
        AssertRejected(Policy(new FakeProcess { ImagePath = @"C:\Temp\RoboMouse.App.exe" }, unsignedService: true), Caller, "expected");
    }

    [Fact]
    public void AppWithTheSameSubject_FromAnotherCA_IsRejected() =>
        AssertRejected(Policy(new FakeProcess(), signers: new() { [AppPath] = Publisher with { Issuer = "CN=Some Other CA" } }),
            Caller, "signed through 'CN=Some Other CA'");

    [Fact]
    public void AppWithoutTheServicesValidatedIdentity_IsRejected()
    {
        // Same subject and CA, but another Artifact Signing account (its own validation EKU, or none).
        AssertRejected(Policy(new FakeProcess(), signers: new() { [AppPath] = Publisher with { ValidationOids = [] } }),
            Caller, "validated identity");
        AssertRejected(Policy(new FakeProcess(), signers: new() { [AppPath] = Publisher with { ValidationOids = ["1.3.6.1.4.1.311.97.999.1"] } }),
            Caller, "validated identity");
    }

    [Fact]
    public void ServiceWithoutAValidationEku_ComparesSubjectAndIssuer()
    {
        var plain = new SignerIdentity(Publisher.Subject, Issuer);
        Assert.True(Policy(new FakeProcess(), serviceSigner: plain, signers: new() { [AppPath] = Publisher }).IsAllowed(Caller, out var reason), reason);
    }

    [Fact]
    public void AppWithAnInvalidSignature_IsRejected()
    {
        var signatures = new FakeSignatures(new());
        signatures.Results[AppPath] = SignatureCheck.Failed("WinVerifyTrust returned 0x800B0109");
        var policy = new CallerPolicy(AppPath, Family, Publisher, new FakeInspector(new FakeProcess()), signatures);

        AssertRejected(policy, Caller, "0x800B0109");
    }

    [Fact]
    public void ServiceSignatureThatCannotBeChecked_FailsClosed_AndIsCheckedAgain()
    {
        var calls = 0;
        var service = new ServiceSignature(() => ++calls == 1
            ? SignatureCheck.Failed("chain could not be built")
            : SignatureCheck.SignedBy(Publisher));
        var policy = new CallerPolicy(AppPath, Family, service, new FakeInspector(new FakeProcess()),
            new FakeSignatures(new() { [AppPath] = Publisher }));

        // Not treated as an unsigned dev build (which would check the path only).
        AssertRejected(policy, Caller, "could not be checked");
        Assert.True(policy.IsAllowed(Caller, out var reason), reason);
        Assert.True(policy.IsAllowed(Caller, out reason), reason);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void UnsignedService_IsRememberedAfterOneCheck()
    {
        var calls = 0;
        var service = new ServiceSignature(() => { calls++; return SignatureCheck.Unsigned; });
        var policy = new CallerPolicy(AppPath, Family, service, new FakeInspector(new FakeProcess()), new FakeSignatures(new()));

        Assert.True(policy.IsAllowed(Caller, out var reason), reason);
        Assert.True(policy.IsAllowed(Caller, out reason), reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void StoreIdentity_FromAFolderOutsideThePackage_IsRejected()
    {
        // Package identity alone is not enough: the exe must be the package's own copy.
        var process = StoreProcess();
        process.ImagePath = @"C:\Users\tim\AppData\Local\Temp\RoboMouse.App.exe";
        AssertRejected(Policy(process), Caller, "not inside its package folder");
    }

    [Fact]
    public void StoreIdentity_WithDotDotEscapingThePackage_IsRejected()
    {
        var process = StoreProcess();
        process.ImagePath = PackageDir + @"\..\evil\RoboMouse.App.exe";
        AssertRejected(Policy(process), Caller, "not inside its package folder");
    }

    [Fact]
    public void StoreApp_WithAnotherPackageFamily_IsRejected()
    {
        var process = StoreProcess();
        process.PackageFamilyName = "Someone.Else_1234567890abc";
        AssertRejected(Policy(process), Caller, "package family");
    }

    [Fact]
    public void StoreApp_IsRejected_WhenTheServiceWasNotToldAboutIt() =>
        AssertRejected(Policy(StoreProcess(), family: null), Caller, "expected");

    [Fact]
    public void StoreApp_WithAnotherExeName_IsRejected()
    {
        var process = StoreProcess();
        process.ImagePath = PackageDir + @"\Tool.exe";
        AssertRejected(Policy(process), Caller, "expected");
    }
}
