using System.Net.NetworkInformation;

namespace v2rayN.Desktop.Services;

/// <summary>One second of traffic: bytes per second up and down.</summary>
public readonly record struct ColituSpeedSample(double Up, double Down);

/// <summary>
/// Live up/down speed for the connection panel, read from the kernel's interface counters (/sys/class/net) once a
/// second. In TUN mode that is the core's TUN interface (singbox_tun / xray_tun) (only tunnelled traffic); in proxy mode the
/// physical adapter (the whole PC). Keeps the last <see cref="Capacity"/> seconds.
/// </summary>
public sealed class ColituTrafficMeter
{
    public const int Capacity = 60;

    private readonly Queue<ColituSpeedSample> _samples = new();
    private string? _adapterId;
    private long _lastSent = -1;
    private long _lastReceived = -1;
    private DateTime _lastAt;

    public IReadOnlyCollection<ColituSpeedSample> Samples => _samples;
    public ColituSpeedSample Latest { get; private set; }

    /// <summary>Whether the numbers are the tunnel's own (TUN adapter) or the whole PC's.</summary>
    public bool TunnelOnly { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        _adapterId = null;
        _lastSent = _lastReceived = -1;
        Latest = default;
    }

    /// <summary>Takes one reading; the first one after a reset only sets the baseline.</summary>
    public void Tick()
    {
        try
        {
            var adapter = PickAdapter();
            if (adapter == null)
            {
                return;
            }
            if (adapter.Id != _adapterId)
            {
                _adapterId = adapter.Id;
                _lastSent = _lastReceived = -1;
            }
            var stats = adapter.GetIPStatistics();
            var now = DateTime.UtcNow;
            if (_lastSent >= 0)
            {
                var seconds = Math.Max(0.25, (now - _lastAt).TotalSeconds);
                Add(new ColituSpeedSample(Rate(stats.BytesSent, _lastSent, seconds), Rate(stats.BytesReceived, _lastReceived, seconds)));
            }
            _lastSent = stats.BytesSent;
            _lastReceived = stats.BytesReceived;
            _lastAt = now;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituTrafficMeter.Tick: {ex.GetType().Name}");
        }
    }

    internal void Add(ColituSpeedSample sample)
    {
        Latest = sample;
        _samples.Enqueue(sample);
        while (_samples.Count > Capacity)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Bytes per second between two counter readings; a counter that went back (adapter reset) counts as 0.</summary>
    internal static double Rate(long now, long before, double seconds) => now < before ? 0 : (now - before) / seconds;

    private NetworkInterface? PickAdapter()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces();
        var tun = adapters.FirstOrDefault(adapter => adapter.OperationalStatus == OperationalStatus.Up
            && (adapter.Name.Equals("singbox_tun", StringComparison.OrdinalIgnoreCase) || adapter.Name.Equals("xray_tun", StringComparison.OrdinalIgnoreCase)));
        TunnelOnly = tun != null;
        if (tun != null)
        {
            return tun;
        }
        var name = ColituNetwork.PhysicalInterfaceName();
        return adapters.FirstOrDefault(adapter => adapter.Name == name);
    }

    /// <summary>"1.2 MB/s", "340 KB/s", "0 B/s".</summary>
    public static string Format(double bytesPerSecond, string language, IFormatProvider? culture = null)
    {
        culture ??= System.Globalization.CultureInfo.CurrentCulture;
        string[] units = language == "ru" ? ["Б/с", "КБ/с", "МБ/с", "ГБ/с"] : ["B/s", "KB/s", "MB/s", "GB/s"];
        var value = Math.Max(0, bytesPerSecond);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var number = value.ToString(unit == 0 || value >= 100 ? "0" : "0.0", culture);
        return $"{number} {units[unit]}";
    }
}
