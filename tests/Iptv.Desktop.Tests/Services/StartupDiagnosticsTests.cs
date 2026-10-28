using System.Reflection;
using Iptv.Desktop;

namespace Iptv.Desktop.Tests.Services;

public class StartupDiagnosticsTests
{
    [Fact]
    public void TopLevelStartup_ReturnsAnExitCode()
    {
        var main = typeof(App).Assembly.GetType("Iptv.Desktop.Program")!.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(typeof(int), main.ReturnType);
    }
    [Fact]
    public void StartupDiagnosticBoundary_IsAvailableBeforeUiInitialization()
        => Assert.NotNull(typeof(App).Assembly.GetType("Iptv.Desktop.StartupDiagnostics"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactRenderingFlag_RoutesSingleVariableAndPreservesExitCode(bool software)
    {
        var called = false;
        var result = Program.Run(software ? new[] { "--software-rendering" } : Array.Empty<string>(), option =>
        {
            called = true;
            Assert.Equal(software, option);
            return 17;
        });
        Assert.True(called);
        Assert.Equal(17, result);
        Assert.Null(StartupDiagnostics.Current);
        Assert.Equal(0, Program.Run(Array.Empty<string>(), _ => 0));
    }

    [Theory]
    [InlineData("--software-rendering=true")]
    [InlineData("--Software-rendering")]
    [InlineData("--unknown=fixture-secret")]
    [InlineData("--startup-log")]
    public void InvalidFlag_DoesNotLaunchDefaultApplication(string flag)
    {
        var launched = false;
        Assert.NotEqual(0, Program.Run(new[] { flag }, _ => { launched = true; return 0; }));
        Assert.False(launched);
    }

    [Fact]
    public void DuplicateAndRelativeOptions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => StartupDiagnostics.Parse(new[] { "--software-rendering", "--software-rendering" }));
        Assert.Throws<ArgumentException>(() => StartupDiagnostics.Parse(new[] { "--startup-log", "relative.log" }));
        var file = Path.Combine(Path.GetTempPath(), "fixture.log");
        Assert.Throws<ArgumentException>(() => StartupDiagnostics.Parse(new[] { "--startup-log", file, "--startup-log", file }));
        Assert.Throws<ArgumentException>(() => StartupDiagnostics.Parse(new[] { "--startup-log", "--software-rendering" }));
    }

    [Fact]
    public void ManagedFailure_EmitsOnlySafeMetadata_AndNonzeroExit()
    {
        using var fixture = new LogFixture();
        var result = Program.Run(new[] { "--startup-log", fixture.LogPath, "--software-rendering" }, _ =>
        {
            StartupDiagnostics.Current!.Stage(StartupStage.WindowConstructed);
            StartupDiagnostics.Current!.Stage(StartupStage.WindowOpened);
            throw new InvalidOperationException(@"fixture-sentinel https://secret.invalid/token?password=secret C:\private\file", new IOException("inner-sentinel"));
        });
        Assert.NotEqual(0, result);
        var log = File.ReadAllText(fixture.LogPath);
        Assert.Contains("rendering=software", log);
        Assert.Contains("stage=Enter", log);
        Assert.Contains("stage=WindowConstructed", log);
        Assert.Contains("stage=WindowOpened", log);
        Assert.Contains("System.InvalidOperationException", log);
        Assert.Contains("System.IO.IOException", log);
        Assert.Contains("hresult=0x", log);
        Assert.Contains("method=Program.Run", log);
        Assert.DoesNotContain("fixture-sentinel", log);
        Assert.DoesNotContain("inner-sentinel", log);
        Assert.DoesNotContain("secret.invalid", log);
        Assert.DoesNotContain(fixture.Root, log);
        Assert.DoesNotContain("private", log);
    }

    [Fact]
    public void LogCreateFailure_IsNonzeroAndDoesNotStartApplication()
    {
        using var fixture = new LogFixture();
        var launched = false;
        var exit = Program.Run(new[] { "--startup-log", fixture.Root }, _ => { launched = true; return 0; });
        Assert.False(launched);
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public void LogWriteFailure_DoesNotReplaceStartupFailureOrReturnZero()
    {
        using var fixture = new LogFixture();
        var result = Program.Run(new[] { "--startup-log", fixture.LogPath }, _ =>
        {
            StartupDiagnostics.Current!.Dispose();
            throw new IOException("fixture-sensitive-after-dispose");
        });
        Assert.NotEqual(0, result);
        Assert.DoesNotContain("fixture-sensitive-after-dispose", File.ReadAllText(fixture.LogPath));
    }

    private sealed class LogFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "startup-diagnostics-" + Guid.NewGuid().ToString("N"));
        public string LogPath => Path.Combine(Root, "startup.log");
        public LogFixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "startup-diagnostics-"), path);
            Directory.Delete(path, true);
        }
    }
}
