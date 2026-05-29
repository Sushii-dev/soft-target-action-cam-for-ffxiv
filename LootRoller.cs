using System;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace ActionCamera;

/// <summary>
/// Rolls Need / Greed / Pass on the active loot-roll items so the user
/// can decide via a keybind instead of mousing to the loot window.
///
/// Drives the game's native <c>Loot.RollItemRaw(Loot*, option, index)</c>
/// (resolved by signature — the same call LazyLoot/PunishXIV use). Rolls
/// every currently-rollable item in one press ("greed everything"),
/// skipping items already rolled / unavailable, and — for Need —
/// skipping items the player can't need on (falls through silently).
///
/// Roll option values (game's RollResult enum): Need = 1, Greed = 2,
/// Pass = 5. Passed as raw uint so we don't depend on the CS enum name.
/// </summary>
internal sealed unsafe class LootRoller
{
    public const uint OptionNeed  = 1;
    public const uint OptionGreed = 2;
    public const uint OptionPass  = 5;

    private delegate byte RollItemRawDelegate(Loot* loot, uint option, uint index);
    private readonly RollItemRawDelegate? rollItemRaw;

    public LootRoller()
    {
        try
        {
            var addr = Plugin.SigScanner.ScanText("41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08");
            rollItemRaw = Marshal.GetDelegateForFunctionPointer<RollItemRawDelegate>(addr);
            Plugin.Log.Information($"LootRoller: RollItemRaw resolved at 0x{addr:X}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to resolve RollItemRaw — loot roll keys disabled.");
        }
    }

    /// <summary>
    /// Roll <paramref name="option"/> (Need/Greed/Pass) on every active,
    /// still-rollable loot item. No-ops silently when no loot window is up
    /// or the roll function couldn't be resolved.
    /// </summary>
    public void RollAll(uint option)
    {
        if (rollItemRaw == null) return;
        var loot = Loot.Instance();
        if (loot == null) return;

        // 16 = FixedSizeArray16<LootItem>. Numeric field comparisons used
        // (instead of CS enum member names) so this stays robust across
        // FFXIVClientStructs revisions:
        //   RollResult: UnAwarded=0 (still rollable)
        //   RollState:  UpToNeed=0, Rolled=17, Unavailable=21, Unknown=28
        //   LootMode:   Unavailable=2, LootMasterGreedOnly=3
        for (var i = 0; i < 16; i++)
        {
            ref var item = ref loot->Items[i];
            if (item.ItemId == 0) continue;             // empty slot
            if ((int)item.RollResult != 0) continue;    // already rolled / awarded

            var state = (int)item.RollState;
            if (state == 17 || state == 21 || state == 28) continue; // rolled / unavailable

            var mode = (int)item.LootMode;
            if (mode == 2 || mode == 3) continue;        // unavailable / lootmaster-greed-only

            // Can't Need on everything (job/restriction) — RollState 0 =
            // UpToNeed. If the user pressed Need but this item isn't
            // needable, skip it silently rather than erroring.
            if (option == OptionNeed && state != 0) continue;

            rollItemRaw(loot, option, (uint)i);
        }
    }
}
