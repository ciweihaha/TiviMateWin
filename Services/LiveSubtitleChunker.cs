namespace TiviMateWin.Services;

/// <summary>
/// 实时字幕音频切块器：48kHz 立体声 PCM → 16kHz 单声道 → 定长窗口 + 能量门。
/// 纯逻辑，不依赖 LibVLC / NAudio / WPF，可进单元测试工程。
/// </summary>
public sealed class LiveSubtitleChunker
{
    public const int InputSampleRate = 48000;
    public const int InputChannels = 2;
    public const int TargetSampleRate = 16000;

    /// <summary>能量门阈值（RMS，相对满幅 32768）：低于此值视为静音，跳过听写。</summary>
    public const double SilenceRmsThreshold = 0.004;

    private readonly int _windowSamples;
    private readonly short[] _window;
    private int _filled;
    // 同一调用内满出多窗时依次排队（实际管线里单次喂入远小于一窗，队列基本是空的）。
    private readonly Queue<short[]> _ready = new();

    public LiveSubtitleChunker(int windowSeconds = 3)
    {
        if (windowSeconds < 1 || windowSeconds > 10)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds), "切块窗口须在 1–10 秒之间");
        _windowSamples = TargetSampleRate * windowSeconds;
        _window = new short[_windowSamples];
    }

    /// <summary>
    /// 48kHz 立体声交错 PCM（16 位）重采样为 16kHz 单声道：
    /// 左右声道取平均，再每 3 帧取一次平均（48k→16k，顺带低通）。
    /// 输入样点数须为偶数（立体声帧完整）；不足 3 帧的尾部直接丢弃（不足 0.1 毫秒）。
    /// </summary>
    public static short[] ResampleStereo48kToMono16k(ReadOnlySpan<short> stereo48k)
    {
        var frames = stereo48k.Length / InputChannels;
        var outCount = frames / 3;
        var mono = new short[outCount];
        for (var i = 0; i < outCount; i++)
        {
            var baseIdx = i * 3 * InputChannels;
            // 3 帧 × 2 声道 = 6 个样点取平均
            var sum = 0;
            for (var j = 0; j < 3 * InputChannels; j++) sum += stereo48k[baseIdx + j];
            mono[i] = (short)(sum / (3 * InputChannels));
        }
        return mono;
    }

    /// <summary>
    /// 喂入 16kHz 单声道样点；窗口满时返回完整窗口（新数组），未满返回 null。
    /// 一次喂入超过一窗时，超出的部分继续累积进下一窗（不丢数据）；同一调用内
    /// 满出多窗时依次排队，下次调用（哪怕喂空数据）返回下一窗。
    /// </summary>
    public short[]? Feed(ReadOnlySpan<short> mono16k)
    {
        var offset = 0;
        while (offset < mono16k.Length)
        {
            var take = Math.Min(_windowSamples - _filled, mono16k.Length - offset);
            mono16k.Slice(offset, take).CopyTo(_window.AsSpan(_filled, take));
            _filled += take;
            offset += take;
            if (_filled >= _windowSamples)
            {
                _filled = 0;
                _ready.Enqueue(_window.ToArray());
            }
        }
        return _ready.Count > 0 ? _ready.Dequeue() : null;
    }

    /// <summary>能量门：窗内 RMS 低于阈值视为静音/无语音，跳过听写（省 CPU、防 Whisper 幻觉）。</summary>
    public static bool IsSilent(ReadOnlySpan<short> mono16k, double threshold = SilenceRmsThreshold)
    {
        if (mono16k.Length == 0) return true;
        double sumSquares = 0;
        foreach (var s in mono16k)
        {
            var v = s / 32768.0;
            sumSquares += v * v;
        }
        var rms = Math.Sqrt(sumSquares / mono16k.Length);
        return rms < threshold;
    }

    /// <summary>16 位 16kHz 单声道 PCM 包成 WAV 字节（Whisper 要 WAV 流）：44 字节头 + PCM。</summary>
    public static byte[] BuildWav(ReadOnlySpan<short> mono16k)
    {
        var dataBytes = mono16k.Length * 2;
        var wav = new byte[44 + dataBytes];
        // RIFF 头
        WriteAscii(wav, 0, "RIFF");
        WriteInt32(wav, 4, 36 + dataBytes);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt32(wav, 16, 16);          // fmt 块长度
        WriteInt16(wav, 20, 1);           // PCM
        WriteInt16(wav, 22, 1);           // 单声道
        WriteInt32(wav, 24, TargetSampleRate);
        WriteInt32(wav, 28, TargetSampleRate * 2); // 每秒字节数
        WriteInt16(wav, 32, 2);           // 块对齐
        WriteInt16(wav, 34, 16);          // 位深
        WriteAscii(wav, 36, "data");
        WriteInt32(wav, 40, dataBytes);
        // PCM 数据（小端）
        for (var i = 0; i < mono16k.Length; i++)
        {
            var v = mono16k[i];
            wav[44 + i * 2] = (byte)(v & 0xFF);
            wav[44 + i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return wav;
    }

    /// <summary>Whisper 常出的非语音标记（[music]、[applause] 等）与空白：应丢弃，不显示、不翻译。</summary>
    public static bool IsNonSpeechMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        return t.Length >= 2 && t[0] == '[' && t[^1] == ']';
    }

    private static void WriteAscii(byte[] buf, int offset, string s)
    {
        for (var i = 0; i < s.Length; i++) buf[offset + i] = (byte)s[i];
    }

    private static void WriteInt16(byte[] buf, int offset, short v)
    {
        buf[offset] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void WriteInt32(byte[] buf, int offset, int v)
    {
        buf[offset] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)((v >> 8) & 0xFF);
        buf[offset + 2] = (byte)((v >> 16) & 0xFF);
        buf[offset + 3] = (byte)((v >> 24) & 0xFF);
    }
}
