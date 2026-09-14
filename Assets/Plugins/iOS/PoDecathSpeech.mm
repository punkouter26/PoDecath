// The iPhone's own voice for the commentary. See SpeechSynth.IosVoice for the C# side.
//
// AVSpeechSynthesizer is part of AVFoundation, which every iOS app links already, so this adds nothing
// to the build but these lines. One synthesizer for the life of the app; a new utterance interrupts
// whatever is being said, because commentary is a slot and not a queue (SpeechSynth explains why).
//
// Rate: AVSpeechUtteranceDefaultSpeechRate is 0.5 on a 0..1 scale that is not linear in words per
// minute. The game's rate is a multiplier around 1.0, so it is applied to the default and clamped.

#import <AVFoundation/AVFoundation.h>

static AVSpeechSynthesizer *g_synth = nil;

extern "C" {

int PoDecathSpeech_Available(void)
{
    if (g_synth == nil) g_synth = [[AVSpeechSynthesizer alloc] init];
    return g_synth != nil ? 1 : 0;
}

void PoDecathSpeech_Speak(const char *text, float rate)
{
    if (text == NULL) return;
    if (g_synth == nil) g_synth = [[AVSpeechSynthesizer alloc] init];
    if (g_synth == nil) return;

    NSString *line = [NSString stringWithUTF8String:text];
    if (line == nil || line.length == 0) return;

    if ([g_synth isSpeaking]) [g_synth stopSpeakingAtBoundary:AVSpeechBoundaryImmediate];

    AVSpeechUtterance *u = [AVSpeechUtterance speechUtteranceWithString:line];
    float r = AVSpeechUtteranceDefaultSpeechRate * (rate > 0.0f ? rate : 1.0f);
    if (r < AVSpeechUtteranceMinimumSpeechRate) r = AVSpeechUtteranceMinimumSpeechRate;
    if (r > AVSpeechUtteranceMaximumSpeechRate) r = AVSpeechUtteranceMaximumSpeechRate;
    u.rate = r;
    // The phone's current language, so the voice matches the device rather than the developer.
    u.voice = [AVSpeechSynthesisVoice voiceWithLanguage:[AVSpeechSynthesisVoice currentLanguageCode]];
    [g_synth speakUtterance:u];
}

void PoDecathSpeech_Stop(void)
{
    if (g_synth != nil && [g_synth isSpeaking]) [g_synth stopSpeakingAtBoundary:AVSpeechBoundaryImmediate];
}

}
