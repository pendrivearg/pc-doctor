using System.Speech.Synthesis;

namespace PCDoctor.App;

/// <summary>Lectura en voz alta sin Internet, con las voces en español instaladas en Windows. Si no hay voz en español, el botón no se muestra.</summary>
internal static class Voice
{
    private static readonly Lazy<SpeechSynthesizer?> Synth = new(() =>
    {
        try
        {
            var s = new SpeechSynthesizer();
            var es = s.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "es");
            if (es is null) { s.Dispose(); return null; }
            s.SelectVoice(es.VoiceInfo.Name);
            s.Rate = -1; // un poco más lento: más fácil de seguir
            return s;
        }
        catch (Exception)
        {
            return null;
        }
    });

    public static bool Available => Synth.Value is not null;

    public static void Speak(string text)
    {
        var s = Synth.Value;
        if (s is null) return;
        s.SpeakAsyncCancelAll();
        s.SpeakAsync(text);
    }

    public static void Stop() => Synth.Value?.SpeakAsyncCancelAll();
}
