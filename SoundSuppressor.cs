using System;
using Dalamud.Hooking;

namespace ActionCamera;

/// <summary>
/// Hooks UIGlobals.PlaySoundEffect — the game's UI sound-effect entry
/// point — so the soft-target "acquired" sound can be muted while the
/// action camera is active. That sound fires fire-and-forget at target-
/// commit time (which is why it survived the v0.6.16 reticle-pulse fix:
/// the +0x88 re-pin stops the visual animation but not this separate
/// sound call).
///
/// The exact effect id for the target-acquire sound isn't reliably
/// documented, so this also supports a discovery mode: while the debug
/// overlay is on and a left-click happened in the last ~150ms, every
/// played effect id is logged. Click on a soft-targeted enemy, read the
/// id that fires, and that becomes the muted id.
///
/// Suppression is scoped: only the configured id is dropped, and only
/// while the predicate (cam active) is true. Every other UI sound passes
/// through untouched.
/// </summary>
internal sealed unsafe class SoundSuppressor : IDisposable
{
    // UIGlobals.PlaySoundEffect(uint effectId, SoundData** pad, SoundData** data, byte a4).
    // We only care about the id; the pointer args pass through opaque.
    private delegate void PlaySoundEffectDelegate(uint effectId, nint a2, nint a3, byte a4);

    private readonly Hook<PlaySoundEffectDelegate>? hook;
    private readonly Func<bool> isCamActive;
    private readonly Func<uint> getMutedId;
    private readonly Func<bool> shouldLog;

    public SoundSuppressor(Func<bool> isCamActive, Func<uint> getMutedId, Func<bool> shouldLog)
    {
        this.isCamActive = isCamActive;
        this.getMutedId  = getMutedId;
        this.shouldLog   = shouldLog;

        try
        {
            // Call-site signature (resolves the CALL target). Same shape
            // FFXIVClientStructs uses for UIGlobals.PlaySoundEffect's
            // MemberFunction attribute.
            hook = Plugin.GameInterop.HookFromSignature<PlaySoundEffectDelegate>(
                "E8 ?? ?? ?? ?? 45 0F B7 C5", Detour);
            hook.Enable();
            Plugin.Log.Information("SoundSuppressor: PlaySoundEffect hook installed.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook PlaySoundEffect.");
        }
    }

    private void Detour(uint effectId, nint a2, nint a3, byte a4)
    {
        if (isCamActive())
        {
            // Discovery: log effect ids that fire right around a left-click
            // so the user can identify the target-acquire sound. Gated on
            // the debug overlay + a recent LMB so it isn't permanent spam.
            if (shouldLog() && InputBinding.WasLmbDownWithin(150))
                Plugin.Log.Information($"[Veiled] PlaySoundEffect id={effectId} (near LMB)");

            var muted = getMutedId();
            if (muted != 0 && effectId == muted)
                return; // drop only the configured id while cam active
        }

        hook!.Original(effectId, a2, a3, a4);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
