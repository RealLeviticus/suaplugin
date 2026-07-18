using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;

namespace SuaAirspacePlugin;

internal sealed class SuaExpiryNotification : IDisposable
{
    private const string WindowFormat = "yyyyMMddHHmm";
    private readonly object _lock = new object();
    private readonly Dictionary<string, List<(DateTime From, DateTime To)>> _scheduledWindows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _popupAreas = new(StringComparer.OrdinalIgnoreCase);
    private Form? _popup;
    private Label? _popupDetail;
    private Button? _keepButton;
    private Button? _allowButton;
    private bool _disposed;

    internal Func<string, Task<bool>>? RetainAreaAsync { get; set; }

    internal void Update(string name, IEnumerable<string>? windows)
    {
        var parsed = new List<(DateTime From, DateTime To)>();
        foreach (var value in windows ?? Enumerable.Empty<string>())
        {
            var parts = value.Split('-');
            if (parts.Length == 2 && TryParse(parts[0], out var from) && TryParse(parts[1], out var to))
                parsed.Add((from, to));
        }

        lock (_lock)
        {
            if (parsed.Count == 0) _scheduledWindows.Remove(name);
            else _scheduledWindows[name] = parsed;
        }
    }

    internal void RemoveMissing(IEnumerable<string> names)
    {
        var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
            foreach (var name in _scheduledWindows.Keys.Where(name => !current.Contains(name)).ToList())
                _scheduledWindows.Remove(name);
    }

