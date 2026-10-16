using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class XamlResourceIntegrityTests
{
    // Keys resolved by the WindowsAppRuntime 1.4 theme resource dictionary
    // (XamlControlsResources). Every entry was verified to exist in
    // resources.pri of the installed Microsoft.WindowsAppRuntime 1.4 package.
    private static readonly string[] WinUiThemeAllowlist =
    {
        "AccentButtonStyle",                    // theme button style
        "AccentFillColorDefaultBrush",          // theme brush
        "AccentTextFillColorPrimaryBrush",      // theme brush
        "ApplicationPageBackgroundThemeBrush",  // theme brush
        "BodyStrongTextBlockStyle",             // theme TextBlock style
        "CaptionTextBlockStyle",                // theme TextBlock style
        "CardBackgroundFillColorSecondaryBrush", // theme brush
        "CardStrokeColorDefaultBrush",          // theme brush
        "ControlFillColorSecondaryBrush",       // theme brush
        "DefaultContentDialogStyle",            // theme ContentDialog style
        "LayerFillColorDefaultBrush",           // theme brush
        "SubtitleTextBlockStyle",               // theme TextBlock style
        "SystemColorWindowTextColor",           // theme color
        "SystemFillColorCautionBrush",          // theme brush
        "SystemFillColorCriticalBrush",         // theme brush
        "SystemFillColorCriticalBackgroundBrush", // theme brush
        "TextControlHeaderForeground",          // theme brush
        "TextFillColorSecondary",               // theme color
        "TextFillColorSecondaryBrush",          // theme brush
        "TextOnAccentFillColorPrimaryBrush",    // theme brush
        "TitleTextBlockStyle",                  // theme TextBlock style
    };

    private static readonly Regex ReferenceRegex = new(
        @"\{(?:StaticResource|ThemeResource)\s+([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    private static readonly Regex ResourceKeyReferenceRegex = new(
        """ResourceKey="([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex KeyDefinitionRegex = new(
        """x:Key="([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex InlineUiContainerRegex = new(
        @"<InlineUIContainer\b", RegexOptions.Compiled);

    [Fact]
    public void All_Xaml_Resource_References_Are_Defined_In_App_Or_Theme()
    {
        var solutionRoot = FindSolutionRoot();
        var appXamlRoot = Path.Combine(solutionRoot, "src", "managed", "Wildpinkler.App");
        Assert.True(Directory.Exists(appXamlRoot), "App XAML root not found: " + appXamlRoot);

        var xamlFiles = new List<string>(EnumerateXamlFiles(appXamlRoot));
        Assert.NotEmpty(xamlFiles);

        var definedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xamlPath in xamlFiles)
        {
            foreach (var line in File.ReadAllLines(xamlPath))
            {
                foreach (Match definition in KeyDefinitionRegex.Matches(line))
                {
                    definedKeys.Add(definition.Groups[1].Value);
                }
            }
        }

        var failures = new List<string>();
        foreach (var xamlPath in xamlFiles)
        {
            string[] lines = File.ReadAllLines(xamlPath);
            var relativePath = Path.GetRelativePath(appXamlRoot, xamlPath).Replace('\\', '/');

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match reference in ReferenceRegex.Matches(line))
                {
                    var key = reference.Groups[1].Value;
                    if (!definedKeys.Contains(key) && Array.IndexOf(WinUiThemeAllowlist, key) < 0)
                    {
                        failures.Add(relativePath + ":" + (i + 1) + " " + key);
                    }
                }

                foreach (Match reference in ResourceKeyReferenceRegex.Matches(line))
                {
                    var key = reference.Groups[1].Value;
                    if (!definedKeys.Contains(key) && Array.IndexOf(WinUiThemeAllowlist, key) < 0)
                    {
                        failures.Add(relativePath + ":" + (i + 1) + " " + key);
                    }
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Undefined XAML resources (defined neither in app XAML nor the WinUI 3 theme):\n" + string.Join("\n", failures));
    }

    // WinUI 3's XAML parser cannot add an InlineUIContainer to a TextBlock's InlineCollection
    // at runtime: it throws a XamlParseException ("Cannot add instance of type
    // 'Microsoft.UI.Xaml.Documents.InlineUIContainer' to a collection of type
    // 'Microsoft.UI.Xaml.Documents.InlineCollection'") whenever the containing template is
    // realized, crashing the app. Use inline layout (e.g. a StackPanel of FontIcon + TextBlock)
    // instead of InlineUIContainer in XAML markup.
    [Fact]
    public void No_Xaml_Uses_InlineUiContainer_Which_WinUi3_Parser_Rejects()
    {
        var solutionRoot = FindSolutionRoot();
        var appXamlRoot = Path.Combine(solutionRoot, "src", "managed", "Wildpinkler.App");
        Assert.True(Directory.Exists(appXamlRoot), "App XAML root not found: " + appXamlRoot);

        var xamlFiles = EnumerateXamlFiles(appXamlRoot);
        var failures = new List<string>();
        foreach (var xamlPath in xamlFiles)
        {
            var relativePath = Path.GetRelativePath(appXamlRoot, xamlPath).Replace('\\', '/');
            string[] lines = File.ReadAllLines(xamlPath);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (InlineUiContainerRegex.IsMatch(lines[lineIndex]))
                {
                    failures.Add(relativePath + ":" + (lineIndex + 1));
                }
            }
        }

        Assert.True(failures.Count == 0,
            "InlineUIContainer in XAML markup (rejected by the WinUI 3 XAML parser at runtime):\n" +
            string.Join("\n", failures));
    }

    [Fact]
    public void ProfilesPage_Does_Not_Expose_Removed_OutputCaptureToggle()
    {
        var solutionRoot = FindSolutionRoot();
        var pagePath = Path.Combine(solutionRoot, "src", "managed", "Wildpinkler.App", "Pages", "ProfilesPage.xaml");
        Assert.True(File.Exists(pagePath), "ProfilesPage.xaml not found: " + pagePath);

        var forbidden = new[] { "UseOutputOverlay", "Toggle output capture" };
        var failures = new List<string>();
        string[] lines = File.ReadAllLines(pagePath);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            foreach (var token in forbidden)
            {
                if (lines[lineIndex].Contains(token, StringComparison.Ordinal))
                {
                    failures.Add((lineIndex + 1) + ": " + token);
                }
            }
        }

        Assert.True(failures.Count == 0,
            "Removed output capture toggle still referenced in ProfilesPage.xaml:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ProfilesPage_ToggleSwitches_Are_OneWay_With_Toggled_Handlers()
    {
        // The Toggled handlers are the single source of truth for the bound property. A TwoWay
        // binding pushes IsOn into the model before Toggled fires, so the handlers'
        // "skip if unchanged" guards swallow the first genuine toggle and the change is never
        // committed (e.g. the tool output branch is never created on first launch).
        var solutionRoot = FindSolutionRoot();
        var pagePath = Path.Combine(solutionRoot, "src", "managed", "Wildpinkler.App", "Pages", "ProfilesPage.xaml");
        Assert.True(File.Exists(pagePath), "ProfilesPage.xaml not found: " + pagePath);

        var toggledHandlers = new[]
        {
            "FolderEnabled_Toggled",
            "ToolEnabled_Toggled",
            "CaptureOutput_Toggled",
            "PluginEnabled_Toggled"
        };

        var lines = File.ReadAllLines(pagePath);
        var failures = new List<string>();
        foreach (var handler in toggledHandlers)
        {
            var line = lines.FirstOrDefault(l => l.Contains("Toggled=\"" + handler + "\"", StringComparison.Ordinal));
            Assert.True(line is not null, "ToggleSwitch handler '" + handler + "' not found in ProfilesPage.xaml.");
            if (line!.Contains("Mode=TwoWay", StringComparison.Ordinal))
                failures.Add(line);
            if (!line.Contains("Mode=OneWay", StringComparison.Ordinal))
                failures.Add(line);
        }

        Assert.True(failures.Count == 0,
            "ToggleSwitch handlers must be bound OneWay so Toggled is the single model writer:\n" +
            string.Join("\n", failures));
    }

    [Fact]
    public void BuiltInToolDefinitions_Dont_Use_Removed_ProducesOutput_Key()
    {
        // Output capture is a per-profile property of definition-less (local) tools only; the
        // definition-level producesOutput flag was removed. Built-in definitions must not
        // reintroduce it, and FNIS/BodySlide are no longer built-in (they ship inside mods and
        // are discovered as local tools).
        var solutionRoot = FindSolutionRoot();
        var assetDirectory = Path.Combine(solutionRoot, "src", "managed", "Wildpinkler.App", "Assets", "ToolDefinitions");
        Assert.True(Directory.Exists(assetDirectory), "Assets/ToolDefinitions not found: " + assetDirectory);

        var definitions = Directory.GetFiles(assetDirectory, "*.wptool.json");
        Assert.True(definitions.Length > 0, "No built-in tool definitions found.");

        var failures = new List<string>();
        foreach (var file in definitions)
        {
            var lines = File.ReadAllLines(file);
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (lines[lineIndex].Contains("producesOutput", StringComparison.Ordinal))
                    failures.Add(Path.GetFileName(file) + ":" + (lineIndex + 1));
            }
        }

        Assert.True(failures.Count == 0,
            "Built-in tool definitions must not contain the removed producesOutput key:\n" +
            string.Join("\n", failures));

        // FNIS and BodySlide are mod-bundled tools, not built-in definitions.
        Assert.DoesNotContain(definitions, file =>
            Path.GetFileName(file).StartsWith("fnis.", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).StartsWith("bodyslide.", StringComparison.OrdinalIgnoreCase));

        // Bethini Pie has a built-in definition.
        Assert.Contains(definitions, file =>
            Path.GetFileName(file) == "bethini-pie.wptool.json");
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wildpinkler.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Wildpinkler.sln while walking up from " +
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
    }

    private static IEnumerable<string> EnumerateXamlFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (!path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                yield return path;
            }
        }
    }
}
