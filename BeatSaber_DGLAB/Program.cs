using System.Text;
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

        // 包含已配对设备:首次连接后会变已配对,后续还要扫得到
        var device = await ScanUntilFoundAsync(DglabProtocol.V3DeviceName, TimeSpan.FromSeconds(30), cts.Token);

        if (device is null)
        {
            Console.WriteLine($"\n[!] 没在 30 秒内扫到 {DglabProtocol.V3DeviceName}。检查电源和距离后再试。");
            return;
        }

        Console.WriteLine($"\n[+] 找到目标: {device.Name} ({device.Id})");

        // 首次连接尝试配对 (Windows 10+ 通常会自动,但显式一次更稳)
        if (device.Pairing.IsPaired == false && device.Pairing.CanPair)
        {
            Console.WriteLine("[*] 尝试配对...");
            var pairResult = await device.Pairing.PairAsync();
            if (pairResult.Status != DevicePairingResultStatus.Paired && pairResult.Status != DevicePairingResultStatus.AlreadyPaired)
            {
                Console.WriteLine($"[!] 配对失败: {pairResult.Status}");
                return;
            }
            Console.WriteLine("[+] 配对成功。");
        }

        Console.WriteLine("[*] 连接 GATT ...");
        using var client = await DglabV3Client.ConnectAsync(device, cts.Token);
        Console.WriteLine("[+] 已连接。");

        client.StrengthUpdated += (seq, a, b) =>
            Console.WriteLine($"[← B1] seq={seq}  A={a,3}  B={b,3}");
        client.BatteryUpdated += pct =>
            Console.WriteLine($"[← BAT] {pct}%");

        try
        {
            var battery = await client.ReadBatteryAsync();
            Console.WriteLine($"[*] 初始电量: {battery}%");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] 读电量失败: {ex.Message}");
        }

        // 重连后必须重新发送 BF (设置软上限 + 平衡参数)
        Console.WriteLine("[*] 写入 BF (软上限 200/200, 平衡 100/100)");
        await client.SendBfAsync();

        Console.WriteLine("[*] 开始 B0 循环: A 通道输出 '呼吸' 波形,每 100ms 一帧 (Ctrl+C 停止)\n");

        byte seq = 1;
        var t0 = DateTime.UtcNow;

        // 简化版 "呼吸" 波形: 强度从 0 → 30 → 0 循环
        var aFreq = new byte[] { 10, 10, 10, 10 };
        var aWave = new byte[] { 0, 10, 20, 30 };
        var bFreq = new byte[] { 10, 10, 10, 10 };
        var bWave = new byte[] { 0, 0, 0, 0 }; // B 通道静默

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                // 不修改强度 (aParse=bParse=0),仅刷新波形;后续每 N 帧绝对设一次 A=30
                var (aParse, bParse, aSet, bSet) =
                    ((DateTime.UtcNow - t0).TotalMilliseconds % 4000 < 100)
                        ? (DglabProtocol.StrengthParse.Absolute, DglabProtocol.StrengthParse.None, (byte)30, (byte)0)
                        : (DglabProtocol.StrengthParse.None, DglabProtocol.StrengthParse.None, (byte)0, (byte)0);

                await client.SendB0Async(seq, aParse, bParse, aSet, bSet, aFreq, aWave, bFreq, bWave);

                if (++seq > 15) seq = 1; // 0-15 循环
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] 写 B0 失败: {ex.Message}");
                break;
            }

            try { await Task.Delay(100, cts.Token); }
            catch (TaskCanceledException) { break; }
        }

        Console.WriteLine("\n[i] 停止,断开连接。");
    }

    /// <summary>扫描直到找到指定设备名,或超时。</summary>
    private static async Task<DeviceInformation?> ScanUntilFoundAsync(
        string deviceName, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<DeviceInformation?>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var scanner = new BluetoothScanner(includePaired: true);
        scanner.DeviceAdded += dev =>
        {
            PrintDevice("[+]", dev, seen);
            if (string.Equals(dev.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                tcs.TrySetResult(dev);
        };
        scanner.DeviceUpdated += dev => PrintDevice("[~]", dev, seen);

        scanner.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(null));

        Console.WriteLine($"[*] 扫描中... 寻找 {deviceName} (超时 {timeout.TotalSeconds}s)\n");
        return await tcs.Task;
    }

    private static void PrintHeader()
    {
        Console.WriteLine("===========================================");
        Console.WriteLine(" Beat Saber DGLAB  -  V3 客户端            ");
        Console.WriteLine("===========================================");
        Console.WriteLine($" 目标设备: {DglabProtocol.V3DeviceName}\n");
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