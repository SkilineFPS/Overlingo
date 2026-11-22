using System.IO;
using System.Media;
using Overlingo.Core;

namespace Overlingo.App;

/// <summary>Přehraje WAV na výchozím zvukovém výstupu Windows. Přerušení (Stop / zrušení) ukončí aktuální větu.</summary>
public sealed class WavPlayer : ISpeechPlayer
{
    public Task PlayAsync(byte[] wav, CancellationToken ct) => Task.Run(() =>
    {
        using var ms = new MemoryStream(wav);
        using var sp = new SoundPlayer(ms);
        sp.Load();
        using var reg = ct.Register(() => { try { sp.Stop(); } catch { } });
        if (!ct.IsCancellationRequested) sp.PlaySync();
    }, ct);
}
