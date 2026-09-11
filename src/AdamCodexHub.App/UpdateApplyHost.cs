using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using AdamCodexHub.Core.Maintenance;

namespace AdamCodexHub.App;

/// <summary>
/// The half of the updater that runs while the app is closed.
///
/// Windows will not let a running application overwrite its own assemblies, so the app stages the
/// package, launches a second copy of itself with <c>--apply-update</c>, and exits. That second copy
/// waits for the first to disappear, swaps the files, and starts the app again. It is deliberately the
/// same executable: nothing new to distribute, nothing new to trust, and no separate binary to keep in
/// step with the app it is replacing.
/// </summary>
internal static class UpdateApplyHost
{
    private const string Flag = "--apply-update";

    /// <summary>
    /// True when this process was started to apply an update, in which case it must not become a normal
    /// app instance. Called before the single-instance mutex, because an update helper is by design a
    /// second process.
    /// </summary>
    public static bool IsUpdateHelper(string[] args) =>
        args.Any(argument => string.Equals(argument, Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Waits for the running app to exit, applies the staged package, and relaunches. Writes what it did
    /// to <c>%LOCALAPPDATA%\AdamCodexHub\logs\update.log</c> — the one place a failed update can explain
    /// itself, since by then there is no UI left to say anything.
    /// </summary>
    public static void Run(string[] args)
    {
        var staging = Argument(args, 0);
        var installDirectory = Argument(args, 1) ?? AppContext.BaseDirectory;
        var relaunch = Argument(args, 2) ?? "1";

        Log($"update helper started: staging={staging} install={installDirectory}");

        try
        {
            WaitForTheAppToExit();

            if (string.IsNullOrWhiteSpace(staging) || !Directory.Exists(staging))
            {
                Log("no staged update found; nothing to do");
                return;
            }

            var manifestPath = Path.Combine(staging, "update-manifest.json");
            if (!File.Exists(manifestPath))
            {
                Log("the staged update has no manifest; refusing to touch the installation");
                return;
            }

            var manifest = UpdatePackage.Parse(File.ReadAllText(manifestPath), out var rejection);
            if (manifest is null)
            {
                Log($"the staged manifest was refused: {rejection?.Reason}");
                return;
            }

            var result = UpdateApplier.Apply(staging, installDirectory, manifest);
            Log(result.Succeeded
                ? $"update to {manifest.Version} applied: {result.Replaced} file(s) replaced, " +
                  $"{result.AlreadyCurrent} already current, backup at {result.BackupDirectory}"
                : $"update to {manifest.Version} FAILED: {result.Failure}");

            if (!result.Succeeded)
            {
                // The installation was rolled back, so the honest thing is to say so rather than let the
                // user find out later.
                MessageBox.Show(
                    "The update could not be applied and the previous version was restored.\n\n" +
                    result.Failure + "\n\nDetails: " + LogPath,
                    "Adam CodexHub",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            Log($"update helper crashed: {ex}");
        }
        finally
        {
            if (relaunch == "1")
            {
                Relaunch(installDirectory);
            }
        }
    }

    /// <summary>
    /// Gives the outgoing app up to 15 seconds to go away. If it is still there, the files are still
    /// locked and writing them would fail halfway, so the helper stops instead.
    /// </summary>
    private static void WaitForTheAppToExit()
    {
        var self = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            var others = Process.GetProcessesByName("AdamCodexHub.App")
                .Where(process => process.Id != self)
                .ToList();

            foreach (var process in others)
            {
                process.Dispose();
            }

            if (others.Count == 0)
            {
                // A moment more: the process being gone and its file handles being released are not
                // quite the same event.
                System.Threading.Thread.Sleep(1200);
                return;
            }

            System.Threading.Thread.Sleep(400);
        }

        Log("the running app did not exit in time; applying anyway (some files may be locked)");
    }

    private static void Relaunch(string installDirectory)
    {
        try
        {
            var executable = Path.Combine(installDirectory, "AdamCodexHub.App.exe");
            if (File.Exists(executable))
            {
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
                Log("app relaunched");
            }
        }
        catch (Exception ex)
        {
            Log($"could not relaunch: {ex.Message}");
        }
    }

    private static string? Argument(string[] args, int indexAfterFlag)
    {
        var flagIndex = Array.FindIndex(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));
        if (flagIndex < 0)
        {
            return null;
        }

        var target = flagIndex + 1 + indexAfterFlag;
        return target < args.Length && !args[target].StartsWith("--", StringComparison.Ordinal) ? args[target] : null;
    }

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdamCodexHub",
        "logs",
        "update.log");

    private static void Log(string message)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The last apply attempt, for the UI to show on the next start.</summary>
    public static string? LastResultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdamCodexHub",
        "last-update.json");
}
