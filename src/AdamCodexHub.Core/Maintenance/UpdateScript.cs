using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AdamCodexHub.Core.Maintenance;

/// <summary>
/// Writes the script that actually swaps the files.
///
/// Why a script rather than doing it here: the process that applies an update is a .NET application, and
/// the update replaces the .NET assemblies that process has already loaded. Measured 2026-09-11 —
/// running the applier in-process failed on AdamCodexHub.App.dll with "being used by another process"
/// and rolled back. Renaming helps the executable and nothing else. A child PowerShell process loads
/// none of those assemblies, can therefore write all of them, and exists on every Windows machine
/// without shipping another binary.
///
/// The decisions stay in C#: which files, which digests, what happens when one fails. The script is the
/// set of hands, not the judgement. It is generated as pure ASCII on purpose - PowerShell 5.1 reads a
/// .ps1 without a byte-order mark as ANSI, and a stray dash from a richer encoding breaks the parse
/// somewhere else entirely.
/// </summary>
public static class UpdateScript
{
    /// <summary>The file name the generated script is written under, inside the staging folder.</summary>
    public const string FileName = "apply-update.ps1";

    /// <summary>
    /// Builds the script for <paramref name="manifest"/>, targeting <paramref name="installDirectory"/>.
    /// <paramref name="relaunch"/> false is for tests, which must not leave an app running.
    /// </summary>
    public static string Generate(
        UpdatePackage.Manifest manifest,
        string stagingDirectory,
        string installDirectory,
        string logPath,
        bool relaunch = true)
    {
        var script = new StringBuilder();
        var backup = Combine(installDirectory, "update-backup-" + manifest.Version);

        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("$log = " + Literal(logPath));
        script.AppendLine("$staging = " + Literal(stagingDirectory));
        script.AppendLine("$install = " + Literal(installDirectory));
        script.AppendLine("$backup  = " + Literal(backup));
        script.AppendLine();
        script.AppendLine("function Write-Log([string]$message) {");
        script.AppendLine("    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $message");
        script.AppendLine("    Add-Content -LiteralPath $log -Value $line -Encoding UTF8");
        script.AppendLine("}");
        script.AppendLine();
        script.AppendLine("# Wait for the app to exit. Its assemblies are locked while it runs, and writing a");
        script.AppendLine("# locked file halfway is how a mixed-version install happens.");
        script.AppendLine("$deadline = (Get-Date).AddSeconds(30)");
        script.AppendLine("while ((Get-Date) -lt $deadline -and (Get-Process -Name 'AdamCodexHub.App' -ErrorAction SilentlyContinue)) {");
        script.AppendLine("    Start-Sleep -Milliseconds 400");
        script.AppendLine("}");
        script.AppendLine("Start-Sleep -Milliseconds 1500");
        script.AppendLine();

        script.AppendLine("$files = @(");
        // Joined, not terminated: PowerShell refuses a trailing comma in an array literal, and a
        // comma after the last entry is a parse error at the top of the file.
        script.AppendLine(string.Join(
            "," + Environment.NewLine,
            manifest.Files.Select(entry =>
                "    @{ path = " + Literal(entry.RelativePath) + "; sha = " + Literal(entry.Sha256) + " }")));
        script.AppendLine(")");
        script.AppendLine();
        script.AppendLine("New-Item -ItemType Directory -Force -Path $backup | Out-Null");
        script.AppendLine("$replaced = New-Object System.Collections.ArrayList");
        script.AppendLine("$created = New-Object System.Collections.ArrayList");
        script.AppendLine("$current = 0");
        script.AppendLine("try {");
        script.AppendLine("    foreach ($file in $files) {");
        script.AppendLine("        $source = Join-Path $staging ($file.path -replace '/', '\\')");
        script.AppendLine("        $target = Join-Path $install ($file.path -replace '/', '\\')");
        script.AppendLine();
        script.AppendLine("        if (-not (Test-Path -LiteralPath $source)) { throw ('the package is missing ' + $file.path) }");
        script.AppendLine("        $digest = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLower()");
        script.AppendLine("        if ($digest -ne $file.sha) { throw ($file.path + ' does not match the manifest') }");
        script.AppendLine();
        script.AppendLine("        if (Test-Path -LiteralPath $target) {");
        script.AppendLine("            $present = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLower()");
        script.AppendLine("            if ($present -eq $file.sha) { $current++; continue }");
        script.AppendLine("        }");
        script.AppendLine();
        script.AppendLine("        $folder = Split-Path -Parent $target");
        script.AppendLine("        if ($folder -and -not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }");
        script.AppendLine("        $saved = Join-Path $backup ($file.path -replace '/', '\\')");
        script.AppendLine("        $savedFolder = Split-Path -Parent $saved");
        script.AppendLine("        if ($savedFolder -and -not (Test-Path -LiteralPath $savedFolder)) { New-Item -ItemType Directory -Force -Path $savedFolder | Out-Null }");
        script.AppendLine();
        script.AppendLine("        if (Test-Path -LiteralPath $target) {");
        script.AppendLine("            Copy-Item -LiteralPath $target -Destination $saved -Force");
        script.AppendLine("            [void]$replaced.Add($file.path)");
        script.AppendLine("        } else {");
        script.AppendLine("            [void]$created.Add($file.path)");
        script.AppendLine("        }");
        script.AppendLine();
        script.AppendLine("        Copy-Item -LiteralPath $source -Destination $target -Force");
        script.AppendLine("        $after = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLower()");
        script.AppendLine("        if ($after -ne $file.sha) { throw ($file.path + ' did not verify after being written') }");
        script.AppendLine("    }");
        script.AppendLine();
        script.AppendLine("    Write-Log ('update to ' + " + Literal(manifest.Version) + " + ' applied: ' + $replaced.Count + ' file(s) replaced, ' + $current + ' already current')");
        script.AppendLine("} catch {");
        script.AppendLine("    Write-Log ('update FAILED: ' + $_.Exception.Message + ' - rolling back ' + $replaced.Count + ' file(s)')");
        script.AppendLine("    foreach ($relative in $replaced) {");
        script.AppendLine("        $target = Join-Path $install ($relative -replace '/', '\\')");
        script.AppendLine("        $saved = Join-Path $backup ($relative -replace '/', '\\')");
        script.AppendLine("        if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination $target -Force }");
        script.AppendLine("    }");
        script.AppendLine("    foreach ($relative in $created) {");
        script.AppendLine("        $target = Join-Path $install ($relative -replace '/', '\\')");
        script.AppendLine("        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force }");
        script.AppendLine("    }");
        script.AppendLine("    if (" + (relaunch ? "$true" : "$false") + ") { Start-Process (Join-Path $install 'AdamCodexHub.App.exe') }");
        script.AppendLine("    exit 1");
        script.AppendLine("}");
        if (relaunch)
        {
            script.AppendLine();
            script.AppendLine("Start-Process (Join-Path $install 'AdamCodexHub.App.exe')");
        }

        return script.ToString();
    }

    /// <summary>A PowerShell single-quoted string: only the quote itself needs escaping, by doubling it.</summary>
    private static string Literal(string value) =>
        "'" + value.Replace("'", "''") + "'";

    private static string Combine(string root, string name) =>
        root.TrimEnd('\\', '/') + "\\" + name;

    /// <summary>True when every character is ASCII, which a .ps1 file must be to parse reliably.</summary>
    public static bool IsAscii(string script)
    {
        foreach (var character in script)
        {
            if (character > 127)
            {
                return false;
            }
        }

        return true;
    }
}
