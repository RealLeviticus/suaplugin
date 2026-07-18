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
    private readonly Dictionary<string, List<(DateTime From, DateTime To)>> _notamWindows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Form> _openPopups = new();
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
            if (parsed.Count == 0) _notamWindows.Remove(name);
            else _notamWindows[name] = parsed;
        }
    }

    internal void RemoveMissing(IEnumerable<string> names)
    {
        var current = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
            foreach (var name in _notamWindows.Keys.Where(name => !current.Contains(name)).ToList())
                _notamWindows.Remove(name);
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
            due = _notamWindows.SelectMany(pair => pair.Value
                    .Where(window => now >= window.From && window.To > now && window.To <= now.AddMinutes(10))
                    .Select(window => (pair.Key, window.To)))
                .Where(item => !_handled.Contains(NotificationKey(item.Key, item.To)))
                .ToList();
        }

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
            QueuePopup(item.Name, item.To);
        }

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

    private void QueuePopup(string name, DateTime endUtc)
    {
        var host = Application.OpenForms.Cast<Form>().FirstOrDefault(form => !form.IsDisposed && form.IsHandleCreated);
        if (host is null) return;
        Action show = () => ShowPopup(name, endUtc);
        if (host.InvokeRequired) host.BeginInvoke(show); else show();
    }

    private void ShowPopup(string name, DateTime endUtc)
    {
        if (_disposed) return;
        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, ClientSize = new Size(330, 142),
            BackColor = Color.FromArgb(143, 156, 156), Text = "SUA DEACTIVATION"
        };
        var title = new Label { Text = "SUA DEACTIVATION DUE", ForeColor = Color.Navy, Font = new Font("Arial", 10, FontStyle.Bold), AutoSize = true, Location = new Point(12, 10) };
        var detail = new Label { Text = $"{name} is scheduled to deactivate at {endUtc:HHmm}Z as per NOTAM.\r\nKeep this airspace active?", AutoSize = false, Size = new Size(306, 52), Location = new Point(12, 36) };
        var keep = new Button { Text = "KEEP ACTIVE", Size = new Size(145, 30), Location = new Point(12, 98) };
        var allow = new Button { Text = "NO - DEACTIVATE", Size = new Size(145, 30), Location = new Point(173, 98) };
        keep.Click += async (_, _) =>
        {
            keep.Enabled = allow.Enabled = false;
            keep.Text = "SAVING...";
            var success = RetainAreaAsync is not null && await RetainAreaAsync(name);
            if (success) popup.Close();
            else { keep.Text = "RETRY KEEP ACTIVE"; keep.Enabled = allow.Enabled = true; }
        };
        allow.Click += (_, _) => popup.Close();
        popup.FormClosed += (_, _) => { _openPopups.Remove(popup); popup.Dispose(); RepositionPopups(); };
        popup.Controls.AddRange(new Control[] { title, detail, keep, allow });
        _openPopups.Add(popup); RepositionPopups(); popup.Show();
    }

    private void RepositionPopups()
    {
        for (var i = 0; i < _openPopups.Count; i++)
        {
            var work = Screen.FromControl(_openPopups[i]).WorkingArea;
            _openPopups[i].Location = new Point(work.Right - _openPopups[i].Width - 12,
                work.Top + 12 + i * (_openPopups[i].Height + 8));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var popup in _openPopups.ToList()) if (!popup.IsDisposed) popup.Close();
        _openPopups.Clear();
    }

    private static string NotificationKey(string name, DateTime endUtc) => name + "|" + endUtc.ToString(WindowFormat, CultureInfo.InvariantCulture);
    private static bool TryParse(string value, out DateTime utc) => DateTime.TryParseExact(value, WindowFormat,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
}
