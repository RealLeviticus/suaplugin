using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace SuaAirspacePlugin;

public sealed class PluginConfig
{
    private const string FileName = "SuaAirspacePlugin.config.json";

    public string CloudApiUrl { get; set; } = "https://sua-airspace.pages.dev/";
    public int SyncIntervalSeconds { get; set; } = 5;
    public string InstallationId { get; set; } = "";

    public static PluginConfig LoadOrCreate()
    {
        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(directory, FileName);
        var serializer = new JavaScriptSerializer();
        PluginConfig? config = null;

        try
        {
            if (File.Exists(path))
                config = serializer.Deserialize<PluginConfig>(File.ReadAllText(path));
        }
        catch
        {
            // Invalid hand-edited config leaves cloud sync safely disabled.
        }

        config ??= new PluginConfig();
        config.SyncIntervalSeconds = Math.Max(2, Math.Min(config.SyncIntervalSeconds, 60));
        config.InstallationId = Guid.TryParse(config.InstallationId, out var installationId)
            ? installationId.ToString("N")
            : Guid.NewGuid().ToString("N");

        try { File.WriteAllText(path, serializer.Serialize(config)); }
        catch { /* A read-only plugin folder can still run without cloud sync. */ }

        return config;
    }
}
