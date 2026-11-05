using UseControl.Core;
using UseControl.Core.Paths;
using UseControl.Localization;
using UseControl.Windows.Installation;

namespace UseControl.Cli.Tests;

[Collection("Culture")]
public sealed class InstallTests : IDisposable
{
    private readonly FakeService _svc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private string? _installedFrom;
    private bool? _purged;

    public InstallTests() => Loc.Culture = Loc.Pick("en");

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }

    private CliApp App(bool admin = true, string input = "") => new(_svc, _out, _err, new StringReader(input), new FixedSystemPaths())
    {
        IsAdministrator = () => admin,
        InstallFrom = from => { _installedFrom = from; return [new InstallStep("Copied", @"C:\Program Files\UseControl"), new InstallStep("Started")]; },
        UninstallAll = purge => { _purged = purge; return [new InstallStep("ServiceRemoved")]; },
        ServiceStartWait = TimeSpan.FromMilliseconds(200),
    };

    [Fact]
    public async Task Install_needs_administrator_rights()
    {
        Assert.Equal(CliApp.Failed, await App(admin: false).RunAsync(["install"]));
        Assert.Null(_installedFrom);
        Assert.Contains("administrator", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_reports_every_step_and_waits_for_the_service()
    {
        Assert.Equal(CliApp.Ok, await App().RunAsync(["install", "--from", @"D:\publish"]));
        Assert.Equal(@"D:\publish", _installedFrom);
        Assert.Contains(@"Programs copied to C:\Program Files\UseControl.", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("installed and running", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_without_from_uses_the_folder_of_uc_exe()
    {
        await App().RunAsync(["install"]);
        Assert.Equal(AppContext.BaseDirectory, _installedFrom);
    }

    [Fact]
    public async Task Uninstall_lifts_restrictions_first_after_confirmation()
    {
        Assert.Equal(CliApp.Failed, await App(input: "no\n").RunAsync(["uninstall"]));
        Assert.Null(_purged);
        Assert.True(_svc.Enabled);

        Assert.Equal(CliApp.Ok, await App(input: "YES\n").RunAsync(["uninstall", "--purge"]));
        Assert.False(_svc.Enabled);
        Assert.True(_purged);
        Assert.Contains("Service removed.", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_can_keep_the_restrictions()
    {
        Assert.Equal(CliApp.Ok, await App().RunAsync(["uninstall", "--keep-restrictions", "--yes"]));
        Assert.True(_svc.Enabled);
        Assert.False(_purged);
    }

    [Theory]
    [InlineData(@"C:\Windows;C:\Tools", @"C:\Program Files\UseControl", @"C:\Windows;C:\Tools;C:\Program Files\UseControl")]
    [InlineData(@"C:\Windows;c:\program files\usecontrol\", @"C:\Program Files\UseControl", @"C:\Windows;c:\program files\usecontrol\")]
    [InlineData("", @"C:\X", @"C:\X")]
    public void Folder_is_added_to_PATH_once(string path, string folder, string expected) =>
        Assert.Equal(expected, Installer.AddToPath(path, folder));

    [Fact]
    public void Folder_is_removed_from_PATH_keeping_the_rest() =>
        Assert.Equal(@"C:\Windows;%SystemRoot%\System32", Installer.RemoveFromPath(@"C:\Windows;C:\Program Files\UseControl\;;%SystemRoot%\System32", @"C:\Program Files\UseControl"));

    [Fact]
    public void Install_refuses_a_folder_without_the_programs()
    {
        var empty = Path.Combine(Path.GetTempPath(), "uc-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Equal(ErrorCodes.InstallSourceIncomplete, Assert.Throws<UcException>(() => Installer.Install(empty)).Code);
        }
        finally
        {
            Directory.Delete(empty);
        }
    }
}
