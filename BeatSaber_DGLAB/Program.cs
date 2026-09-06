using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BeatSaber_DGLAB;

internal class Program
{
    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        PrintHeader();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 双路径并行扫描,谁先扫到 47L121000 谁触发连接;tcs 传回已就绪的客户端
        var findTcs = new TaskCompletionSource<DglabV3Client?>();

        // 路径 A:BluetoothLEAdvertisementWatcher — 原始广播,辅助调试 (光看到地址连不上,因为没 DeviceInformation 不能配对)
        using var advScanner = new BleAdvertisementScanner();
        advScanner.AdvertisementReceived += (addr, name, rssi) =>
        {
            var marker = name == DglabProtocol.V3DeviceName ? " ★ DGLAB V3!" : "  ";
            Console.WriteLine($"[adv] {addr:X12}  {name,-24}  RSSI:{rssi,4} dBm{marker}");
        };
        advScanner.Start();

        // 路径 B:DeviceWatcher — 这里要 includePaired:false,因为只有未配对路径才会枚举新发现的 DGLAB
        using var devScanner = new BluetoothScanner(includePaired: false);
        var seenDevs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        devScanner.DeviceAdded += dev =>
        {
            PrintDevice("[dev +]", dev, seenDevs);
            if (string.Equals(dev.Name, DglabProtocol.V3DeviceName, StringComparison.OrdinalIgnoreCase))
                _ = ConnectFromDeviceAsync(dev, findTcs, cts.Token);
        };
        devScanner.DeviceUpdated += dev => PrintDevice("[dev ~]", dev, seenDevs);
        devScanner.Start();

        Console.WriteLine("[*] 扫描中 (Ctrl+C 停止) ...\n");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
        using var reg = timeoutCts.Token.Register(() => findTcs.TrySetResult(null));

        var client = await findTcs.Task;
        if (client is null)
        {
            Console.WriteLine("\n[!] 60 秒内没找到 47L121000。常见原因:");
            Console.WriteLine("    1. 手机正连着它 (BLE 连接中设备停止广播)");
            Console.WriteLine("    2. 主机需要按一下按钮进入配对模式");
            Console.WriteLine("    3. 主机距离 > 5m 或在另一个房间");
            Console.WriteLine("    把手机断开连接,主机靠近并按电源键后再试。");
            return;
        }

        await RunDemoLoopAsync(client, cts.Token);
    }

    private static async Task ConnectFromAddressAsync(ulong addr, TaskCompletionSource<DglabV3Client?> tcs, CancellationToken ct)
    {
        // 仅作为未来扩展点保留:Win32 unpackaged 没有 DeviceInformation 就无法配对,
        // 所以仅靠广播地址无法完成连接。当前走 DeviceInformation 路径。
        await Task.CompletedTask;
    }

    private static async Task ConnectFromDeviceAsync(DeviceInformation dev, TaskCompletionSource<DglabV3Client?> tcs, CancellationToken ct)
    {
        if (tcs.Task.IsCompleted) return;
        try
        {
            // 别手动调 PairAsync:DeviceInformation.Pairing 对 BLE Just Works 不友好,
            // OS 会自己处理。直接靠 InitializeAsync 里的 retry 等配对完成。
            Console.WriteLine($"[*] DevicePairing: IsPaired={dev.Pairing.IsPaired} CanPair={dev.Pairing.CanPair}");
            Console.WriteLine("[*] 通过 DeviceInformation 连接 (InitializeAsync 内部含重试,等 OS 完成 Just Works 配对) ...");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var client = await DglabV3Client.ConnectAsync(dev, linkedCts.Token);
            tcs.TrySetResult(client);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 连接失败 (DeviceInformation 路径): {ex.Message}");
        }
    }

    private static async Task RunDemoLoopAsync(DglabV3Client client, CancellationToken ct)
    {
        Console.WriteLine("[+] 已连接。");
        client.StrengthUpdated += (seq, a, b) => Console.WriteLine($"[← B1] seq={seq}  A={a,3}  B={b,3}");
        client.BatteryUpdated += pct => Console.WriteLine($"[← BAT] {pct}%");

        try
        {
            var battery = await client.ReadBatteryAsync();
            Console.WriteLine($"[*] 初始电量: {battery}%");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 读电量失败: {ex.Message}");
        }

        Console.WriteLine("[*] 写入 BF (软上限 200/200, 平衡 100/100)");
        await client.SendBfAsync();

        Console.WriteLine("[*] 开始 B0 循环: A 通道 4s 设一次绝对强度 30,其余帧刷波形 (Ctrl+C 停止)\n");

        byte seq = 1;
        var t0 = DateTime.UtcNow;
        var aFreq = new byte[] { 10, 10, 10, 10 };
        var aWave = new byte[] { 0, 10, 20, 30 };
        var bFreq = new byte[] { 10, 10, 10, 10 };
        var bWave = new byte[] { 0, 0, 0, 0 };

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (aParse, bParse, aSet, bSet) =
                    ((DateTime.UtcNow - t0).TotalMilliseconds % 4000 < 100)
                        ? (DglabProtocol.StrengthParse.Absolute, DglabProtocol.StrengthParse.None, (byte)30, (byte)0)
                        : (DglabProtocol.StrengthParse.None, DglabProtocol.StrengthParse.None, (byte)0, (byte)0);

                await client.SendB0Async(seq, aParse, bParse, aSet, bSet, aFreq, aWave, bFreq, bWave);
                if (++seq > 15) seq = 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] 写 B0 失败: {ex.Message}");
                break;
            }
            try { await Task.Delay(100, ct); }
            catch (TaskCanceledException) { break; }
        }

        Console.WriteLine("\n[i] 停止,断开连接。");
        client.Dispose();
    }

    private static void PrintHeader()
    {
        Console.WriteLine("===========================================");
        Console.WriteLine(" Beat Saber DGLAB  -  V3 客户端            ");
        Console.WriteLine("===========================================");
        Console.WriteLine($" 目标设备: {DglabProtocol.V3DeviceName}");
        Console.WriteLine(" 双路径扫描: 原始广播 (AdvertisementWatcher)");
        Console.WriteLine("           + 已知设备 (DeviceWatcher)\n");
    }

    private static void PrintDevice(string tag, DeviceInformation device, HashSet<string> seen)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? "(未命名)" : device.Name;
        var address = Prop(device, "System.Devices.Aep.DeviceAddress") ?? "?";
        var rssi = Prop(device, "System.Devices.Aep.SignalStrength") ?? "?";
        var paired = Prop(device, "System.Devices.Aep.IsPaired") ?? "?";

        var isDglab = BluetoothScanner.DglabNameHints
            .Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

        if (isDglab || seen.Add(device.Id))
        {
            var marker = isDglab ? " ★" : "  ";
            Console.WriteLine($"{tag} {marker}{name,-24} {address,-20} RSSI:{rssi,4} dBm  paired:{paired}");
        }
    }

    private static string? Prop(DeviceInformation device, string key)
        => device.Properties.TryGetValue(key, out var v) ? v?.ToString() : null;
}