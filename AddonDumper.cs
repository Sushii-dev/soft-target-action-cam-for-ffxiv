using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Dump-the-world diagnostic for addon-callback debugging.
///
/// Lives behind <c>/actioncam dumpaddon</c>. When invoked, walks
/// <see cref="AtkUnitManager.AllLoadedUnitsList"/>, prints every <em>visible</em>
/// AtkUnitBase, and for each prints its name, focus status, AtkValue array,
/// and any AtkComponentButton it can find by id-scanning 1..63.
///
/// Built after burning v0.5.20.0 on a wrong-API bug (FireCallback was firing
/// the addon's dismiss path instead of clicking the Accept button on
/// JournalAccept). Next time an addon callback misbehaves: open the addon,
/// run this command, paste the log. Five minutes of diagnostics beats five
/// version bumps of guessing.
///
/// Output goes to Dalamud's PluginLog (full detail) and to game chat (the
/// short summary lines), so it can be triaged in-game and reproduced from
/// the log file later.
/// </summary>
internal static unsafe class AddonDumper
{
    public static void Dump()
    {
        var stage = AtkStage.Instance();
        if (stage == null)
        {
            Plugin.ChatGui.Print("[ActionCamera] dumpaddon: AtkStage unavailable.");
            return;
        }
        var unitManager = (AtkUnitManager*)stage->RaptureAtkUnitManager;
        if (unitManager == null)
        {
            Plugin.ChatGui.Print("[ActionCamera] dumpaddon: AtkUnitManager unavailable.");
            return;
        }

        var list = unitManager->AllLoadedUnitsList;
        var entryCount = list.Count;
        var focusedAddon = unitManager->FocusedAddon;

        var visibleCount = 0;

        for (var i = 0; i < entryCount; i++)
        {
            var addon = list.Entries[i].Value;
            if (addon == null) continue;
            if (!addon->IsVisible) continue;

            visibleCount++;

            var name = addon->NameString;
            var isFocused = addon == focusedAddon;
            var valueCount = addon->AtkValuesCount;

            // Per-addon header — also sent to chat so the user has a running
            // list of visible addons without scrolling through the log.
            Plugin.ChatGui.Print($"[ActionCamera] {name} (id={addon->Id}, vals={valueCount}{(isFocused ? ", FOCUSED" : "")})");

            var detail = new StringBuilder();
            detail.Append($"=== {name} ===\n");
            detail.Append($"  Id={addon->Id}  IsVisible={addon->IsVisible}  IsFocused={isFocused}\n");
            detail.Append($"  AtkValuesCount={valueCount}\n");

            // Cap at 16. SelectYesno puts the prompt in Values[0]; quest
            // dialogs stash item ids etc. in low indices. 16 is plenty for
            // identifying which dialog this is and what its callback expects.
            var dumpN = (int)valueCount > 16 ? 16 : (int)valueCount;
            if (addon->AtkValues != null)
            {
                for (var v = 0; v < dumpN; v++)
                {
                    var av = addon->AtkValues[v];
                    var formatted = av.GetValueAsString();
                    if (formatted.Length > 80) formatted = formatted[..80] + "…";
                    detail.Append($"  Values[{v}] {av.Type} = {formatted}\n");
                }
            }

            // Button-id scan. ECommons accesses JournalAccept's Accept via
            // GetComponentButtonById(44); other addons stash buttons behind
            // similarly arbitrary ids. Scan 1..63 — covers every confirmation
            // dialog seen in the reference plugins.
            var btnHeader = false;
            for (uint id = 1; id < 64; id++)
            {
                var btn = addon->GetComponentButtonById(id);
                if (btn == null) continue;
                if (!btnHeader) { detail.Append("  Buttons:\n"); btnHeader = true; }

                var label = TryReadButtonLabel(btn);
                detail.Append($"    id={id,3}  Enabled={btn->IsEnabled}  Label=\"{label}\"\n");
            }

            Plugin.Log.Info(detail.ToString());
        }

        Plugin.ChatGui.Print($"[ActionCamera] dumpaddon: {visibleCount} visible addon(s). Full detail in Dalamud log.");
        Plugin.Log.Info($"[ActionCamera] dumpaddon: enumerated {entryCount} loaded, {visibleCount} visible.");
    }

    private static string TryReadButtonLabel(AtkComponentButton* btn)
    {
        if (btn == null) return string.Empty;
        var textNode = btn->ButtonTextNode;
        if (textNode == null) return string.Empty;
        try
        {
            var ptr = textNode->NodeText.StringPtr.Value;
            if (ptr == null) return string.Empty;
            var span = new System.ReadOnlySpan<byte>(ptr, 64);
            var nul = span.IndexOf((byte)0);
            if (nul < 0) nul = span.Length;
            return System.Text.Encoding.UTF8.GetString(ptr, nul);
        }
        catch
        {
            return "<read-failed>";
        }
    }
}
