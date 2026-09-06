using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace BeatSaber_DGLAB;

/// <summary>
/// DGLAB 郊狼 V3 主机 BLE GATT 客户端。
/// 职责:连接、订阅通知、读写 characteristic、收发 BF/B0/B1。
/// </summary>
public sealed class DglabV3Client : IDisposable
{
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _writeChar;
    private GattCharacteristic? _notifyChar;
    private GattCharacteristic? _batteryChar;
    private bool _disposed;

    public event Action<byte, byte, byte>? StrengthUpdated; // (seq, aStrength, bStrength)
    public event Action<byte>? BatteryUpdated;             // 0-100

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>通过 DeviceInformation 打开 GATT 连接。</summary>
    public static async Task<DglabV3Client> ConnectAsync(DeviceInformation deviceInfo, CancellationToken ct = default)
    {
        var client = new DglabV3Client();
        try
        {
            client._device = await BluetoothLEDevice.FromIdAsync(deviceInfo.Id);
            if (client._device is null)
                throw new InvalidOperationException("无法打开 BLE 设备 (FromIdAsync 返回 null)。设备可能未开启或不在范围内。");

            // 枚举所有 service,按 UUID 过滤 (WinRT 没有按 UUID 直查的 API)
            var svcResult = await client._device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (svcResult.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"枚举服务失败 ({svcResult.Status})");

            var mainSvc = svcResult.Services.FirstOrDefault(s => s.Uuid == DglabProtocol.ServiceMain)
                ?? throw new InvalidOperationException("找不到主服务 0x180C");
            var battSvc = svcResult.Services.FirstOrDefault(s => s.Uuid == DglabProtocol.ServiceBattery)
                ?? throw new InvalidOperationException("找不到电量服务 0x180A");

            client._writeChar = await FindCharAsync(mainSvc, DglabProtocol.CharWrite)
                ?? throw new InvalidOperationException("找不到 0x150A 写特性");
            client._notifyChar = await FindCharAsync(mainSvc, DglabProtocol.CharNotify)
                ?? throw new InvalidOperationException("找不到 0x150B 通知特性");
            client._batteryChar = await FindCharAsync(battSvc, DglabProtocol.CharBattery)
                ?? throw new InvalidOperationException("找不到 0x1500 电量特性");

            // 订阅通知
            client._notifyChar.ValueChanged += client.OnNotifyChanged;
            var sub = await client._notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (sub != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"订阅 0x150B 通知失败 ({sub})");

            // 电池也订阅通知 (主机也会主动推送)
            client._batteryChar.ValueChanged += client.OnBatteryChanged;
            await client._batteryChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<GattCharacteristic?> FindCharAsync(GattDeviceService svc, Guid uuid)
    {
        var result = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        return result.Status == GattCommunicationStatus.Success
            ? result.Characteristics.FirstOrDefault(c => c.Uuid == uuid)
            : null;
    }

    /// <summary>读取电池 (0-100)。</summary>
    public async Task<byte> ReadBatteryAsync()
    {
        var r = await _batteryChar!.ReadValueAsync();
        if (r.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"读电池失败 ({r.Status})");
        CryptographicBuffer.CopyToByteArray(r.Value, out var bytes);
        if (bytes is null || bytes.Length == 0) return 0;
        return bytes[0];
    }

    /// <summary>写入 BF 指令 (软上限 + 平衡参数)。重连后必须调用。</summary>
    public async Task SendBfAsync(
        byte aSoftCap = 200, byte bSoftCap = 200,
        byte aFreqBalance = 100, byte bFreqBalance = 100,
        byte aStrengthBalance = 100, byte bStrengthBalance = 100)
    {
        var data = DglabProtocol.BuildBf(aSoftCap, bSoftCap, aFreqBalance, bFreqBalance, aStrengthBalance, bStrengthBalance);
        var r = await _writeChar!.WriteValueAsync(AsBuffer(data));
        if (r != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"写 BF 失败 ({r})");
    }

    /// <summary>写入 B0 指令 (强度变化 + 波形数据)。每 100ms 调用一次。</summary>
    public async Task SendB0Async(
        byte sequence,
        DglabProtocol.StrengthParse aParse, DglabProtocol.StrengthParse bParse,
        byte aStrength, byte bStrength,
        byte[] aFreq, byte[] aWave, byte[] bFreq, byte[] bWave)
    {
        var data = DglabProtocol.BuildB0(sequence, aParse, bParse, aStrength, bStrength, aFreq, aWave, bFreq, bWave);
        var r = await _writeChar!.WriteValueAsync(AsBuffer(data));
        if (r != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"写 B0 失败 ({r})");
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