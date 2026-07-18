using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Web.Script.Serialization;

namespace SuaAirspacePlugin;

/// <summary>
/// Publishes the immutable profile catalogue/default schedules captured at
/// startup plus controller-created vatSys differences, then reconciles shared
/// desired state from Cloudflare. Activations injected by this service are
/// excluded from uploads to prevent feedback. Only outbound HTTPS is used.
/// </summary>
public sealed class CloudSyncService : IDisposable
{
    private readonly SuaAirspaceService _sua;
    private readonly PluginConfig _config;
    private readonly HttpClient _http;
    private readonly JavaScriptSerializer _json;
    private readonly System.Timers.Timer? _timer;
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedNames = new(StringComparer.OrdinalIgnoreCase);
    private int _running;
    private bool _wasConnected;
    private bool _disconnectPending;
    private bool _disposed;

    public CloudSyncService(SuaAirspaceService sua, PluginConfig config)
    {
        _sua = sua;
        _config = config;
        _json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        if (string.IsNullOrWhiteSpace(config.CloudApiUrl))
            return;

        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        _timer = new System.Timers.Timer(config.SyncIntervalSeconds * 1000.0) { AutoReset = true };
        _timer.Elapsed += OnElapsed;
    }

    public void Start()
    {
        if (_timer is null || _disposed) return;
        _timer.Start();
        _ = SyncWhenReadyAsync();
    }

    private void OnElapsed(object? sender, ElapsedEventArgs e) => _ = SyncOnceAsync();

    private async Task SyncWhenReadyAsync()
    {
        while (!_disposed && !_sua.SyncReady)
            await Task.Delay(250).ConfigureAwait(false);

        if (!_disposed)
            await SyncOnceAsync().ConfigureAwait(false);
    }

    private async Task SyncOnceAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _running, 1) != 0) return;
        try
        {
            if (!_sua.IsConnected)
            {
                if (_wasConnected)
                {
                    _wasConnected = false;
                    _disconnectPending = true;
                    _managedNames.Clear();
                    _fingerprints.Clear();
                    _sua.HandleNetworkDisconnected();
                }

                if (_disconnectPending && await SendDisconnectAsync().ConfigureAwait(false))
                    _disconnectPending = false;
                return;
            }

            if (!_sua.AreasLoaded) return;
            _wasConnected = true;
            _disconnectPending = false;

            var endpoint = _config.CloudApiUrl.TrimEnd('/') + "/api/plugin/sync";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            var leaseSeconds = Math.Max(30, _config.SyncIntervalSeconds * 3);
            request.Content = new StringContent(
                _json.Serialize(_sua.GetCloudSnapshot(_config.InstallationId, leaseSeconds)),
                Encoding.UTF8,
                "application/json");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var result = _json.Deserialize<SyncResponse>(body);
            if (result?.Success != true) return;
            ApplyDesired(result.Desired ?? new List<DesiredActivation>());
        }
        catch
        {
            // vatSys must remain unaffected by network or cloud availability.
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<bool> SendDisconnectAsync()
    {
        try
        {
            var endpoint = _config.CloudApiUrl.TrimEnd('/') + "/api/plugin/sync";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Content = new StringContent(_json.Serialize(new
            {
                InstallationId = _config.InstallationId,
                Disconnected = true,
            }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return _json.Deserialize<SyncResponse>(body)?.Success == true;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyDesired(List<DesiredActivation> desired)
    {
        var incoming = desired
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var previous in _managedNames.Where(name => !incoming.ContainsKey(name)).ToList())
        {
            _sua.Deactivate(previous);
            _managedNames.Remove(previous);
            _fingerprints.Remove(previous);
        }

        foreach (var pair in incoming)
        {
            var item = pair.Value;
            var nowWire = DateTime.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
            var windows = (item.Windows ?? new List<string>())
                .Where(value => value is not null && value.Length == 25 && value[12] == '-' &&
                    string.CompareOrdinal(value.Substring(13), nowWire) > 0)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var fingerprint = (item.H24 ? "1" : "0") + "|" + string.Join(",", windows) + "|" +
                item.Floor?.ToString(CultureInfo.InvariantCulture) + "|" +
                item.Ceiling?.ToString(CultureInfo.InvariantCulture) + "|" +
                (item.LinePattern ?? "") + "|" + (item.RaCategory ?? "");
            if (_fingerprints.TryGetValue(item.Name, out var current) && current == fingerprint) continue;

            var applied = true;
            if (item.Floor.HasValue || item.Ceiling.HasValue)
                applied = _sua.TrySetLevels(item.Name, item.Floor, item.Ceiling);

            if (item.H24)
                applied = _sua.TryActivate(item.Name, 0) && applied;
            else
                applied = _sua.TrySetWindows(item.Name, string.Join(",", windows)) && applied;

            if (!applied) continue;

            // Applying the owner's draw style only when the fingerprint changes
            // means a local restyle by a non-activating controller survives every
            // subsequent sync until the activating controller changes it again.
            _sua.TryApplyRaCategory(item.Name, item.RaCategory, item.LinePattern);

            _managedNames.Add(item.Name);
            _fingerprints[item.Name] = fingerprint;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Stop();
        _timer?.Dispose();
        _http.Dispose();
    }

    private sealed class SyncResponse
    {
        public bool Success { get; set; }
        public List<DesiredActivation>? Desired { get; set; }
    }

    private sealed class DesiredActivation
    {
        public string Name { get; set; } = "";
        public bool H24 { get; set; }
        public List<string>? Windows { get; set; }
        public int? Floor { get; set; }
        public int? Ceiling { get; set; }
        public string? LinePattern { get; set; }
        public string? RaCategory { get; set; }
    }
}
