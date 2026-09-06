using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace BeatSaber_DGLAB;

/// <summary>
/// DGLAB 郊狼 V3 主机 BLE GATT 客户端。
/// 重要: unpackaged Win32 控制台应用配对 BLE Just Works 设备时,
///       OS 在后台异步完成配对握手。前几次 GATT 调用会被 AccessDenied / UnauthorizedAccess,
///       必须带重试 + 延迟,直到 OS 完成配对。
/// </summary>
public sealed class DglabV3Client : IDisposable
{
    private const int MaxRetries = 12;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private GattCharacteristic? _batteryChar;
    private bool _disposed;

    public event Action<byte, byte, byte>? StrengthUpdated; // (seq, aStrength, bStrength)
    public event Action<byte>? BatteryUpdated;             // 0-100

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>通过蓝牙地址 (来自 BleAdvertisementScanner) 打开 GATT 连接。</summary>
    public static async Task<DglabV3Client> ConnectAsync(ulong bluetoothAddress, CancellationToken ct = default)
    {
        var client = new DglabV3Client();
        try
        {
            client._device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (client._device is null)
                throw new InvalidOperationException("无法打开 BLE 设备 (FromBluetoothAddressAsync 返回 null)。");

            await client.InitializeAsync(ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>通过 DeviceInformation 打开 GATT 连接。</summary>
    public static async Task<DglabV3Client> ConnectAsync(DeviceInformation deviceInfo, CancellationToken ct = default)
    {
        var client = new DglabV3Client();
        try
        {
            // FromIdAsync 在 OS 配对未完成时可能抛 UnauthorizedAccessException,需要重试
            for (int i = 0; i < MaxRetries && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    client._device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
                    if (client._device is not null) break;
                    Console.WriteLine($"[init] FromIdAsync 返回 null,重试 {i + 1}/{MaxRetries}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[init] FromIdAsync 抛错 {ex.GetType().Name}: {ex.Message},重试 {i + 1}/{MaxRetries}");
                }
                await Task.Delay(RetryDelay, ct);
            }
            if (client._device is null)
                throw new InvalidOperationException("FromIdAsync 重试耗尽,设备无法打开");

            await client.InitializeAsync(ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        // 枚举所有 service (可能因配对未完成返回 AccessDenied,需要重试)
        GattDeviceService? mainSvc = null;
        GattDeviceService? battSvc = null;

        for (int i = 0; i < MaxRetries && !ct.IsCancellationRequested; i++)
        {
            var svcResult = await _device!.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svcResult.Status == GattCommunicationStatus.Success)
            {
                mainSvc = svcResult.Services.FirstOrDefault(s => s.Uuid == DglabProtocol.ServiceMain);
                battSvc = svcResult.Services.FirstOrDefault(s => s.Uuid == DglabProtocol.ServiceBattery);
                if (mainSvc is not null && battSvc is not null) break;
            }
            Console.WriteLine($"[init] 服务枚举重试 {i + 1}/{MaxRetries} (status={svcResult.Status})");
            await Task.Delay(RetryDelay, ct);
        }
        if (mainSvc is null) throw new InvalidOperationException("找不到主服务 0x180C (重试后)");
        if (battSvc is null) throw new InvalidOperationException("找不到电量服务 0x180A (重试后)");

        _writeChar = await FindCharAsync(mainSvc, DglabProtocol.CharWrite, ct)
            ?? throw new InvalidOperationException("找不到 0x150A 写特性 (重试后)");
        _notifyChar = await FindCharAsync(mainSvc, DglabProtocol.CharNotify, ct)
            ?? throw new InvalidOperationException("找不到 0x150B 通知特性 (重试后)");
        _batteryChar = await FindCharAsync(battSvc, DglabProtocol.CharBattery, ct)
            ?? throw new InvalidOperationException("找不到 0x1500 电量特性 (重试后)");

        // 订阅通知 — 0x150B (主命令响应)
        _notifyChar.ValueChanged += OnNotifyChanged;
        await RetryAsync(async () =>
            await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify),
            "订阅 0x150B", ct,
            s => s == GattCommunicationStatus.Success);

        // 0x1500 (电量):0x180A 是标准 "Device Information" 服务 UUID,Windows 对自定义 characteristic
        // 订阅会拒 (UnauthorizedAccessException)。订阅是可选的,主程序用 ReadBatteryAsync 按需读即可。
        // 留一个开关,真需要 push 通知再尝试。
        // _batteryChar.ValueChanged += OnBatteryChanged;
        // await RetryAsync(... "订阅 0x1500" ..., ct);
    }

    private static async Task<GattCharacteristic?> FindCharAsync(GattDeviceService svc, Guid uuid, CancellationToken ct)
    {
        for (int i = 0; i < MaxRetries && !ct.IsCancellationRequested; i++)
        {
            var result = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                var found = result.Characteristics.FirstOrDefault(c => c.Uuid == uuid);
                if (found is not null) return found;
            }
            Console.WriteLine($"[init] char {uuid} 枚举重试 {i + 1}/{MaxRetries} (status={result.Status})");
            await Task.Delay(RetryDelay, ct);
        }
        return null;
    }

    /// <summary>读取电池 (0-100)。带重试。</summary>
    public async Task<byte> ReadBatteryAsync()
    {
        var r = await RetryAsync(async () => await _batteryChar!.ReadValueAsync(), "读电池", default);
        if (r.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"读电池失败 ({r.Status})");
        CryptographicBuffer.CopyToByteArray(r.Value, out var bytes);
        if (bytes is null || bytes.Length == 0) return 0;
        return bytes[0];
    }

    /// <summary>写入 BF 指令 (软上限 + 平衡参数)。重连后必须调用。带重试。</summary>
    public async Task SendBfAsync(
        byte aSoftCap = 200, byte bSoftCap = 200,
        byte aFreqBalance = 100, byte bFreqBalance = 100,
        byte aStrengthBalance = 100, byte bStrengthBalance = 100)
    {
        var data = DglabProtocol.BuildBf(aSoftCap, bSoftCap, aFreqBalance, bFreqBalance, aStrengthBalance, bStrengthBalance);
        var status = await RetryAsync(async () => await _writeChar!.WriteValueAsync(AsBuffer(data)), "写 BF", default,
            s => s == GattCommunicationStatus.Success);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"写 BF 失败 ({status})");
    }

