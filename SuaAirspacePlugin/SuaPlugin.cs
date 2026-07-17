using System;
using System.ComponentModel.Composition;
using vatsys;
using vatsys.Plugin;

namespace SuaAirspacePlugin;

[Export(typeof(IPlugin))]
public sealed class SuaPlugin : IPlugin, IDisposable
{
    private const int WebPort = 5300;

    private readonly SuaAirspaceService _sua;
    private readonly SuaNotamService _notam;
    private readonly EmbeddedWebServer _server;
    private readonly CloudSyncService _sync;

    private bool _disposed;

    public SuaPlugin()
    {
        var config = PluginConfig.LoadOrCreate();
        _sua = new SuaAirspaceService();
        _notam = new SuaNotamService(_sua);
        _server = new EmbeddedWebServer(_sua, _notam, config, WebPort);
        _sync = new CloudSyncService(_sua, config);

        try
        {
            _server.Start();
            _sync.Start();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    public string Name => "SUA Airspace Plugin";

    public void OnFDRUpdate(FDP2.FDR updated) { }

    public void OnRadarTrackUpdate(RDP.RadarTrack updated) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sync.Dispose();
        _server.Dispose();
        _sua.Dispose();
    }

    private void ReportError(Exception ex)
    {
        try { Errors.Add(ex, Name); } catch { }
    }
}
