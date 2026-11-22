namespace Overlingo.Core;

/// <summary>Zdroj hlasu: převede text na WAV (Azure, nebo hlas nainstalovaný ve Windows). Rate 1.0 = běžná rychlost.</summary>
public interface ISpeechSynth
{
    Task<byte[]> SynthesizeAsync(string text, double rate, CancellationToken ct);
}

/// <summary>Přehrávač hotového WAV (v aplikaci přes Windows). Play se vrátí, až věta dozní nebo je přerušena.</summary>
public interface ISpeechPlayer
{
    Task PlayAsync(byte[] wav, CancellationToken ct);
}

/// <summary>
/// Čte přeložené titulky nahlas přes Azure. Syntéza běží v předstihu (příští věta se připravuje, zatímco předchozí zní),
/// fronta je krátká a při zpoždění se mluví rychleji. Chyby se hlásí událostí Error (max. jednou za 30 s pro stejnou zprávu),
/// nikdy se nepřepíná na jiný hlas ani službu.
/// </summary>
public sealed class SpeechService : IDisposable
{
    readonly ISpeechSynth _client;
    readonly ISpeechPlayer _player;
    readonly SpeechPlanner _plan = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _cts = new();
    readonly Task _loop;
    readonly Func<DateTime> _now;
    string _lastErr = ""; DateTime _lastErrAt = DateTime.MinValue;
    long _chars;
    volatile bool _muted;

    public double Volume { get; set; } = 1.0;
    public event Action<string>? Error;
    public long CharsSent => Interlocked.Read(ref _chars);
    public int Spoken { get; private set; }
    public int Dropped => _plan.Dropped;
    public bool Muted { get => _muted; set { _muted = value; if (value) _plan.Clear(); } }

    public SpeechService(ISpeechSynth client, ISpeechPlayer player, int maxQueue, double baseRate, Func<DateTime>? now = null)
    {
        _client = client; _player = player; _now = now ?? (() => DateTime.UtcNow);
        _plan.MaxQueue = maxQueue; _plan.BaseRate = Math.Clamp(baseRate, 0.5, 2.0); _plan.MaxRate = Math.Min(2.0, _plan.BaseRate + 0.5);
        _loop = Task.Run(Loop);
    }

    /// <summary>Přidá přeložený titulek. replace = true: stejný titulek se přeložil znovu (oprava) – nahradí čekající větu.</summary>
    public void Speak(string text, bool replace = false)
    {
        if (_muted) return;
        _plan.Enqueue(text, replace, _now());
        _signal.Release();
    }

    public void Stop() => _plan.Clear();

    void Report(string msg)
    {
        var now = _now();
        if (msg == _lastErr && (now - _lastErrAt).TotalSeconds < 30) return;
        _lastErr = msg; _lastErrAt = now;
        Error?.Invoke(msg);
    }

    async Task Loop()
    {
        var ct = _cts.Token;
        Task<byte[]?>? ahead = null; SpeechItem? aheadItem = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (ahead == null)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    if (!_plan.TryDequeue(_now(), 0, out var it)) continue;
                    aheadItem = it; ahead = Synth(it, ct);
                }
                var item = aheadItem!; var wavTask = ahead; ahead = null; aheadItem = null;
                // Předsyntéza další věty během přehrávání této.
                if (_plan.TryDequeue(_now(), 1, out var next)) { aheadItem = next; ahead = Synth(next, ct); }
                var wav = await wavTask.ConfigureAwait(false);
                if (wav == null || _muted) continue;
                try { await _player.PlayAsync(WavTools.ScaleVolume(wav, Volume), ct).ConfigureAwait(false); Spoken++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Report("Hlas: přehrávání selhalo: " + e.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task<byte[]?> Synth(SpeechItem it, CancellationToken ct)
    {
        try
        {
            Interlocked.Add(ref _chars, it.Text.Length);
            return await _client.SynthesizeAsync(it.Text, it.Rate, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Report(e is TranslationException ? e.Message : "Hlas: " + e.Message); return null; }
    }

    public void Dispose() { _cts.Cancel(); try { _loop.Wait(500); } catch { } _cts.Dispose(); }
}