    /// <summary>写入 B0 指令 (强度变化 + 波形数据)。每 100ms 调用一次。带重试。</summary>
    public async Task SendB0Async(
        byte sequence,
        DglabProtocol.StrengthParse aParse, DglabProtocol.StrengthParse bParse,
        byte aStrength, byte bStrength,
        byte[] aFreq, byte[] aWave, byte[] bFreq, byte[] bWave)
    {
        var data = DglabProtocol.BuildB0(sequence, aParse, bParse, aStrength, bStrength, aFreq, aWave, bFreq, bWave);
        var status = await RetryAsync(async () => await _writeChar!.WriteValueAsync(AsBuffer(data)), "写 B0", default,
            s => s == GattCommunicationStatus.Success);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"写 B0 失败 ({status})");
    }

    private void OnNotifyChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        if (bytes is null) return;
        var parsed = DglabProtocol.ParseB1(bytes);
        if (parsed is { } p) StrengthUpdated?.Invoke(p.seq, p.aStrength, p.bStrength);
    }

    private void OnBatteryChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        if (bytes is null || bytes.Length == 0) return;
        BatteryUpdated?.Invoke(bytes[0]);
    }

    private static IBuffer AsBuffer(byte[] data) => CryptographicBuffer.CreateFromByteArray(data);

    /// <summary>
    /// 重试包装: GATT 操作前几次可能因 OS 配对未完成而失败 (抛异常 OR 返回 AccessDenied),
    /// 这里重试到 MaxRetries 次,每次间隔 RetryDelay。
    /// isSuccess 为 null 时只看是否抛异常。
    /// </summary>
    private static async Task<T> RetryAsync<T>(Func<Task<T>> op, string what, CancellationToken ct, Func<T, bool>? isSuccess = null)
    {
        Exception? lastEx = null;
        T? lastResult = default;
        for (int i = 0; i < MaxRetries && !ct.IsCancellationRequested; i++)
        {
            try
            {
                lastResult = await op();
                if (isSuccess is null || isSuccess(lastResult)) return lastResult;
                Console.WriteLine($"[{what}] 重试 {i + 1}/{MaxRetries} (status={lastResult})");
            }
            catch (Exception ex)
            {
                lastEx = ex;
                Console.WriteLine($"[{what}] 重试 {i + 1}/{MaxRetries} (ex: {ex.GetType().Name}: {ex.Message})");
            }
            await Task.Delay(RetryDelay, ct);
        }
        throw lastEx ?? new InvalidOperationException($"{what} 重试耗尽 (last={lastResult})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_notifyChar is not null) _notifyChar.ValueChanged -= OnNotifyChanged;
        if (_batteryChar is not null) _batteryChar.ValueChanged -= OnBatteryChanged;
        _device?.Dispose();
        _device = null;
    }
}