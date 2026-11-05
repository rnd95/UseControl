using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using UseControl.Core;
using UseControl.Core.Configuration;
using UseControl.Windows.FileSystem;

namespace UseControl.Windows.Installation;

/// <summary>One thing the installer did, as a text key (Install.*) with its values, for the user's language.</summary>
public sealed record InstallStep(string Key, params string[] Args);

/// <summary>
/// Installs UseControl from a folder with the published programs: copies them to Program Files,
/// registers and starts the service, adds uc.exe to PATH, creates shortcuts, and replaces the
/// PowerShell version (moving its configuration). Needs administrator rights.
/// </summary>
public static class Installer
{
    public const string ServiceName = "UseControl";
    public const string ServiceExe = "UseControl.Service.exe";
    public const string PanelExe = "UseControl.exe";
    public const string CliExe = "uc.exe";

    private const string EnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UseControl");

    public static string DataDirectory => RestrictionEngine.DefaultDataDirectory;

    public static bool IsInstalled() => ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<InstallStep> Install(string sourceDirectory)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectory);
        var steps = new List<InstallStep>();
        var source = Path.GetFullPath(sourceDirectory).TrimEnd('\\');
        var target = InstallDirectory;
        foreach (var exe in new[] { ServiceExe, PanelExe, CliExe })
        {
            if (!File.Exists(Path.Combine(source, exe))) throw new UcException(ErrorCodes.InstallSourceIncomplete, source, exe);
        }

        StopService(steps);
        steps.AddRange(MigrateLegacy(target));

        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(source, target);
            steps.Add(new InstallStep("Copied", target));
        }
        var open = AclInspector.WritableBy(target);
        if (open.Count > 0) throw new UcException(ErrorCodes.InstallFolderNotSafe, target, string.Join(", ", open));

        RestrictionEngine.SecureDataDirectory(DataDirectory);
        RegisterService(Path.Combine(target, ServiceExe), steps);

        if (AddToMachinePath(target)) steps.Add(new InstallStep("PathAdded", target));
        CreateShortcuts(Path.Combine(target, PanelExe));
        steps.Add(new InstallStep("Shortcuts"));

        using (var sc = new ServiceController(ServiceName))
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        }
        steps.Add(new InstallStep("Started"));
        return steps;
    }

    /// <summary>Stops and removes the service, PATH entry, shortcuts and program files. The configuration stays unless <paramref name="removeData"/>.</summary>
    public static IReadOnlyList<InstallStep> Uninstall(bool removeData)
    {
        var steps = new List<InstallStep>();
        StopService(steps);
        if (IsInstalled())
        {
            RunSc("delete", ServiceName);
            steps.Add(new InstallStep("ServiceRemoved"));
        }
        if (RemoveFromMachinePath(InstallDirectory)) steps.Add(new InstallStep("PathRemoved"));
        foreach (var lnk in ShortcutPaths()) File.Delete(lnk);

        // uc.exe may be running from the folder it deletes: finish the deletion in a separate process.
        if (Directory.Exists(InstallDirectory))
        {
            ScheduleDelete(InstallDirectory);
            steps.Add(new InstallStep("FilesRemoved", InstallDirectory));
        }
        if (removeData && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
            steps.Add(new InstallStep("DataRemoved", DataDirectory));
        }
        else if (Directory.Exists(DataDirectory))
        {
            steps.Add(new InstallStep("DataKept", DataDirectory));
        }
        return steps;
    }

    // ------------------------------------------------------------------ service

    private static void StopService(List<InstallStep> steps)
    {
        if (!IsInstalled()) return;
        using var sc = new ServiceController(ServiceName);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        steps.Add(new InstallStep("Stopped"));
    }

    private static void RegisterService(string exe, List<InstallStep> steps)
    {
        var binPath = "\"" + exe + "\"";
        if (IsInstalled())
        {
            RunSc("config", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem");
            steps.Add(new InstallStep("ServiceUpdated"));
        }
        else
        {
            RunSc("create", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "UseControl");
            steps.Add(new InstallStep("ServiceCreated"));
        }
        RunSc("description", ServiceName, "UseControl: restrictions for standard Windows accounts.");
        // Restart by itself if it ever stops unexpectedly.
        RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000");
    }

    private static void RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UcException(ErrorCodes.InstallFailed, "sc.exe");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new UcException(ErrorCodes.InstallFailed, $"sc {args[0]}: {output.Trim()}");
    }

    // ------------------------------------------------------------------ files

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            // Configuration files of the PowerShell version must not be published with the programs.
            if (Path.GetFileName(file).Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, to, overwrite: true);
        }
    }

    private static void ScheduleDelete(string folder)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add($"ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{folder}\"");
        Process.Start(psi)?.Dispose();
    }

    // ------------------------------------------------------------------ PATH and shortcuts

    /// <summary>The PATH with <paramref name="folder"/> added once (case-insensitive, trailing backslash ignored).</summary>
    public static string AddToPath(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var parts = SplitPath(path);
        if (parts.Any(p => SameFolder(p, folder))) return path ?? "";
        parts.Add(folder);
        return string.Join(';', parts);
    }

    public static string RemoveFromPath(string path, string folder) => string.Join(';', SplitPath(path).Where(p => !SameFolder(p, folder)));

    private static List<string> SplitPath(string? path) => [.. (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool SameFolder(string a, string b) => a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool AddToMachinePath(string folder) => EditMachinePath(p => AddToPath(p, folder));

    private static bool RemoveFromMachinePath(string folder) => EditMachinePath(p => RemoveFromPath(p, folder));

    private static bool EditMachinePath(Func<string, string> edit)
    {
        using var env = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(EnvironmentKey, writable: true);
        if (env is null) return false;
        var kind = env.GetValueKind("Path");
        var current = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var updated = edit(current);
        if (updated == current) return false;
        env.SetValue("Path", updated, kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        return true;
    }

    private static string[] ShortcutPaths() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "UseControl.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "UseControl.lnk"),
    ];

    /// <summary>Start menu (all users) and the installing administrator's desktop.</summary>
    private static void CreateShortcuts(string panelExe)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new UcException(ErrorCodes.InstallFailed, "WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType)!;
        foreach (var lnk in ShortcutPaths())
        {
            dynamic s = shell.CreateShortcut(lnk);
            s.TargetPath = panelExe;
            s.WorkingDirectory = Path.GetDirectoryName(panelExe);
            s.IconLocation = panelExe + ",0";
            s.Description = "UseControl";
            s.Save();
        }
    }

    // ------------------------------------------------------------------ the PowerShell version

    /// <summary>
    /// The PowerShell version lived in the same folders: scripts and config.json in Program Files\UseControl,
    /// a scheduled task for games time, a desktop shortcut. Its configuration becomes the service's
    /// configuration when the service has none yet.
    /// </summary>
    internal static IEnumerable<InstallStep> MigrateLegacy(string programFolder)
    {
        var legacyConfig = Path.Combine(programFolder, "config.json");
        var serviceConfig = Path.Combine(DataDirectory, "config.json");
        if (File.Exists(legacyConfig) && !File.Exists(serviceConfig))
        {
            var cfg = ConfigSerializer.Load(legacyConfig);
            RestrictionEngine.SecureDataDirectory(DataDirectory);
            ConfigSerializer.Save(cfg, serviceConfig);
            yield return new InstallStep("LegacyConfigMoved", string.Join(", ", cfg.Users));
        }

        foreach (var task in new[] { "UseControl games lock", "Family-PC games lock" })
        {
            if (RunSchtasksDelete(task)) yield return new InstallStep("LegacyTaskRemoved", task);
        }

        var removed = false;
        foreach (var f in new[] { "UseControl.ps1", "UseControl-GUI.ps1", "config.sample.json", "config.json", "README.md", "uc.ico" })
        {
            var p = Path.Combine(programFolder, f);
            if (File.Exists(p)) { File.Delete(p); removed = true; }
        }
        foreach (var f in Directory.Exists(programFolder) ? Directory.EnumerateFiles(programFolder, "config.*.bak.json").ToList() : [])
        {
            File.Delete(f);
            removed = true;
        }
        var lib = Path.Combine(programFolder, "lib");
        if (Directory.Exists(lib) && Directory.EnumerateFiles(lib, "*.ps1").Any())
        {
            Directory.Delete(lib, recursive: true);
            removed = true;
        }
        foreach (var old in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Family-PC"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Family-PC") })
        {
            if (Directory.Exists(old)) { Directory.Delete(old, recursive: true); removed = true; }
        }
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var lnk in new[] { "Family-PC.lnk" })
        {
            var p = Path.Combine(desktop, lnk);
            if (File.Exists(p)) { File.Delete(p); removed = true; }
        }
        if (removed) yield return new InstallStep("LegacyFilesRemoved");
    }

    private static bool RunSchtasksDelete(string task)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "/Delete", "/TN", task, "/F" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
