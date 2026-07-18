using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using vatsys;

namespace SuaAirspacePlugin;

/// <summary>
/// Lists the dataset's Danger/Restricted areas and manages plugin-side manual
/// activation. Because vatSys Activations only store a time of day, the plugin
/// keeps real date-aware UTC windows per area and injects/removes an H24
/// activation in vatsys.RestrictedAreas as each window opens and closes (the
/// 1 s timer is the scheduler). The display pass preserves vatSys's standard
/// restricted-area colour and only removes plugin-area infills. Each area keeps
/// the line pattern defined in the dataset, which a controller can still change
/// from the vatSys Restricted Area window.
/// </summary>
public sealed class SuaAirspaceService : IDisposable
{
    private const string WindowFormat = "yyyyMMddHHmm";

    private sealed class Window
    {
        public DateTime FromUtc;
        public DateTime ToUtc;
    }

    private sealed class ManualState
    {
        public bool H24;
        public readonly List<Window> Windows = new();
        public RestrictedAreas.RestrictedArea.Activation? Injected;
        public bool? OriginalDaiw;
        public DisplayMaps.Map.Patterns? OriginalLinePattern;
        public DisplayMaps.Map.InfillTypes? OriginalInfillType;
        public DisplayMaps.Map.Patterns? DesiredLinePattern;
        public bool? DesiredDaiw;
        public int? OriginalFloor;
        public int? OriginalCeiling;
        public int? AppliedFloor;
        public int? AppliedCeiling;

        public bool HasSchedule => H24 || Windows.Count > 0;
        public bool HasLevelEdits => OriginalFloor.HasValue || OriginalCeiling.HasValue;
    }

    private sealed class CatalogueArea
    {
        public string Name = "";
        public string Type = "";
        public int Floor;
        public int Ceiling;
        public bool Daiw;
        public bool Hidden;
        public string Schedule = "";
        public RestrictedAreas.RestrictedArea DefaultState = new RestrictedAreas.RestrictedArea();
    }

    private sealed class ControllerActivation
    {
        public string Name { get; set; } = "";
        public bool H24 { get; set; }
        public List<string> Windows { get; set; } = new List<string>();
        public int? Floor { get; set; }
        public int? Ceiling { get; set; }
        public string? LinePattern { get; set; }
    }

    private readonly object _lock = new object();
    private readonly Dictionary<string, ManualState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Timers.Timer _timer;
    private List<CatalogueArea>? _catalogue;
    private Dictionary<string, CatalogueArea>? _catalogueByName;
    private readonly Dictionary<Control, bool> _activationPermissionControls = new();
    private int _nativeRefreshPending;
    private int _nativeRefreshQueued;
    private int _activationPermissionUiQueued;
    private bool _disposed;

