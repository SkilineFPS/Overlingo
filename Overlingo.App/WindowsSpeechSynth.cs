using Overlingo.Core;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace Overlingo.App;

/// <summary>
/// Hlas nainstalovaný ve Windows (offline, zdarma, bez klíče). Nic nepřepíná samo: když pro jazyk překladu není hlas, vyhodí srozumitelnou chybu.
/// </summary>
public sealed class WindowsSpeechSynth : ISpeechSynth
{
    readonly string _voiceName, _lang;
    public WindowsSpeechSynth(string voiceName, string targetLang) { _voiceName = voiceName ?? ""; _lang = (targetLang ?? "cs").ToLowerInvariant(); }

    public static IReadOnlyList<VoiceInformation> Voices() => SpeechSynthesizer.AllVoices.ToList();
    public static string Label(VoiceInformation v) => $"{v.DisplayName} ({v.Language})";

    VoiceInformation? Pick()
    {
        var all = SpeechSynthesizer.AllVoices;
        if (_voiceName.Length > 0)
        {
            var named = all.FirstOrDefault(v => v.DisplayName.Equals(_voiceName, StringComparison.OrdinalIgnoreCase) || Label(v).Equals(_voiceName, StringComparison.OrdinalIgnoreCase));
            if (named != null) return named;
            throw new TranslationException(TranslationErrorKind.NotConfigured, $"Hlas Windows „{_voiceName}“ není nainstalovaný. Vyberte jiný v záložce Hlas.");
        }
        var byLang = all.FirstOrDefault(v => v.Language.StartsWith(_lang, StringComparison.OrdinalIgnoreCase));
        if (byLang == null)
            throw new TranslationException(TranslationErrorKind.NotConfigured,
                $"Windows nemá nainstalovaný hlas pro jazyk „{_lang}“. Nainstalujte ho v Nastavení Windows → Čas a jazyk → Řeč → Přidat hlasy.");
        return byLang;
    }

    /// <summary>Krátká zpráva o stavu pro tlačítko Test.</summary>
    public string Describe()
    {
        var v = Pick()!;
        return $"Windows hlas: {Label(v)}.";
    }

    public async Task<byte[]> SynthesizeAsync(string text, double rate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new TranslationException(TranslationErrorKind.BadResponse, "Hlas: prázdný text.");
        var voice = Pick()!;
        using var synth = new SpeechSynthesizer { Voice = voice };
        synth.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3.0);
        using var stream = await synth.SynthesizeTextToStreamAsync(text);
        ct.ThrowIfCancellationRequested();
        uint size = (uint)stream.Size;
        var bytes = new byte[size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync(size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
