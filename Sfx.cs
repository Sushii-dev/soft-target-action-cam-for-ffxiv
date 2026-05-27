using FFXIVClientStructs.FFXIV.Client.UI;

namespace ActionCamera;

/// <summary>
/// Wrapper around <see cref="UIGlobals.PlaySoundEffect"/> with two semantic
/// helpers — success (first interaction completed) and failure (interact key
/// pressed but nothing was in range / nothing was advanceable).
///
/// Each helper short-circuits when its enable flag is off, so the call sites
/// don't have to repeat the same guard. Sound id is whatever the user picked
/// in the config — defaults at <see cref="Configuration"/>.
///
/// Why we don't play a success sound on dialogue advance: the game already
/// plays its own click sounds for advancing Talk / Yes-No / Select* prompts.
/// Doubling that with our own chime would feel laggy and overwhelming. The
/// success sound is reserved for the *initial* interaction — the moment a
/// new dialog opens or an examine window pops — where the game otherwise
/// gives no feedback.
/// </summary>
internal static class Sfx
{
    public static void PlaySuccess(Configuration config)
    {
        if (!config.PlayInteractSuccessSound) return;
        unsafe { UIGlobals.PlaySoundEffect(config.InteractSuccessSoundId); }
    }

    public static void PlayFail(Configuration config)
    {
        if (!config.PlayInteractFailSound) return;
        unsafe { UIGlobals.PlaySoundEffect(config.InteractFailSoundId); }
    }

    /// <summary>
    /// Audition path for the config UI's "Test" button. Plays the given id
    /// without consulting any enable flag — the user pressed the button on
    /// purpose, so we always make a sound.
    /// </summary>
    public static void PlayTest(uint soundId)
    {
        unsafe { UIGlobals.PlaySoundEffect(soundId); }
    }
}