    public SuaAirspaceService()
    {
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) => Tick();
        _timer.AutoReset = true;
        _timer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }

    internal bool AreasLoaded
    {
        get
        {
            var instance = RestrictedAreas.Instance;
            return instance is not null && instance.Areas.Any(area => !string.IsNullOrWhiteSpace(area.Name));
        }
    }

    internal bool IsConnected
    {
        get
        {
            try { return Network.IsConnected; }
            catch { return false; }
        }
    }

    internal bool CanActivateRestrictedAreas
    {
        get
        {
            try
            {
                return Network.IsConnected && Convert.ToInt32(Network.Facility, CultureInfo.InvariantCulture) > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    internal bool SyncReady => IsConnected && AreasLoaded;

    public object GetAreas() => CreateSnapshot(null, 0);

    internal object GetCloudSnapshot(string installationId, int userLeaseSeconds) =>
        CreateSnapshot(installationId, userLeaseSeconds);

    private object CreateSnapshot(string? installationId, int userLeaseSeconds)
    {
        var instance = RestrictedAreas.Instance;
        var controllerCid = GetControllerCid();
        var controllerRating = GetControllerRating();
        var controllerFacility = GetControllerFacility();
        if (!IsConnected || instance is null || instance.Areas.Count == 0)
        {
            if (installationId is null)
                return new { Loaded = false, Areas = new List<object>(), UtcTime = UtcNowString() };
            return new
            {
                Loaded = false,
                Areas = new List<object>(),
                UtcTime = UtcNowString(),
                InstallationId = installationId,
                ControllerCid = controllerCid,
                ControllerRating = controllerRating,
                ControllerFacility = controllerFacility,
                UserLeaseSeconds = userLeaseSeconds,
                UserActivations = new List<ControllerActivation>(),
            };
        }

        lock (_lock)
        {
            EnsureCatalogueSnapshot(instance);
            var areas = _catalogue!
                .Select(a =>
                {
                    return new
                    {
                        a.Name,
                        a.Type,
                        a.Floor,
                        a.Ceiling,
                        a.Daiw,
                        a.Schedule,
                        Active = a.DefaultState.IsActive(),
                        PreActive = a.DefaultState.IsPreActive(),
                        a.Hidden,
                        Manual = false,
                        H24Manual = false,
                        Scheduled = false,
                        Windows = new List<string>(),
                        LevelsEdited = false,
                    };
                })
                .ToList();

            if (installationId is null)
                return new { Loaded = true, Areas = areas, UtcTime = UtcNowString() };

            return new
            {
                Loaded = true,
                Areas = areas,
                UtcTime = UtcNowString(),
                InstallationId = installationId,
                ControllerCid = controllerCid,
                ControllerRating = controllerRating,
                ControllerFacility = controllerFacility,
                UserLeaseSeconds = userLeaseSeconds,
                UserActivations = CanActivateRestrictedAreas
                    ? GetControllerActivations(instance)
                    : new List<ControllerActivation>(),
            };
        }
    }

    /// <summary>minutes &lt;= 0: active until deactivated; otherwise a window from now.</summary>
    public object Activate(string name, int minutes)
    {
        if (!CanActivateRestrictedAreas) return ActivationPermissionFailure();
        return ActivateCore(name, minutes);
    }

    private object ActivateCore(string name, int minutes)
    {
        var area = FindArea(name, out var error);
        if (area is null) return Failure(error);

        lock (_lock)
        {
            var state = GetOrCreateState(area.Name);
            state.Windows.Clear();
            if (minutes <= 0)
            {
                state.H24 = true;
            }
            else
            {
                state.H24 = false;
                var now = DateTime.UtcNow;
                state.Windows.Add(new Window { FromUtc = now, ToUtc = now.AddMinutes(Math.Min(minutes, 7 * 24 * 60)) });
            }
        }

        Tick();
        return Success(area);
    }

    internal bool TryActivate(string name, int minutes)
    {
        if (FindArea(name, out _) is null) return false;
        ActivateCore(name, minutes);
        return true;
    }

    internal bool TrySetWindows(string name, string spec)
    {
        if (FindArea(name, out _) is null) return false;
        SetWindowsCore(name, spec);
        return true;
    }

    internal bool TrySetLevels(string name, int? floor, int? ceiling)
    {
        if (FindArea(name, out _) is null) return false;
        SetLevelsCore(name, floor, ceiling);
        return true;
    }

    /// <summary>
    /// Stage the requested category with its activation. Future windows retain
    /// the current local/dataset category until activation starts; active windows
    /// apply the requested pattern and DAIW setting until they end.
    /// </summary>
    internal bool TrySetLinePattern(string name, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (!Enum.TryParse<DisplayMaps.Map.Patterns>(pattern, ignoreCase: true, out var parsed))
            return false;

        var area = FindArea(name, out _);
        if (area is null) return false;
        if (area.LinePattern == parsed) return true;

        area.LinePattern = parsed;
        RequestNativeMapRefresh();
        return true;
    }

    internal bool TryApplyRaCategory(string name, string? category, string? fallbackPattern)
    {
        var area = FindArea(name, out _);
        if (area is null) return false;

        DisplayMaps.Map.Patterns pattern;
        switch ((category ?? "").Trim().ToUpperInvariant())
        {
            case "RA1": pattern = DisplayMaps.Map.Patterns.Dashed; break;
            case "RA2": pattern = DisplayMaps.Map.Patterns.Dotted; break;
            case "RA3": pattern = DisplayMaps.Map.Patterns.Solid; break;
            default:
                if (!Enum.TryParse(fallbackPattern, true, out pattern)) pattern = area.LinePattern;
                break;
        }

        var categoryKnown = pattern == DisplayMaps.Map.Patterns.Dashed ||
            pattern == DisplayMaps.Map.Patterns.Dotted || pattern == DisplayMaps.Map.Patterns.Solid;
        if (!categoryKnown) return true;
        var desiredDaiw = pattern != DisplayMaps.Map.Patterns.Dashed;

        lock (_lock)
        {
            var state = GetOrCreateState(area.Name);
            state.DesiredLinePattern = pattern;
            state.DesiredDaiw = desiredDaiw;
            if (state.Injected is not null) ApplyScheduledCategory(area, state);
        }
        RequestNativeMapRefresh();
        return true;
    }

    internal bool TryActivateWindows(string name, List<(DateTime FromUtc, DateTime ToUtc)> windows)
    {
        var area = FindArea(name, out _);
        if (area is null) return false;

        var now = DateTime.UtcNow;
        var valid = windows
            .Where(w => w.ToUtc > w.FromUtc && w.ToUtc > now)
            .Select(w => new Window { FromUtc = w.FromUtc, ToUtc = w.ToUtc })
            .ToList();

        lock (_lock)
        {
            var state = GetOrCreateState(area.Name);
            if (valid.Count == 0)
            {
                // No usable timing in the NOTAM — fall back to H24.
                state.H24 = true;
                state.Windows.Clear();
            }
            else
            {
                state.H24 = false;
                state.Windows.Clear();
                state.Windows.AddRange(valid);
            }
        }

        Tick();
        return true;
    }

    /// <summary>Replace an area's activation windows. Spec: "yyyyMMddHHmm-yyyyMMddHHmm,..."; empty clears scheduling.</summary>
    public object SetWindows(string name, string spec)
    {
        if (!CanActivateRestrictedAreas) return ActivationPermissionFailure();
        return SetWindowsCore(name, spec);
    }

    private object SetWindowsCore(string name, string spec)
    {
        var area = FindArea(name, out var error);
        if (area is null) return Failure(error);

        var windows = new List<Window>();
        foreach (var part in (spec ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Trim().Split('-');
            if (ends.Length != 2 ||
                !TryParseWindowTime(ends[0], out var from) ||
                !TryParseWindowTime(ends[1], out var to))
                return Failure("Invalid window: " + part + " (expected yyyyMMddHHmm-yyyyMMddHHmm)");
            if (to <= from) return Failure("Window ends before it starts: " + part);
            windows.Add(new Window { FromUtc = from, ToUtc = to });
        }

        lock (_lock)
        {
            var state = GetOrCreateState(area.Name);
            state.H24 = false;
            state.Windows.Clear();
            state.Windows.AddRange(windows.OrderBy(w => w.FromUtc));
        }

        Tick();
        return Success(area);
    }

    /// <summary>Edit an area's vertical limits (feet). Originals restore on deactivation.</summary>
    public object SetLevels(string name, int? floor, int? ceiling)
    {
        if (!CanActivateRestrictedAreas) return ActivationPermissionFailure();
        return SetLevelsCore(name, floor, ceiling);
    }

    private object SetLevelsCore(string name, int? floor, int? ceiling)
    {
        var area = FindArea(name, out var error);
        if (area is null) return Failure(error);
        if (floor is null && ceiling is null) return Failure("floor or ceiling required.");
        if (floor is not null && ceiling is not null && ceiling < floor)
            return Failure("Ceiling is below floor.");

        lock (_lock)
        {
            var state = GetOrCreateState(area.Name);
            if (floor is not null)
            {
                if (state.AppliedFloor.HasValue && area.AltitudeFloor != state.AppliedFloor.Value)
                    state.OriginalFloor = area.AltitudeFloor;
                else
                    state.OriginalFloor ??= area.AltitudeFloor;
                area.AltitudeFloor = floor.Value;
                state.AppliedFloor = floor.Value;
            }
            if (ceiling is not null)
            {
                if (state.AppliedCeiling.HasValue && area.AltitudeCeiling != state.AppliedCeiling.Value)
                    state.OriginalCeiling = area.AltitudeCeiling;
                else
                    state.OriginalCeiling ??= area.AltitudeCeiling;
                area.AltitudeCeiling = ceiling.Value;
                state.AppliedCeiling = ceiling.Value;
            }
        }

        Tick();
        return Success(area);
    }

    public object Deactivate(string name)
    {
        var area = FindArea(name, out var error);
        if (area is null) return Failure(error);

        var displayChanged = false;
        lock (_lock)
        {
            if (_states.TryGetValue(area.Name, out var state))
            {
                displayChanged = state.Injected is not null || state.OriginalInfillType.HasValue || state.HasLevelEdits;
                ReleaseState(area, state);
                if (CanDiscardState(state)) _states.Remove(area.Name);
            }
        }

        Tick();
        if (displayChanged) RequestNativeMapRefresh();
        return Success(area);
    }

    public object DeactivateAll()
    {
        var instance = RestrictedAreas.Instance;
        if (instance is null) return Failure("Restricted areas are not loaded yet.");

        var displayChanged = false;
        lock (_lock)
        {
            foreach (var area in instance.Areas)
            {
                if (string.IsNullOrWhiteSpace(area.Name)) continue;
                if (!_states.TryGetValue(area.Name, out var state)) continue;
                displayChanged |= state.Injected is not null || state.OriginalInfillType.HasValue || state.HasLevelEdits;
                ReleaseState(area, state);
                if (CanDiscardState(state)) _states.Remove(area.Name);
            }
        }

        Tick();
        if (displayChanged) RequestNativeMapRefresh();
        return new { Success = true };
    }

    /// <summary>
    /// Scheduler pass: open/close vatSys activations as windows come and go,
    /// then ask vatSys to rebuild its native SUA maps when state changes. Runs
    /// on every change and once a second.
    /// </summary>
    private void Tick()
    {
        try
        {
            var instance = RestrictedAreas.Instance;
            if (instance is null) return;

            var changed = false;
            lock (_lock)
            {
                EnsureCatalogueSnapshot(instance);
                if (!IsConnected)
                {
                    changed = ResetToDataset(instance);
                }
                else
                {
                    var now = DateTime.UtcNow;

                    // vatSys's minute refresh can drop dataset-default
                    // activations from its native snapshot; re-add any that went
                    // missing. Line patterns are deliberately left untouched so
                    // each area keeps its dataset default and whatever draw style
                    // a controller sets from the Restricted Area window.
                    foreach (var area in instance.Areas)
                    {
                        _states.TryGetValue(area.Name, out var state);
                        if (_catalogueByName!.TryGetValue(area.Name, out var catalogueArea) &&
                            RestoreMissingDefaults(area, catalogueArea, state?.Injected))
                            changed = true;
                    }

                    foreach (var entry in _states.ToList())
                    {
                        var area = instance.Areas.FirstOrDefault(a =>
                            string.Equals(a.Name, entry.Key, StringComparison.OrdinalIgnoreCase));
                        if (area is null) { _states.Remove(entry.Key); continue; }

                        var state = entry.Value;
                        if (state.Injected is not null &&
                            !area.Activations.Any(activation => ReferenceEquals(activation, state.Injected)))
                            state.Injected = null;

                        var shouldBeActive = state.H24 ||
                            state.Windows.Any(w => now >= w.FromUtc && now < w.ToUtc);

                        if (shouldBeActive && state.Injected is null) { Inject(area, state); changed = true; }
                        else if (!shouldBeActive && state.Injected is not null) { RemoveInjection(area, state); changed = true; }

                        state.Windows.RemoveAll(w => w.ToUtc <= now);
                        if (CanDiscardState(state))
                            _states.Remove(entry.Key);
                    }

                    // OBS connections can consume shared/default SUA, but any
                    // changes made through their local Restricted Area window
                    // are removed before they can be published or retained.
                    if (!CanActivateRestrictedAreas && RemoveControllerChanges(instance))
                        changed = true;
                }
            }

            if (changed) Interlocked.Exchange(ref _nativeRefreshPending, 1);
            TryQueueNativeMapRefresh();
            TryQueueActivationPermissionUi();
        }
        catch
        {
            // Never let the scheduler take vatSys down.
        }
    }

    internal void HandleNetworkDisconnected()
    {
        var instance = RestrictedAreas.Instance;
        if (instance is null) return;

        var changed = false;
        lock (_lock)
        {
            EnsureCatalogueSnapshot(instance);
            changed = ResetToDataset(instance);
        }

        if (changed) RequestNativeMapRefresh();
    }

    /// <summary>
    /// vatSys creates and registers the native dynamic map resources on its UI
    /// thread. Queue the rebuild there so the render thread never sees a new
    /// map before vatSys has prepared its brushes and other per-map resources.
    /// </summary>
    private void TryQueueNativeMapRefresh()
    {
        if (_disposed || Volatile.Read(ref _nativeRefreshPending) == 0) return;
        if (Interlocked.CompareExchange(ref _nativeRefreshQueued, 1, 0) != 0) return;

        Form? host = null;
        try
        {
            host = Application.OpenForms.Cast<Form>()
                .FirstOrDefault(form => !form.IsDisposed && form.IsHandleCreated);
            if (host is null)
            {
                Interlocked.Exchange(ref _nativeRefreshQueued, 0);
                return;
            }

            Action refresh = () =>
            {
                try
                {
                    Interlocked.Exchange(ref _nativeRefreshPending, 0);
                    if (!_disposed)
                    {
                        DisplayMaps.UpdateDynamicRMaps();
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _nativeRefreshQueued, 0);
                    if (!_disposed && Volatile.Read(ref _nativeRefreshPending) != 0)
                        TryQueueNativeMapRefresh();
                }
            };

            if (host.InvokeRequired) host.BeginInvoke(refresh);
            else refresh();
        }
        catch
        {
            Interlocked.Exchange(ref _nativeRefreshQueued, 0);
        }
    }

    private void RequestNativeMapRefresh()
    {
        Interlocked.Exchange(ref _nativeRefreshPending, 1);
        TryQueueNativeMapRefresh();
    }

    private void TryQueueActivationPermissionUi()
    {
        if (_disposed || Interlocked.CompareExchange(ref _activationPermissionUiQueued, 1, 0) != 0) return;

        Form? host = null;
        try
        {
            host = Application.OpenForms.Cast<Form>()
                .FirstOrDefault(form => !form.IsDisposed && form.IsHandleCreated);
            if (host is null)
            {
                Interlocked.Exchange(ref _activationPermissionUiQueued, 0);
                return;
            }

            Action update = () =>
            {
                try
                {
                    var canActivate = CanActivateRestrictedAreas;
                    foreach (var form in Application.OpenForms.Cast<Form>().ToList())
                    {
                        if (!string.Equals(form.GetType().FullName, "vatsys.RestrictedAreaWindow", StringComparison.Ordinal))
                            continue;
                        var saveButton = FindControl(form, "saveButton");
                        if (saveButton is null) continue;

                        if (!canActivate)
                        {
                            if (!_activationPermissionControls.ContainsKey(saveButton))
                                _activationPermissionControls[saveButton] = saveButton.Enabled;
                            saveButton.Enabled = false;
                        }
                        else if (_activationPermissionControls.TryGetValue(saveButton, out var wasEnabled))
                        {
                            saveButton.Enabled = wasEnabled;
                            _activationPermissionControls.Remove(saveButton);
                        }
                    }

                    foreach (var control in _activationPermissionControls.Keys
                        .Where(control => control.IsDisposed).ToList())
                        _activationPermissionControls.Remove(control);
                }
                catch { }
                finally { Interlocked.Exchange(ref _activationPermissionUiQueued, 0); }
            };

            if (host.InvokeRequired) host.BeginInvoke(update);
            else update();
        }
        catch
        {
            Interlocked.Exchange(ref _activationPermissionUiQueued, 0);
        }
    }

    private static Control? FindControl(Control root, string name)
    {
        if (string.Equals(root.Name, name, StringComparison.Ordinal)) return root;
        foreach (Control child in root.Controls)
        {
            var found = FindControl(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    private void Inject(RestrictedAreas.RestrictedArea area, ManualState state)
    {
        ApplyScheduledCategory(area, state);
        if (area.InfillType != DisplayMaps.Map.InfillTypes.None)
        {
            state.OriginalInfillType ??= area.InfillType;
            area.InfillType = DisplayMaps.Map.InfillTypes.None;
        }

        var activation = new RestrictedAreas.RestrictedArea.Activation { H24 = true };
        SwapActivations(area, current => current.Add(activation));
        state.Injected = activation;

    }

    private static void ApplyScheduledCategory(RestrictedAreas.RestrictedArea area, ManualState state)
    {
        if (state.DesiredLinePattern.HasValue && area.LinePattern != state.DesiredLinePattern.Value)
        {
            state.OriginalLinePattern ??= area.LinePattern;
            area.LinePattern = state.DesiredLinePattern.Value;
        }
        if (state.DesiredDaiw.HasValue && area.DAIWEnabled != state.DesiredDaiw.Value)
        {
            state.OriginalDaiw ??= area.DAIWEnabled;
            area.DAIWEnabled = state.DesiredDaiw.Value;
        }
    }

    private void RemoveInjection(RestrictedAreas.RestrictedArea area, ManualState state)
    {
        if (state.Injected is not null)
        {
            var injected = state.Injected;
            SwapActivations(area, current => current.RemoveAll(a => ReferenceEquals(a, injected)));
            state.Injected = null;
        }

        if (state.OriginalDaiw.HasValue)
        {
            area.DAIWEnabled = state.OriginalDaiw.Value;
            state.OriginalDaiw = null;
        }

        if (state.OriginalLinePattern.HasValue)
        {
            area.LinePattern = state.OriginalLinePattern.Value;
            state.OriginalLinePattern = null;
        }

        if (state.OriginalInfillType.HasValue)
        {
            area.InfillType = state.OriginalInfillType.Value;
            state.OriginalInfillType = null;
        }
    }

    private void ReleaseState(RestrictedAreas.RestrictedArea area, ManualState state)
    {
        RemoveInjection(area, state);
        if (state.OriginalFloor.HasValue &&
            (!state.AppliedFloor.HasValue || area.AltitudeFloor == state.AppliedFloor.Value))
            area.AltitudeFloor = state.OriginalFloor.Value;
        if (state.OriginalCeiling.HasValue &&
            (!state.AppliedCeiling.HasValue || area.AltitudeCeiling == state.AppliedCeiling.Value))
            area.AltitudeCeiling = state.OriginalCeiling.Value;
        state.OriginalFloor = null;
        state.OriginalCeiling = null;
        state.AppliedFloor = null;
        state.AppliedCeiling = null;
        state.H24 = false;
        state.Windows.Clear();
    }

    private ManualState GetOrCreateState(string areaName)
    {
        if (!_states.TryGetValue(areaName, out var state))
            _states[areaName] = state = new ManualState();
        return state;
    }

    private static bool CanDiscardState(ManualState state) =>
        !state.HasSchedule && state.Injected is null && !state.HasLevelEdits &&
        !state.OriginalDaiw.HasValue && !state.OriginalLinePattern.HasValue && !state.OriginalInfillType.HasValue;

    private bool ResetToDataset(RestrictedAreas instance)
    {
        var changed = _states.Count > 0;
        foreach (var area in instance.Areas)
        {
            if (!_catalogueByName!.TryGetValue(area.Name, out var catalogueArea)) continue;
            var defaults = catalogueArea.DefaultState;
            var currentKeys = area.Activations.Select(ActivationKey).OrderBy(value => value).ToList();
            var defaultKeys = defaults.Activations.Select(ActivationKey).OrderBy(value => value).ToList();
            if (!currentKeys.SequenceEqual(defaultKeys))
            {
                area.Activations = defaults.Activations.Select(CloneActivation).ToList();
                changed = true;
            }

            if (area.AltitudeFloor != catalogueArea.Floor) { area.AltitudeFloor = catalogueArea.Floor; changed = true; }
            if (area.AltitudeCeiling != catalogueArea.Ceiling) { area.AltitudeCeiling = catalogueArea.Ceiling; changed = true; }
            if (area.DAIWEnabled != defaults.DAIWEnabled) { area.DAIWEnabled = defaults.DAIWEnabled; changed = true; }
            // Line pattern is a user/dataset display choice the plugin no longer
            // manages, so it is not reset here.
            if (area.InfillType != defaults.InfillType) { area.InfillType = defaults.InfillType; changed = true; }
            if (area.InfillPattern != defaults.InfillPattern) { area.InfillPattern = defaults.InfillPattern; changed = true; }
        }

        _states.Clear();
        return changed;
    }

    private bool RemoveControllerChanges(RestrictedAreas instance)
    {
        var changed = false;
        foreach (var area in instance.Areas)
        {
            if (!_catalogueByName!.TryGetValue(area.Name, out var catalogueArea)) continue;
            _states.TryGetValue(area.Name, out var state);

            var currentKeys = area.Activations
                .Where(activation => state?.Injected is null || !ReferenceEquals(activation, state.Injected))
                .Select(ActivationKey)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var defaultKeys = catalogueArea.DefaultState.Activations
                .Select(ActivationKey)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            if (!currentKeys.SequenceEqual(defaultKeys))
            {
                var allowed = catalogueArea.DefaultState.Activations.Select(CloneActivation).ToList();
                if (state?.Injected is not null) allowed.Add(state.Injected);
                area.Activations = allowed;
                changed = true;
            }

            var allowedFloor = state?.AppliedFloor ?? catalogueArea.Floor;
            var allowedCeiling = state?.AppliedCeiling ?? catalogueArea.Ceiling;
            if (area.AltitudeFloor != allowedFloor) { area.AltitudeFloor = allowedFloor; changed = true; }
            if (area.AltitudeCeiling != allowedCeiling) { area.AltitudeCeiling = allowedCeiling; changed = true; }
        }

        return changed;
    }

    private void EnsureCatalogueSnapshot(RestrictedAreas instance)
    {
        if (_catalogue is not null) return;

        _catalogue = instance.Areas
            .Where(area => !string.IsNullOrWhiteSpace(area.Name))
            .OrderBy(area => area.Type)
            .ThenBy(area => area.Name, StringComparer.OrdinalIgnoreCase)
            .Select(area =>
            {
                if (area.LinePattern == DisplayMaps.Map.Patterns.Dashed) area.DAIWEnabled = false;
                else if (area.LinePattern == DisplayMaps.Map.Patterns.Dotted || area.LinePattern == DisplayMaps.Map.Patterns.Solid)
                    area.DAIWEnabled = true;
                var defaultState = new RestrictedAreas.RestrictedArea(
                    area.Name, area.Type, area.AltitudeFloor, area.AltitudeCeiling)
                {
                    DAIWEnabled = area.DAIWEnabled,
                    LinePattern = area.LinePattern,
                    InfillType = area.InfillType,
                    InfillPattern = area.InfillPattern,
                    Activations = area.Activations.Select(CloneActivation).ToList(),
                };

                return new CatalogueArea
                {
                    Name = area.Name,
                    Type = area.Type.ToString(),
                    Floor = area.AltitudeFloor,
                    Ceiling = area.AltitudeCeiling,
                    Daiw = area.DAIWEnabled,
                    Hidden = area.LinePattern == DisplayMaps.Map.Patterns.None,
                    Schedule = DescribeSchedule(defaultState, null),
                    DefaultState = defaultState,
                };
            })
            .ToList();
        _catalogueByName = _catalogue.ToDictionary(area => area.Name, StringComparer.OrdinalIgnoreCase);
    }

    private List<ControllerActivation> GetControllerActivations(RestrictedAreas instance)
    {
        var result = new List<ControllerActivation>();
        var now = DateTime.UtcNow;

        foreach (var area in instance.Areas)
        {
            if (!_catalogueByName!.TryGetValue(area.Name, out var catalogueArea)) continue;
            _states.TryGetValue(area.Name, out var state);

            var defaultCounts = catalogueArea.DefaultState.Activations
                .GroupBy(ActivationKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var userActivations = new List<RestrictedAreas.RestrictedArea.Activation>();
            foreach (var activation in area.Activations)
            {
                if (state?.Injected is not null && ReferenceEquals(activation, state.Injected)) continue;
                var key = ActivationKey(activation);
                if (defaultCounts.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    defaultCounts[key] = remaining - 1;
                    continue;
                }
                userActivations.Add(activation);
            }

            var localFloor = state?.AppliedFloor.HasValue == true &&
                area.AltitudeFloor == state.AppliedFloor.Value && state.OriginalFloor.HasValue
                    ? state.OriginalFloor.Value
                    : area.AltitudeFloor;
            var localCeiling = state?.AppliedCeiling.HasValue == true &&
                area.AltitudeCeiling == state.AppliedCeiling.Value && state.OriginalCeiling.HasValue
                    ? state.OriginalCeiling.Value
                    : area.AltitudeCeiling;
            var floor = localFloor == catalogueArea.Floor ? (int?)null : localFloor;
            var ceiling = localCeiling == catalogueArea.Ceiling ? (int?)null : localCeiling;
            var h24 = userActivations.Any(activation => activation.H24);
            var windows = h24
                ? new List<string>()
                : userActivations
                    .Where(activation => !activation.H24)
                    .SelectMany(activation => ExpandDailyActivation(activation, now))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();

            if (!h24 && windows.Count == 0 && !floor.HasValue && !ceiling.HasValue) continue;

            // Only a genuine activation carries the controller's chosen draw
            // style to other clients; a level-only edit leaves borders alone.
            var linePattern = h24 || windows.Count > 0 ? area.LinePattern.ToString() : null;
            if (linePattern == DisplayMaps.Map.Patterns.Dashed.ToString()) area.DAIWEnabled = false;
            else if (linePattern == DisplayMaps.Map.Patterns.Dotted.ToString() || linePattern == DisplayMaps.Map.Patterns.Solid.ToString())
                area.DAIWEnabled = true;
            result.Add(new ControllerActivation
            {
                Name = area.Name,
                H24 = h24,
                Windows = windows,
                Floor = floor,
                Ceiling = ceiling,
                LinePattern = linePattern,
            });
        }

        return result;
    }

    private static IEnumerable<string> ExpandDailyActivation(
        RestrictedAreas.RestrictedArea.Activation activation,
        DateTime now)
    {
        var startTime = activation.Start.TimeOfDay;
        var endTime = activation.End.TimeOfDay;
        for (var dayOffset = -1; dayOffset <= 2; dayOffset++)
        {
            var from = now.Date.AddDays(dayOffset).Add(startTime);
            var to = now.Date.AddDays(dayOffset).Add(endTime);
            if (to <= from) to = to.AddDays(1);
            if (to <= now || from > now.AddDays(2)) continue;
            yield return from.ToString(WindowFormat, CultureInfo.InvariantCulture) + "-" +
                to.ToString(WindowFormat, CultureInfo.InvariantCulture);
        }
    }

    private static bool RestoreMissingDefaults(
        RestrictedAreas.RestrictedArea area,
        CatalogueArea catalogueArea,
        RestrictedAreas.RestrictedArea.Activation? injected)
    {
        var currentCounts = area.Activations
            .Where(activation => injected is null || !ReferenceEquals(activation, injected))
            .GroupBy(ActivationKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var missing = new List<RestrictedAreas.RestrictedArea.Activation>();

        foreach (var defaultActivation in catalogueArea.DefaultState.Activations)
        {
            var key = ActivationKey(defaultActivation);
            if (currentCounts.TryGetValue(key, out var remaining) && remaining > 0)
            {
                currentCounts[key] = remaining - 1;
                continue;
            }
            missing.Add(CloneActivation(defaultActivation));
        }

        if (missing.Count == 0) return false;
        SwapActivations(area, current => current.AddRange(missing));
        return true;
    }

    private static string ActivationKey(RestrictedAreas.RestrictedArea.Activation activation) =>
        activation.H24
            ? "H24"
            : activation.Start.ToString("HHmm", CultureInfo.InvariantCulture) + "-" +
              activation.End.ToString("HHmm", CultureInfo.InvariantCulture);

    private static string GetControllerCid()
    {
        try
        {
            var cid = (Network.ControllerId ?? "").Trim();
            return cid.Length >= 4 && cid.Length <= 12 && cid.All(char.IsDigit) ? cid : "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetControllerRating()
    {
        try { return Network.Rating.ToString(); }
        catch { return ""; }
    }

    private static string GetControllerFacility()
    {
        try { return Network.Facility.ToString(); }
        catch { return ""; }
    }

    private static RestrictedAreas.RestrictedArea.Activation CloneActivation(
        RestrictedAreas.RestrictedArea.Activation activation)
    {
        if (activation.H24)
            return new RestrictedAreas.RestrictedArea.Activation { H24 = true };

        if (!string.IsNullOrWhiteSpace(activation.RawStart) &&
            !string.IsNullOrWhiteSpace(activation.RawEnd))
            return new RestrictedAreas.RestrictedArea.Activation(activation.RawStart, activation.RawEnd);

        return new RestrictedAreas.RestrictedArea.Activation
        {
            Start = activation.Start,
            End = activation.End,
        };
    }

    private static void SwapActivations(
        RestrictedAreas.RestrictedArea area,
        Action<List<RestrictedAreas.RestrictedArea.Activation>> mutate)
    {
        // vatSys's minute timer enumerates Activations on another thread, so
        // mutate a copy and swap the list reference atomically.
        var updated = new List<RestrictedAreas.RestrictedArea.Activation>(area.Activations);
        mutate(updated);
        area.Activations = updated;
    }

    private RestrictedAreas.RestrictedArea? FindArea(string? name, out string error)
    {
        error = "";
        var instance = RestrictedAreas.Instance;
        if (instance is null)
        {
            error = "Restricted areas are not loaded yet.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Area name required.";
            return null;
        }

        var target = name!.Trim();
        var area = instance.Areas.FirstOrDefault(a =>
            string.Equals(a.Name, target, StringComparison.OrdinalIgnoreCase));
        if (area is null) error = "Unknown area: " + target;
        return area;
    }

    private static string DescribeSchedule(RestrictedAreas.RestrictedArea area, ManualState? state)
    {
        var parts = new List<string>();
        foreach (var act in area.Activations)
        {
            if (state is not null && ReferenceEquals(act, state.Injected)) continue;
            parts.Add(act.H24 ? "H24" : act.RawStart + "-" + act.RawEnd);
        }

        return parts.Count == 0 ? "" : string.Join(", ", parts.Distinct());
    }

    private static bool TryParseWindowTime(string value, out DateTime utc) =>
        DateTime.TryParseExact(value.Trim(), WindowFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private static string FormatWindow(Window w) =>
        w.FromUtc.ToString(WindowFormat, CultureInfo.InvariantCulture) + "-" +
        w.ToUtc.ToString(WindowFormat, CultureInfo.InvariantCulture);

    private object Success(RestrictedAreas.RestrictedArea area)
    {
        lock (_lock)
        {
            _states.TryGetValue(area.Name, out var state);
            return new
            {
                Success = true,
                area.Name,
                Active = area.IsActive(),
                Manual = state?.Injected is not null,
            };
        }
    }

    private static object Failure(string error) => new { Success = false, Error = error };

    private static object ActivationPermissionFailure() =>
        Failure("Connect to VATSIM in a controller position (not OBS) to activate Restricted Areas.");

    private static string UtcNowString() =>
        DateTime.UtcNow.ToString("HHmm'Z'", CultureInfo.InvariantCulture);
}
