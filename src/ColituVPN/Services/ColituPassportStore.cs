using System.Text.Json;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Key/value store of the Colitu Passport (the phones keep the same keys in MMKV). One JSON file
/// next to the app's other settings; only this device reads or writes it. Without a path (tests)
/// it lives in memory only. Every call is thread-safe; a change is written at once (atomically).
/// </summary>
public sealed class ColituPassportStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values;

    public ColituPassportStore(string? path)
    {
        _path = path;
        _values = Load(path);
    }

    private static Dictionary<string, string> Load(string? path)
    {
        try
        {
            if (path != null && File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituPassportStore.Load", ex);
        }
        return [];
    }

    public string? GetString(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public long GetLong(string key, long fallback = 0) =>
        long.TryParse(GetString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        GetString(key) switch { "1" => true, "0" => false, _ => fallback };

    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (value == null)
            {
                if (!_values.Remove(key)) return;
            }
            else
            {
                if (_values.TryGetValue(key, out var old) && old == value) return;
                _values[key] = value;
            }
            Save();
        }
    }

    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string key, long value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string key, bool value) => Set(key, value ? "1" : "0");

    public void Remove(string key) => Set(key, (string?)null);

    /// <summary>Several changes, one write.</summary>
    public void Batch(Action<ColituPassportStore> changes)
    {
        lock (_gate)
        {
            _batch++;
            try
            {
                changes(this);
            }
            finally
            {
                _batch--;
                if (_batch == 0 && _dirty) Save();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            Save();
        }
    }

    private int _batch;
    private bool _dirty;

    private void Save()
    {
        if (_batch > 0)
        {
            _dirty = true;
            return;
        }
        _dirty = false;
        if (_path == null) return;
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_values));
            File.Move(temp, _path, true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituPassportStore.Save", ex);
        }
    }
}
