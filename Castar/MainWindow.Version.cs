using Castar.Updates;
using System;
using System.Reflection;

namespace Castar;

public partial class MainWindow
{
    public static string NavigationVersionText { get; } = ReadNavigationVersion();

    private static string ReadNavigationVersion()
    {
        var assembly = typeof(App).Assembly;
        var version = GitHubReleaseAssetService.ReadInstalledVersion(assembly);
        var versionText = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+', 2)[0];
        var label = $"Версия {(string.IsNullOrWhiteSpace(versionText) ? version.ToString() : versionText)}";
        var fileVersionText = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;

        if (Version.TryParse(fileVersionText, out var fileVersion)
            && fileVersion.Revision > 0 && fileVersion.ToString(3) == version.ToString())
            return $"{label} · сборка {fileVersion.Revision}";

        return label;
    }
}
