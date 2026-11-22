using System.Text;

namespace Overlingo.Core;

/// <summary>Drobné úpravy WAV (RIFF, 16bit PCM): kontrola hlavičky a hlasitost bez dalších balíčků.</summary>
public static class WavTools
{
    public static bool LooksLikeWav(byte[] d) =>
        d.Length > 44 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';

    /// <summary>Zesílení / zeslabení 16bit PCM dat (gain 0–1.5). Vrací novou kopii; ostatní hlavičky zůstanou. Jiný formát než 16bit PCM se nezmění.</summary>
    public static byte[] ScaleVolume(byte[] wav, double gain)
    {
        gain = Math.Clamp(gain, 0, 1.5);
        if (!LooksLikeWav(wav) || Math.Abs(gain - 1.0) < 0.001) return wav;
        int pos = 12; int dataStart = -1, dataLen = 0; int bits = 0, fmt = 0;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int len = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt " && pos + 8 + 16 <= wav.Length) { fmt = BitConverter.ToInt16(wav, pos + 8); bits = BitConverter.ToInt16(wav, pos + 8 + 14); }
            if (id == "data") { dataStart = pos + 8; dataLen = len < 0 || dataStart + len > wav.Length ? wav.Length - dataStart : len; break; }
            if (len < 0) return wav;
            pos += 8 + len + (len & 1);
        }
        if (dataStart < 0 || fmt != 1 || bits != 16) return wav;
        var res = (byte[])wav.Clone();
        for (int i = dataStart; i + 1 < dataStart + dataLen; i += 2)
        {
            int v = (short)(res[i] | (res[i + 1] << 8));
            v = (int)Math.Clamp(Math.Round(v * gain), short.MinValue, short.MaxValue);
            res[i] = (byte)(v & 0xFF); res[i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return res;
    }

    /// <summary>Délka záznamu v sekundách (pro odhad, jak dlouho věta zní); 0, když ji nejde zjistit.</summary>
    public static double DurationSeconds(byte[] wav)
    {
        if (!LooksLikeWav(wav) || wav.Length < 44) return 0;
        int byteRate = BitConverter.ToInt32(wav, 28);
        return byteRate <= 0 ? 0 : Math.Max(0, (wav.Length - 44.0) / byteRate);
    }
}
