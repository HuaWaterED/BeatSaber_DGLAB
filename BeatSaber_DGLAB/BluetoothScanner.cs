using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BeatSaber_DGLAB;

/// <summary>
/// 封装 Windows.Devices.Enumeration.DeviceWatcher 的 BLE/经典蓝牙扫描器。
/// 用 DeviceWatcher 而不是 FindAllAsync,因为 Watcher 会持续接收广播,
/// 更接近手机/PC 蓝牙设置里的"扫描"体验,也能拿到 RSSI 等实时属性。
/// </summary>
public sealed class BluetoothScanner : IDisposable
{
    // DGLAB 主机广播名/常见命名规则,扫描时方便高亮过滤
    public static readonly string[] DglabNameHints =
    {
        "D-LAB", "D-Lab", "DLAB", "DG-LAB", "DG Lab",
        "Coyote", "郊激", "郊激仪",
    };

    private readonly string _selector;
    private readonly string[] _properties;
    private DeviceWatcher? _watcher;
    private bool _disposed;

    public event Action<DeviceInformation>? DeviceAdded;
    public event Action<DeviceInformation>? DeviceUpdated;
    public event Action? EnumerationCompleted;

    public BluetoothScanner(bool includePaired = false)
    {
        // false = 只看未配对(更接近"主动扫描"的语义);true = 已配对也包含
        _selector = BluetoothDevice.GetDeviceSelectorFromPairingState(includePaired);

        // 这些属性在订阅时一并请求,避免后续反复查表
        _properties = new[]
        {
            "System.Devices.Aep.DeviceAddress",
            "System.Devices.Aep.IsConnected",
            "System.Devices.Aep.IsPaired",
            "System.Devices.Aep.SignalStrength",
            "System.Devices.Aep.Bluetooth.Le.IsConnectable",
            "System.Devices.Aep.Bluetooth.Le.IsDiscoverable",
        };
    }

    /// <summary>开始扫描。事件触发在调用线程 (这里是控制台主线程) 的 SynchronizationContext 上。</summary>
    public void Start()
    {
        if (_watcher is not null)
            throw new InvalidOperationException("Scanner already started.");

        _watcher = DeviceInformation.CreateWatcher(_selector, _properties);
        _watcher.Added += OnAdded;
        _watcher.Updated += OnUpdated;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Stopped += OnStopped;
        _watcher.Start();
    }

    public void Stop()
    {
        _watcher?.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.Added -= OnAdded;
            _watcher.Updated -= OnUpdated;
            _watcher.EnumerationCompleted -= OnEnumerationCompleted;
            _watcher.Stopped -= OnStopped;
            _watcher.Stop();
            _watcher = null;
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation args) => DeviceAdded?.Invoke(args);

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        // DeviceInformationUpdate 只携带 Id + 变动字段,需要查回来拿 Name/Properties
        // 这里 Fire-and-forget,把结果通过 DeviceUpdated 抛出去
        _ = ResolveUpdateAsync(args.Id);
    }

    private async Task ResolveUpdateAsync(string id)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(id);
            if (info is not null) DeviceUpdated?.Invoke(info);
        }
        catch (Exception)
        {
            // 设备可能在更新过程中已消失,忽略
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        => EnumerationCompleted?.Invoke();
    private void OnStopped(DeviceWatcher sender, object args) { }
}