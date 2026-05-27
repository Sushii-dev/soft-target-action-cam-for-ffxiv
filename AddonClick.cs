using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ActionCamera;

/// <summary>
/// Simulate a real mouse click on an addon's component button.
///
/// The plugin previously used <c>addon->FireCallback(1, [int 0], true)</c> to
/// confirm button-based dialogs (SelectYesno, SelectOk, JournalAccept,
/// JournalResult, MaterializeDialog). That call invokes the addon's main
/// callback handler with arg 0 — for list-based addons (SelectString and
/// friends) that maps cleanly to "selected index 0", but for typed-button
/// dialogs it usually triggers the dialog's <em>dismiss</em> path rather
/// than the typed button's bound action. Symptom: pressing the interact
/// key over a JournalAccept just closes the prompt without accepting.
///
/// The correct path — used by ECommons' <c>ClickAddonButton</c>, GagSpeak,
/// Athavar Click, etc. — is to replay the button's own pre-registered
/// AtkEvent through the addon's <c>ReceiveEvent</c>. That's the same path
/// the game uses when the user actually clicks the button with the mouse,
/// so the typed handler (Accept / Yes / OK / Complete / ...) fires.
/// </summary>
internal static unsafe class AddonClick
{
    /// <summary>
    /// Replay the button's bound AtkEvent through the addon's ReceiveEvent.
    /// Returns false if the button is null, disabled, hidden, or has no
    /// registered events (which can happen if the addon hasn't finished
    /// initialising yet — caller should treat as "try again next tick").
    /// </summary>
    public static bool Click(AtkComponentButton* button, AtkUnitBase* addon)
    {
        if (button == null || addon == null) return false;

        // Refuse to click disabled buttons. Matches ECommons'
        // ClickButtonIfEnabled — if the addon disabled the button (eg
        // "you don't meet quest requirements"), we honour that rather
        // than force the action through.
        if (!button->IsEnabled) return false;

        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null) return false;

        var btnRes = &ownerNode->AtkResNode;
        if (!btnRes->IsVisible()) return false;

        var evt = btnRes->AtkEventManager.Event;
        if (evt == null) return false;

        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
        return true;
    }
}
