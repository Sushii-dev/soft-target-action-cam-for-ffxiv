using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;

namespace ActionCamera;

/// <summary>
/// Mutes the soft-target "acquired" sound while the action camera is
/// active. The sound is effect id 11 (confirmed via the interact
/// sound-tester), but the game does NOT emit it through
/// UIGlobals.PlaySoundEffect — runtime counters proved that hook stays
/// flat on the target-acquire event. It goes one layer deeper, through
/// SoundManager's InitSound funnel (the function VFXEditor hooks for
/// sound replacement — every game sound passes through it).
///
/// We hook InitSound, and while the predicate is true drop calls whose
/// sound index matches the configured id (returning a null SoundData*,
/// the same suppression VFXEditor uses). The resource path is logged in
/// discovery mode so the match can be tightened to a specific path if
/// matching on index alone proves too broad.
/// </summary>
internal sealed unsafe class SoundSuppressor : IDisposable
{
    // SoundManager::InitSound — the sound-instantiation funnel.
    //   SoundData* InitSound(SoundManager* mgr, char* path, float volume,
    //                        uint soundIdx, uint unk1, bool unk2,
    //                        SoundVolumeCategory category)
    // Raw types (nint / byte* / primitives) used so we don't need the
    // FFXIVClientStructs SoundData / SoundVolumeCategory types; the ABI
    // matches (pointer-sized return + args, bool→byte, enum→uint).
    private delegate nint InitSoundDelegate(
        nint manager, byte* path, float volume,
        uint soundIdx, uint unk1, byte unk2, uint category);

    private readonly Hook<InitSoundDelegate>? hook;
    private readonly Func<bool> isCamActive;
    private readonly Func<uint, bool> isMutedId;
    private readonly Func<bool> shouldLog;

    public long CallCount       { get; private set; }
    public uint LastId          { get; private set; }
    public uint LastIdInCam     { get; private set; }
    public long SuppressedCount { get; private set; }

    public SoundSuppressor(Func<bool> isCamActive, Func<uint, bool> isMutedId, Func<bool> shouldLog)
    {
        this.isCamActive = isCamActive;
        this.isMutedId   = isMutedId;
        this.shouldLog   = shouldLog;

        try
        {
            // VFXEditor's InitSound signature — every sound funnels here.
            hook = Plugin.GameInterop.HookFromSignature<InitSoundDelegate>(
                "E8 ?? ?? ?? ?? 8B 5D 77", Detour);
            hook.Enable();
            Plugin.Log.Information("SoundSuppressor: InitSound hook installed.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook InitSound.");
        }
    }

    private nint Detour(nint manager, byte* path, float volume,
        uint soundIdx, uint unk1, byte unk2, uint category)
    {
        CallCount++;
        LastId = soundIdx;

        if (isCamActive())
        {
            LastIdInCam = soundIdx;

            if (shouldLog())
            {
                var p = path != null ? Marshal.PtrToStringAnsi((nint)path) ?? "" : "";
                Plugin.Log.Information($"[Veiled] InitSound idx={soundIdx} cat={category} path={p}");
            }

            if (isMutedId(soundIdx))
            {
                SuppressedCount++;
                return nint.Zero; // suppress: no SoundData created, no playback
            }
        }

        return hook!.Original(manager, path, volume, soundIdx, unk1, unk2, category);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
