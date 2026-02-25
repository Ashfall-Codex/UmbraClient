using System.Reflection;

namespace UmbraSync;

public static class Constants
{
    public static readonly Version PluginVersionObj =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    public static readonly string PluginVersion = $"{PluginVersionObj.Major}.{PluginVersionObj.Minor}.{PluginVersionObj.Build}";
    public static readonly string PluginBuild = PluginVersionObj.Revision.ToString();

    public const string DiscordUrl = "https://discord.gg/2zJB7DjAs9";
    public const string GitHubUrl = "https://github.com/Ashfall-Codex/UmbraClient";
}
