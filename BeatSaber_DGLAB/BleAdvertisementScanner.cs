using Windows.Devices.Bluetooth.Advertisement;

namespace BeatSaber_DGLAB;

/// <summary>
/// 原始 BLE 广播扫描器,基于 BluetoothLEAdvertisementWatcher。
/// 不走 DeviceInformation 缓存/已配对过滤,直接监听空口广播。
/// 适合发现尚未配对的、未知的、或当前不被 DeviceInformation 枚举的 BLE 设备。
/// </summary>
public sealed class BleAdvertisementScanner : IDisposable
{
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    public event Action<ulong /*BluetoothAddress*/, string /*LocalName*/, short /*RSSI*/>? AdvertisementReceived;

    public void Start()
    {
        if (_watcher is not null) return;

        _watcher = new BluetoothLEAdvertisementWatcher
        {
            // Active 扫描会主动发 scan request,能拿到更多 advertisement data (含 LocalName)
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        _watcher.Received += OnReceived;
        _watcher.Start();
    }

    public void Stop() => _watcher?.Stop();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.Stop();
            _watcher.Received -= OnReceived;
            _watcher = null;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName ?? string.Empty;
        AdvertisementReceived?.Invoke(args.BluetoothAddress, name, args.RawSignalStrengthInDBm);
    }
}