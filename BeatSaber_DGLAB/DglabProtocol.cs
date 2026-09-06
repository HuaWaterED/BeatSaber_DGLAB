namespace BeatSaber_DGLAB;

/// <summary>
/// DGLAB 郊狼 V3 蓝牙协议 — 纯数据包构造,不依赖任何 I/O。
/// 参考:dglab-bluetooth-protocol/coyote/v3/README.md
/// </summary>
public static class DglabProtocol
{
    /// <summary>脉冲主机 3.0 广播名</summary>
    public const string V3DeviceName = "47L121000";

    // 基础 UUID 格式:0000xxxx-0000-1000-8000-00805f9b34fb
    public static readonly Guid ServiceMain = new("0000180C-0000-1000-8000-00805f9b34fb"); // B0/B1 命令 + 通知
    public static readonly Guid ServiceBattery = new("0000180A-0000-1000-8000-00805f9b34fb"); // 电量

    public static readonly Guid CharWrite = new("0000150A-0000-1000-8000-00805f9b34fb");   // WRITE
    public static readonly Guid CharNotify = new("0000150B-0000-1000-8000-00805f9b34fb");  // NOTIFY
    public static readonly Guid CharBattery = new("00001500-0000-1000-8000-00805f9b34fb");  // READ/NOTIFY 电量

    /// <summary>强度值解读方式:high 2 bits = A, low 2 bits = B。</summary>
    public enum StrengthParse : byte
    {
        None = 0b00,         // 不修改
        Increase = 0b01,     // 相对增加
        Decrease = 0b10,     // 相对减少
        Absolute = 0b11,     // 绝对设置
    }

    /// <summary>
    /// BF 指令: 设置两通道强度软上限 + 波形频率平衡参数 + 波形强度平衡参数。
    /// 7 字节,断电保存,每次重连都必须重新发送。
    /// </summary>
    public static byte[] BuildBf(
        byte aSoftCap, byte bSoftCap,
        byte aFreqBalance, byte bFreqBalance,
        byte aStrengthBalance, byte bStrengthBalance)
    {
        return new byte[]
        {
            0xBF,
            aSoftCap, bSoftCap,
            aFreqBalance, bFreqBalance,
            aStrengthBalance, bStrengthBalance,
        };
    }

    /// <summary>
    /// B0 指令: 通道强度变化 + 两通道各 4 组波形数据。
    /// 20 字节,每 100ms 发送一次。V3 无需大小端转换。
    /// </summary>
    public static byte[] BuildB0(
        byte sequence,
        StrengthParse aParse, StrengthParse bParse,
        byte aStrength, byte bStrength,
        byte[] aFreq, byte[] aWave,
        byte[] bFreq, byte[] bWave)
    {
        if (aFreq.Length != 4 || aWave.Length != 4 || bFreq.Length != 4 || bWave.Length != 4)
            throw new ArgumentException("波形数组必须各 4 项");

        var buf = new byte[20];
        buf[0] = 0xB0;
        // byte1: high nibble = 序列号, low nibble = (A 解读 << 2 | B 解读)
        buf[1] = (byte)(((sequence & 0x0F) << 4) | (((byte)aParse & 0x03) << 2) | ((byte)bParse & 0x03));
        buf[2] = aStrength;
        buf[3] = bStrength;
        Buffer.BlockCopy(aFreq, 0, buf, 4, 4);
        Buffer.BlockCopy(aWave, 0, buf, 8, 4);
        Buffer.BlockCopy(bFreq, 0, buf, 12, 4);
        Buffer.BlockCopy(bWave, 0, buf, 16, 4);
        return buf;
    }

    /// <summary>
    /// 解析 B1 回应消息。返回 (序列号, A 当前强度, B 当前强度)。
    /// </summary>
    public static (byte seq, byte aStrength, byte bStrength)? ParseB1(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xB1) return null;
        return (data[1], data[2], data[3]);
    }
}