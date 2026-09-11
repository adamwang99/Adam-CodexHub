using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AdamCodexHub.Core.Tests;

/// <summary>
/// Guards the App layer's resource surface without launching WPF.
///
/// The App project has no tests of its own, and its two most repeatable failure modes are not logic
/// bugs at all — they are resources that silently go missing:
///
/// 1. A localization key used in XAML or code but defined in only one locale file. The UI then shows
///    the raw key (or Vietnamese text in the English build) and nothing fails until someone looks at
///    that exact screen in that exact language.
/// 2. An embedded image referenced by a locale but not listed in the csproj `Resource` items — the
///    guide tab renders empty in a shipped build. That one has happened before.
///
/// Both are catchable by reading files, so they are tested here. This is deliberately not a WPF test
/// suite: driving the real window needs the desktop, and the view models read the user's own
/// preferences file, so a unit test that touched them would be editing real data.
/// </summary>
public sealed class AppResourceIntegrityTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AdamCodexHub.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent!;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no AdamCodexHub.sln above the test binary).");
    }

    private static string AppDirectory => Path.Combine(Root, "src", "AdamCodexHub.App");

    private static string LocaleFile(string language) =>
        Path.Combine(AppDirectory, "Resources", "Locales", $"Locale.{language}.xaml");

    private static HashSet<string> ReadLocaleKeys(string language) =>
        Regex
            .Matches(File.ReadAllText(LocaleFile(language)), "x:Key=\"(?<key>[^\"]+)\"")
            .Select(match => match.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Every file that can reference a localization key: XAML markup and C# code.</summary>
    private static IEnumerable<string> AppSourceFiles(string extension) =>
        Directory
            .EnumerateFiles(AppDirectory, "*" + extension, SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    /// <summary>{DynamicResource L10n_X} and {StaticResource L10n_X} in markup.</summary>
    private static IEnumerable<string> KeysUsedInXaml(string file) =>
        Regex
            .Matches(File.ReadAllText(file), "Resource (L10n_[A-Za-z0-9_]+)")
            .Select(match => match.Groups[1].Value);

    /// <summary>L10n.T("L10n_X") and L10n.F("L10n_X", …) in code.</summary>
    private static IEnumerable<string> KeysUsedInCode(string file) =>
        Regex
            .Matches(File.ReadAllText(file), "L10n\\.(?:T|F)\\(\"(L10n_[A-Za-z0-9_]+)\"")
            .Select(match => match.Groups[1].Value);

    [Fact]
    public void BothLocalesDefineExactlyTheSameKeys()
    {
        var vietnamese = ReadLocaleKeys("VI");
        var english = ReadLocaleKeys("EN");

        // A key in one language only is precisely the bug this exists for: the other language shows
        // the key itself, and only on the screen that uses it.
        var missingInEnglish = vietnamese.Except(english, StringComparer.Ordinal).OrderBy(key => key).ToList();
        var missingInVietnamese = english.Except(vietnamese, StringComparer.Ordinal).OrderBy(key => key).ToList();

        Assert.True(
            missingInEnglish.Count == 0,
            $"Defined in Locale.VI.xaml but missing from Locale.EN.xaml: {string.Join(", ", missingInEnglish)}");
        Assert.True(
            missingInVietnamese.Count == 0,
            $"Defined in Locale.EN.xaml but missing from Locale.VI.xaml: {string.Join(", ", missingInVietnamese)}");
    }

    [Fact]
    public void EveryKeyUsedInXamlIsDefinedInBothLocales()
    {
        var vietnamese = ReadLocaleKeys("VI");
        var english = ReadLocaleKeys("EN");
        var unresolved = new List<string>();

        foreach (var file in AppSourceFiles(".xaml"))
        {
            foreach (var key in KeysUsedInXaml(file))
            {
                if (!vietnamese.Contains(key) || !english.Contains(key))
                {
                    unresolved.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"XAML references locale keys that are not defined:\n  {string.Join("\n  ", unresolved)}");
    }

    [Fact]
    public void EveryKeyUsedFromCodeIsDefinedInBothLocales()
    {
        var vietnamese = ReadLocaleKeys("VI");
        var english = ReadLocaleKeys("EN");
        var unresolved = new List<string>();

        foreach (var file in AppSourceFiles(".cs"))
        {
            foreach (var key in KeysUsedInCode(file))
            {
                if (!vietnamese.Contains(key) || !english.Contains(key))
                {
                    unresolved.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"Code references locale keys that are not defined:\n  {string.Join("\n  ", unresolved)}");
    }

    [Fact]
    public void EveryEmbeddedImageReferencedByALocaleIsActuallyShipped()
    {
        // A locale points at an image with pack://application:,,,/Assets/<file>; the csproj has to list
        // that same file as a Resource or the reference resolves to nothing in a published build (an
        // empty guide tab, with no error anywhere).
        var project = File.ReadAllText(Path.Combine(AppDirectory, "AdamCodexHub.App.csproj"));
        var shipped = Regex
            .Matches(project, "Assets\\\\(?<file>[^\"]+)\"")
            .Select(match => match.Groups["file"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var language in new[] { "VI", "EN" })
        {
            var text = File.ReadAllText(LocaleFile(language));
            foreach (Match match in Regex.Matches(text, "pack://application:,,,/Assets/(?<file>[^\"]+)"))
            {
                var file = match.Groups["file"].Value;
                if (!shipped.Contains(file))
                {
                    missing.Add($"Locale.{language}.xaml -> Assets/{file} is not a csproj Resource");
                }

                if (!File.Exists(Path.Combine(AppDirectory, "Assets", file)))
                {
                    missing.Add($"Locale.{language}.xaml -> Assets/{file} does not exist on disk");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            $"Locale image references that will not ship:\n  {string.Join("\n  ", missing)}");
    }

    [Fact]
    public void TheLocaleFilesStayWellFormedAndSingleBytePerLineEnding()
    {
        // EN is CRLF and VI is LF in this repo; a patch tool that assumes one can leave a file that
        // still "works" but diffs as entirely rewritten. Cheap to assert, and it has bitten before.
        foreach (var language in new[] { "VI", "EN" })
        {
            var text = File.ReadAllText(LocaleFile(language));
            Assert.Contains("L10n_Set_Title", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
        }
    }
}