    internal void Check(RestrictedAreas instance)
    {
        if (_disposed || !Network.IsConnected || Convert.ToInt32(Network.Facility, CultureInfo.InvariantCulture) <= 0)
            return;

        var callsign = (Network.Callsign ?? "").Trim();
        var sector = SectorsVolumes.Sectors.FirstOrDefault(item =>
            string.Equals(item.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        if (sector is null) return;

        var now = DateTime.UtcNow;
        List<(string Name, DateTime To)> due;
        lock (_lock)
        {
            due = _scheduledWindows.SelectMany(pair => pair.Value
                    .Where(window => now >= window.From && window.To > now && window.To <= now.AddMinutes(10))
                    .Select(window => (pair.Key, window.To)))
                .Where(item => !_handled.Contains(NotificationKey(item.Key, item.To)))
                .ToList();
        }

        var eligible = new List<(string Name, DateTime To)>();
        foreach (var item in due)
        {
            var area = instance.Areas.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (area is null || !IntersectsSector(area, sector)) continue;

            lock (_lock)
            {
                var key = NotificationKey(item.Name, item.To);
                if (!_handled.Add(key)) continue;
            }
            eligible.Add(item);
        }

        if (eligible.Count > 0) QueuePopup(eligible);

        lock (_lock)
            _handled.RemoveWhere(key => DateTime.TryParseExact(key.Substring(key.LastIndexOf('|') + 1),
                WindowFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var end) && end < now.AddHours(-1));
    }

    private static bool IntersectsSector(RestrictedAreas.RestrictedArea area, SectorsVolumes.Sector sector)
    {
        var areaPoints = area.Area?.List;
        if (areaPoints is null || areaPoints.Count < 3) return false;

        foreach (var volume in sector.Volumes)
        {
            if (area.AltitudeCeiling < volume.LowerLevel || area.AltitudeFloor > volume.UpperLevel) continue;
            var testLevel = Math.Max(area.AltitudeFloor, volume.LowerLevel);
            if (areaPoints.Any(point => sector.IsInSector(point, testLevel))) return true;
            if (volume.Boundary.Any(point => PointInPolygon(point, areaPoints))) return true;
            if (PolygonsCross(areaPoints, volume.Boundary)) return true;
        }
        return false;
    }

    private static bool PointInPolygon(Coordinate point, IList<Coordinate> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude) &&
                point.Longitude < (b.Longitude - a.Longitude) * (point.Latitude - a.Latitude) /
                (b.Latitude - a.Latitude) + a.Longitude) inside = !inside;
        }
        return inside;
    }

    private static bool PolygonsCross(IList<Coordinate> first, IList<Coordinate> second)
    {
        for (var i = 0; i < first.Count; i++)
            for (var j = 0; j < second.Count; j++)
                if (SegmentsCross(first[i], first[(i + 1) % first.Count], second[j], second[(j + 1) % second.Count]))
                    return true;
        return false;
    }

    private static bool SegmentsCross(Coordinate a, Coordinate b, Coordinate c, Coordinate d)
    {
        static double Turn(Coordinate p, Coordinate q, Coordinate r) =>
            (q.Longitude - p.Longitude) * (r.Latitude - p.Latitude) -
            (q.Latitude - p.Latitude) * (r.Longitude - p.Longitude);
        var abC = Turn(a, b, c); var abD = Turn(a, b, d);
        var cdA = Turn(c, d, a); var cdB = Turn(c, d, b);
        return ((abC > 0 && abD < 0) || (abC < 0 && abD > 0)) &&
               ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0));
    }

    private void QueuePopup(IReadOnlyCollection<(string Name, DateTime To)> areas)
    {
        var host = Application.OpenForms.Cast<Form>().FirstOrDefault(form => !form.IsDisposed && form.IsHandleCreated);
        if (host is null) return;
        Action show = () => ShowPopup(areas);
        if (host.InvokeRequired) host.BeginInvoke(show); else show();
    }

    private void ShowPopup(IEnumerable<(string Name, DateTime To)> areas)
    {
        if (_disposed) return;
        foreach (var area in areas) _popupAreas[area.Name] = area.To;
        if (_popup is not null && !_popup.IsDisposed)
        {
            UpdatePopupText();
            return;
        }

        var terminal = new Font("Terminus (TTF)", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        _popup = new BaseForm
        {
            Resizeable = false, FormBorderStyle = FormBorderStyle.FixedToolWindow,
            ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual,
            ClientSize = new Size(520, 150), MinimumSize = new Size(300, 140),
            Text = "SUA DEACTIVATION"
        };
        _popupDetail = new TextLabel
        {
            HasBorder = false, InteractiveText = false, ForeColor = SystemColors.ControlDark,
            Font = new Font("Terminus (TTF)", 16, FontStyle.Bold, GraphicsUnit.Pixel),
            TextAlign = ContentAlignment.MiddleCenter, AutoSize = true,
            MaximumSize = new Size(496, 0), Location = new Point(12, 12)
        };
        _keepButton = CreateVatSysButton("KEEP ACTIVE", new Point(12, 108), terminal);
        _allowButton = CreateVatSysButton("ALLOW DEACTIVATION", new Point(266, 108), terminal);
        _keepButton.Click += async (_, _) =>
        {
            if (_keepButton is null || _allowButton is null || _popup is null) return;
            _keepButton.Enabled = _allowButton.Enabled = false;
            _keepButton.Text = "SAVING...";
            var names = _popupAreas.Keys.ToList();
            var results = RetainAreaAsync is null
                ? Enumerable.Repeat(false, names.Count).ToArray()
                : await Task.WhenAll(names.Select(name => RetainAreaAsync(name)));
            if (results.All(success => success)) _popup.Close();
            else
            {
                _keepButton.Text = "RETRY KEEP ACTIVE";
                _keepButton.Enabled = _allowButton.Enabled = true;
            }
        };
        _allowButton.Click += (_, _) => _popup?.Close();
        _popup.FormClosed += (_, _) =>
        {
            _popupAreas.Clear();
            _popupDetail = null; _keepButton = null; _allowButton = null;
            _popup?.Dispose(); _popup = null;
        };
        _popup.Controls.AddRange(new Control[] { _popupDetail, _keepButton, _allowButton });
        UpdatePopupText();
        var work = Screen.FromControl(_popup).WorkingArea;
        // Clear the Windows title bar and vatSys menu strip while remaining in
        // the controller's natural top-left notification area.
        _popup.Location = new Point(work.Left + 12, work.Top + 58);
        _popup.Show();
    }

    private static Button CreateVatSysButton(string text, Point location, Font font)
    {
        return new GenericButton
        {
            Text = text, Location = location, Size = new Size(242, 30), Font = font,
            SubFont = new Font("Microsoft Sans Serif", 8.25f), SubText = "",
            UseVisualStyleBackColor = true
        };
    }

    private void UpdatePopupText()
    {
        if (_popupDetail is null) return;
        var scheduleLines = _popupAreas
            .GroupBy(area => area.Value)
            .OrderBy(group => group.Key)
            .Select(group => "THE FOLLOWING SUA: " +
                string.Join(", ", group.Select(area => area.Key).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) +
                " IS SCHEDULED TO DEACTIVATE AT " + group.Key.ToString("HHmm", CultureInfo.InvariantCulture) + "Z.");
        _popupDetail.Text = string.Join("\r\n\r\n", scheduleLines);
        if (_popup is null || _keepButton is null || _allowButton is null) return;
        var buttonY = _popupDetail.Bottom + 12;
        _keepButton.Location = new Point(10, buttonY);
        _allowButton.Location = new Point(266, buttonY);
        _popup.ClientSize = new Size(520, buttonY + _keepButton.Height + 12);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_popup is not null && !_popup.IsDisposed) _popup.Close();
        _popupAreas.Clear();
    }

    private static string NotificationKey(string name, DateTime endUtc) => name + "|" + endUtc.ToString(WindowFormat, CultureInfo.InvariantCulture);
    private static bool TryParse(string value, out DateTime utc) => DateTime.TryParseExact(value, WindowFormat,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
}
