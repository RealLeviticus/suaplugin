using System;
using System.ComponentModel.Composition;
using vatsys;
using vatsys.Plugin;

namespace SuaAirspacePlugin;

[Export(typeof(IPlugin))]
public sealed class SuaPlugin : IPlugin, IDisposable
{
    private readonly SuaAirspaceService _sua;
    private readonly CloudSyncService _sync;

    private bool _disposed;

    public SuaPlugin()
    {
        var config = PluginConfig.LoadOrCreate();
        _sua = new SuaAirspaceService();
        _sync = new CloudSyncService(_sua, config);

        try
        {
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
        _sua.Dispose();
    }

    private void ReportError(Exception ex)
    {
        try { Errors.Add(ex, Name); } catch { }
    }
}
