using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ActionCamera.Windows;

namespace ActionCamera;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGamepadState GamepadState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/veiled";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("ActionCamera");
    private ConfigWindow ConfigWindow { get; init; }

    private readonly CameraController cameraController;
    private readonly TargetSelector targetSelector;
    private readonly ReticleOverlay reticleOverlay;
    private readonly HardTargetSuppressor hardTargetSuppressor;
    private readonly RotationDriver rotationDriver;
    private readonly InteractHandler interactHandler;
    private readonly InteractIndicator interactIndicator;
    private readonly CursorShowHook cursorShowHook;
    private readonly CursorUpdateHook cursorUpdateHook;
    private readonly DebugOverlay debugOverlay;
    private readonly MouseBindController mouseBindController;
    private readonly MouseOverSuppressor mouseOverSuppressor;
    private readonly SoftTargetSuppressor softTargetSuppressor;
    private readonly InputStatusSuppressor inputStatusSuppressor;
    private readonly SoftTargetGuard softTargetGuard;
    private readonly SoundSuppressor soundSuppressor;
    private readonly LootRoller lootRoller;

    // True when the user explicitly engaged the cam via the activation key.
    // This is intent only — actual cam state mirrors cursor visibility (so
    // RMB-hold etc. also activate the cam). userWantsActive is consulted for
    // sticky-off semantics (popup → cursor shown → reset intent unless an
    // auto-resume exemption applies) and for auto-resume bookkeeping.
    private bool userWantsActive;
    private bool toggleKeyWasDown;
    private bool clearTargetKeyWasDown;
    private bool hardTargetKeyWasDown;
    private bool interactKeyWasDown;
    private bool needRollKeyWasDown;
    private bool greedRollKeyWasDown;
    private bool passRollKeyWasDown;
    private bool wasExemptedLastTick;
    private bool gatheringUiWasOpen;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.MigrateIfNeeded();

        targetSelector       = new TargetSelector(Configuration);
        cameraController     = new CameraController(Configuration, targetSelector);
        reticleOverlay       = new ReticleOverlay(cameraController, Configuration);
        hardTargetSuppressor = new HardTargetSuppressor(Configuration, targetSelector, () => cameraController.IsActive);
        rotationDriver       = new RotationDriver(Configuration, () => cameraController.IsActive, cameraController.GetCameraHRotation);
        interactHandler      = new InteractHandler(Configuration, cameraController.GetCameraHRotation);
        // Indicator shares the handler's cone scan so what gets drawn matches
        // what the interact key will fire against. No double scanning.
        interactIndicator    = new InteractIndicator(Configuration, interactHandler.GetIndicatorCandidate);
        // Hook AtkCursor.Show to suppress the game's per-tick re-assert while
        // cam is active. Predicate: we only block Show calls that would
        // re-show the cursor against the user's intent — RMB-held gestures
        // and any open menu / cutscene / popup pass through so legitimate
        // cursor returns still work. See CursorShowHook for full rationale.
        // Same gate is shared by both cursor hooks. Lambda is captured by
        // reference so it reads the live userWantsActive each tick.
        Func<bool> shouldSuppress = () =>
            userWantsActive
            && !IsVanillaRmbCameraGesture()
            && !IsMenuOpen();

        cursorShowHook = new CursorShowHook(shouldSuppress);

        // Hook AtkUnitManager::UpdateCursor — the per-frame routine that
        // re-asserts Cursor.IsCursorVisible (the byte the SW-cursor render
        // actually reads). This is the path the Show hook can't reach and
        // is the root-cause target for the scroll-zoom flicker.
        cursorUpdateHook = new CursorUpdateHook(shouldSuppress);

        // Hook TargetSystem::GetMouseOverObject and return null while the
        // action camera is active. With cursor hidden + center-locked,
        // every LMB / RMB click ray-hits whatever entity is at the
        // crosshair — game's input-pipeline release-handler runs the
        // click-target acquisition chain off that ray-pick result and
        // queues the "target acquired" animation + sound (the user
        // playtest jank since v0.6.x). Returning null upstream of the
        // chain kills the whole acquisition path: no sound, no reticle
        // re-attach animation, no SetSoftTarget call, no SetHardTarget
        // call. Same pattern SimpleTweaks' DisableClickTargeting uses.
        //
        // Predicate uses userWantsActive (the user's stable intent flag)
        // rather than cameraController.IsActive + cursor-visible check.
        // v0.6.7 diagnostic counters showed pass-throughs spiking during
        // clicks — the cursor-visible check was leaking because the
        // game's click handler flips AtkCursor.IsVisible transiently
        // via a path the cursor-show hook doesn't catch. userWantsActive
        // only flips on activation-key toggle or sticky-off (cursor
        // visible + menu open), not on transient pulses, so the
        // predicate stays true through the entire click.
        mouseOverSuppressor = new MouseOverSuppressor(() => userWantsActive);

        // Pair to MouseOverSuppressor. v0.6.8 closed the GetMouseOverObject
        // path but the game's click handler ALSO reads the cached
        // ts->MouseOverTarget FIELD directly (which TargetSelector keeps
        // populated each frame as part of the cone-targeting feature) and
        // calls SetSoftTarget off that. The setter itself fires the
        // "target acquired" animation + SFX — plugin's direct-field
        // writes don't trigger this hook because they don't call the
        // function. Blocking the function rejects only game-side
        // acquisition calls; plugin's cone-driven SoftTarget writes
        // continue uninterrupted.
        softTargetSuppressor = new SoftTargetSuppressor(() => userWantsActive);

        // Third leg of the SimpleTweaks DisableClickTargeting pattern:
        // game's targeting layer reads click state through
        // InputManager::GetInputStatus. v0.6.9 counters showed
        // SetSoftTarget never fires during clicks — meaning the click
        // path doesn't even reach the setter. Returning 0 from
        // GetInputStatus for LMB/RMB codes tells the targeting layer
        // "button never pressed", so the whole acquisition chain
        // (animation, SFX, soft-target write) is skipped at the source.
        //
        // Plugin's own button reads via InputBinding use Win32
        // GetAsyncKeyState directly — bypass InputManager — so the
        // plugin's bind firing is unaffected.
        inputStatusSuppressor = new InputStatusSuppressor(() => userWantsActive);

        // Enforce SoftTarget == cone pick late each frame (in
        // HandleTargetingKeybinds, after input, before the reticle read)
        // so any same-frame clear — Escape's keybind clear or the LMB
        // mouse-commit's inlined clear — is repaired before the reticle
        // can animate a null→entity edge. See SoftTargetGuard. Gated on
        // the plugin actually owning the soft target (WriteSoftTarget on).
        softTargetGuard = new SoftTargetGuard(
            () => userWantsActive && Configuration.WriteSoftTarget,
            () => targetSelector.CachedBestAddress);

        // Mute the soft-target acquire sound while cam active. Created
        // before debugOverlay so the overlay can show its live counters;
        // the shouldLog lambda reads debugOverlay.Enabled at call-time
        // (the field is assigned just below, before any hook fires).
        // Muted id is 0 (off) until confirmed via the debug sound log;
        // flip MutedSoftTargetSoundId once known.
        soundSuppressor = new SoundSuppressor(
            () => userWantsActive && Configuration.MuteSoftTargetSoundInCam,
            id => Configuration.MutedSoftTargetSoundIds != null
                  && Configuration.MutedSoftTargetSoundIds.Contains(id),
            () => debugOverlay != null && debugOverlay.Enabled);

        lootRoller = new LootRoller();

        debugOverlay = new DebugOverlay(
            cursorUpdateHook,
            mouseOverSuppressor,
            softTargetSuppressor,
            inputStatusSuppressor,
            soundSuppressor,
            () => userWantsActive,
            () => cameraController.IsActive,
            IsMenuOpen,
            CameraController.IsRmbHeld);

        // BETA: mouse-button → hotbar fire. Hard-gated to cam-active +
        // cursor-hidden so vanilla mouse semantics stay untouched outside
        // the action mode. Toggle defaults to OFF; existing users see
        // zero behaviour change unless they opt in via the config UI.
        mouseBindController = new MouseBindController(
            Configuration,
            () => cameraController.IsActive,
            CameraController.IsGameCursorVisible,
            IsMenuOpen);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Veiled Aim: toggle action camera. Args: on | off | config | cleartarget | debug | dumpaddon | testfire <bar> <slot>"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenConfigUi;
        Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfigUi;
        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        hardTargetSuppressor.Dispose();
        rotationDriver.Dispose();
        // Dispose the cursor hooks BEFORE the controller so the final Show
        // call in CameraController.Dispose's RequestShowCursor isn't NOP'd
        // and UpdateCursor runs again on the way out (so the cursor flips
        // back to its natural state once the plugin is gone).
        cursorShowHook.Dispose();
        cursorUpdateHook.Dispose();
        mouseOverSuppressor.Dispose();
        softTargetSuppressor.Dispose();
        inputStatusSuppressor.Dispose();
        softTargetGuard.Dispose();
        soundSuppressor.Dispose();
        cameraController.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
        {
            if (cameraController.IsActive) cameraController.Deactivate();
            if (userWantsActive)
            {
                cameraController.RequestShowCursor();
                userWantsActive = false;
            }
            return;
        }

        // Refresh InputBinding's LMB activity timestamp. Used by the click-
        // target suppressor to cover FFXIV's deferred SetHardTarget pipeline
        // (the swap-existing-hard-target path fires the call on LMB-up, by
        // which point a point-in-time check would miss the click signal).
        InputBinding.Tick();

        // menuOpen here is only used to gate key handlers — so e.g. CTRL pressed
        // while typing in chat doesn't toggle the cam. It is NOT used to drive
        // cam state any more; cursor visibility (via AtkCursor.IsVisible) is
        // the source of truth for active/inactive.
        var menuOpen = IsMenuOpen();

        HandleToggleKey(menuOpen);
        HandleClearTargetKey(menuOpen);
        HandleHardTargetKey(menuOpen);
        HandleInteractKey();
        HandleLootRollKeys();
        ReconcileCursorSync();
        HandleGatheringCursor();
        cameraController.Update();
        // RotationDriver runs after camera so it reads the freshly-updated yaw.
        rotationDriver.Update();
        // Mouse-bind controller is intentionally last in the tick — it
        // depends on the camera's IsActive state having been reconciled
        // for this frame, so the gate stack sees the same view of "are we
        // in cursor-locked mode" that the rest of the systems do.
        mouseBindController.Update();

        // v0.6.12: keep MouseOverNameplateTarget (+0xE0) clear while cam
        // intent is on. Decomp research traced the LMB-release soft-target
        // reseat (the acquire ring pulse + sound) to the click-confirm
        // handler reading this field directly and inlining a SoftTarget
        // write — bypassing SetSoftTarget (counter 0) and the
        // GetMouseOverObject / GetInputStatus hooks entirely. With the
        // cursor hidden + center-locked, nameplate-mouseover is
        // meaningless, so zeroing the field every frame denies the
        // handler any target to reseat without affecting anything the
        // user can see or use.
        if (userWantsActive)
            ClearNameplateMouseover();
    }

    private unsafe void ClearNameplateMouseover()
    {
        var ts = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (ts == null) return;

        // v0.6.13: the pulse only fires when the camera is STILL — user
        // confirmed constant camera motion suppresses it entirely. That
        // pinpoints the game's idle-cursor hover detection as the source:
        // when the (hidden, center-locked) cursor sits idle over an enemy,
        // the game's hover system writes MouseOverTarget (+0xD0) itself,
        // and the LMB-release click handler promotes that cached field to
        // SoftTarget with the acquire cue. Our GetMouseOverObject hook
        // (a different raycast entry) doesn't stop the game's own hover
        // write, so we clear the cached field directly each frame.
        //
        // Skip nulling +0xD0 when the user has WriteMouseOverTarget on
        // AND there's a cone pick — that path legitimately wants the
        // field populated for ReAction's Field Target pronoun. In that
        // case TargetSelector owns the field; the reseat would target the
        // cone pick anyway, which is acceptable.
        var keepMouseOver = Configuration.WriteMouseOverTarget
                            && targetSelector.CachedBest != null;
        if (!keepMouseOver && ts->MouseOverTarget != null)
            ts->MouseOverTarget = null;

        if (ts->MouseOverNameplateTarget != null)
            ts->MouseOverNameplateTarget = null;
    }

    /// <summary>
    /// Reconciliation between the user's cam intent, cursor visibility, RMB
    /// state, and the various game systems that can independently re-assert
    /// AtkCursor.IsVisible.
    ///
    /// The big lesson from real-world testing: writing to MouseOverTarget (so
    /// the cone-pick gets the yellow outline + ReAction's Field Target
    /// pronoun) causes FFXIV's UI module to re-assert AtkCursor.IsVisible on
    /// the next tick — the game treats "user is hovering an interactable" as
    /// "show the cursor". One Hide() per CTRL press wasn't enough; the game
    /// would flip IsVisible back true on the very next tick and our
    /// sticky-off would tear the cam back down.
    ///
    /// So sticky-off is no longer triggered by raw cursor-visible. It only
    /// fires when cursor visible + a tracked menu/popup/cutscene condition
    /// is open (IsMenuOpen). When the cursor's visible but no menu is open,
    /// we re-Hide each tick to fight the game's re-assert. This runs before
    /// the game renders, so there's no flicker.
    ///
    /// RMB-hold short-circuits the re-Hide branch — RMB legitimately wants
    /// the cursor's visibility state to be whatever the game says, and we
    /// don't want to fight it during the gesture. shouldBeActive forces
    /// the cam on regardless of cursor flags while RMB is held.
    /// </summary>
    private void ReconcileCursorSync()
    {
        var cursorVisible = CameraController.IsGameCursorVisible();
        var rbHeld = CameraController.IsRmbHeld();
        var exempted = IsCurrentlyExempted();

        // Intent maintenance: only when we have an explicit intent AND the
        // cursor is currently visible AND RMB isn't held (RMB is allowed to
        // own cursor state during the gesture).
        if (userWantsActive && cursorVisible && !rbHeld)
        {
            if (IsMenuOpen())
            {
                // Genuine UI / cutscene / popup. The cursor-visible event is
                // user-initiated or game-mandated; treat as sticky-off (or
                // defer / auto-resume based on the exemption checkboxes).
                if (exempted)
                {
                    // Defer — cam off this tick, intent preserved.
                }
                else if (wasExemptedLastTick)
                {
                    // Exemption just cleared — re-Hide to auto-resume.
                    cameraController.RequestHideCursor();
                }
                else
                {
                    // Sticky-off — fresh activation key press required.
                    userWantsActive = false;
                }
            }
            else
            {
                // Cursor visible but no UI / menu is open. The game's UI
                // module is re-asserting visibility on its own (typically
                // because our MouseOverTarget write makes it think the user
                // is hovering an interactable). Re-Hide to keep the cam
                // session alive — user intent is unchanged.
                cameraController.RequestHideCursor();
            }
        }
        wasExemptedLastTick = exempted;

        // Re-read cursor visibility after the potential re-Hide above.
        cursorVisible = CameraController.IsGameCursorVisible();

        // rbHeld force-activates the cam so a vanilla RMB-drag can rotate
        // even when the cam is toggled off (BDO-style always-available
        // look). But when RMB is BOUND to a skill it is NOT a camera
        // gesture, so holding it must NOT activate the cam — otherwise a
        // right-CLICK (to open a target's context menu) briefly activates
        // the cam, hides the cursor, and the menu never opens. Gate the
        // rbHeld clause on RMB being unbound.
        var shouldBeActive = !cursorVisible || (rbHeld && !IsRmbBoundToFire());

        if (shouldBeActive && !cameraController.IsActive)
            cameraController.Activate();
        else if (!shouldBeActive && cameraController.IsActive)
            cameraController.Deactivate();
    }

    /// <summary>
    /// True iff one of the user-checked auto-resume exemption conditions is
    /// currently active. Drives the "defer" branch of ReconcileCursorSync —
    /// while exempted, cursor-visible doesn't reset userWantsActive, so the
    /// cam auto-resumes when the cursor hides again.
    /// </summary>
    private unsafe bool IsCurrentlyExempted()
    {
        if (Configuration.AutoResumeAfterCutscene
            && (Condition[ConditionFlag.OccupiedInCutSceneEvent]
                || Condition[ConditionFlag.WatchingCutscene78]))
            return true;

        if (Configuration.AutoResumeAfterEvent
            && (Condition[ConditionFlag.OccupiedInEvent]
                || Condition[ConditionFlag.OccupiedInQuestEvent]))
            return true;

        if (Configuration.AutoResumeAfterZoneTransition
            && Condition[ConditionFlag.BetweenAreas])
            return true;

        // Gathering window: always defer the cam (independent of AutoResumeAfterUI)
        // so it auto-resumes the instant the gathering UI closes.
        if (IsGatheringUiOpen())
            return true;

        if (Configuration.AutoResumeAfterUI)
        {
            var stage = AtkStage.Instance();
            if (stage != null)
            {
                var unitManager = (AtkUnitManager*)stage->RaptureAtkUnitManager;
                if (unitManager != null && unitManager->FocusedAddon != null)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when RMB is held as a VANILLA camera-drag gesture — held but
    /// NOT bound to a mouse-bind. The cursor-suppression carve-outs exist
    /// so that vanilla RMB-drag camera gets the cursor's natural state;
    /// but when RMB is bound (beta binds on), a held RMB is a hotbar-fire
    /// gesture, not a camera gesture. If the carve-out fired for bound
    /// RMB, the cursor would un-hide mid-hold, MouseBindController's
    /// cursor-visible gate would bail, and RMB hold-repeat would break
    /// (LMB was unaffected because it was never in the carve-out). So the
    /// carve-out must apply only to the vanilla (unbound) RMB gesture.
    /// </summary>
    private bool IsVanillaRmbCameraGesture()
        => CameraController.IsRmbHeld() && !IsRmbBoundToFire();

    private bool IsRmbBoundToFire()
    {
        if (!Configuration.BetaMouseBindsEnabled) return false;
        foreach (var b in Configuration.MouseBinds)
            if (b != null && b.Button == Dalamud.Game.ClientState.Keys.VirtualKey.RBUTTON)
                return true;
        return false;
    }

    private void HandleHardTargetKey(bool menuOpen)
    {
        if (Configuration.HardTargetKey == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY) return;

        // Track edge state every frame, even while gated by menuOpen / IsActive,
        // so that holding the key across a transition does not fire a phantom
        // press on the first ungated frame.
        var isDown = InputBinding.IsDown(Configuration.HardTargetKey);
        var rising = isDown && !hardTargetKeyWasDown;
        hardTargetKeyWasDown = isDown;

        if (!rising) return;
        if (menuOpen) return;

        // Toggle mode: if the flag is on AND a hard target already exists,
        // this press clears it instead of re-targeting. Clear branch is not
        // gated on IsActive — clearing is meaningful outside camera mode too.
        if (Configuration.HardTargetKeyClearsOnPress && TargetManager.Target != null)
        {
            ClearHardTarget();
            return;
        }

        // Set branch: cachedBest is only fresh while the camera is active.
        if (!cameraController.IsActive) return;

        var pick = targetSelector.CachedBest;
        if (pick == null) return;

        // Bypass the suppression hook for this single SetHardTarget call.
        // try/finally guarantees the bypass flag is cleared even if the Dalamud
        // setter ever short-circuits without invoking the native function.
        hardTargetSuppressor.AllowNext();
        try     { TargetManager.Target = pick; }
        finally { hardTargetSuppressor.CancelAllow(); }
    }

    private void HandleInteractKey()
    {
        if (Configuration.InteractKey == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY) return;

        // Deliberately NOT gated on menuOpen — that gate would block dialogue
        // advance, which is exactly when an addon (== focused/visible UI) is
        // up. The handler is internally responsible for distinguishing
        // dialogue-advance from world-interact and only firing in scenarios
        // where it knows what to do.
        var isDown = InputBinding.IsDown(Configuration.InteractKey);
        var rising = isDown && !interactKeyWasDown;
        interactKeyWasDown = isDown;
        if (!rising) return;

        // Never fire while the user is typing. InputBinding.IsDown already
        // gates ImGui text fields (WantTextInput) for Dalamud / Penumbra
        // UI; this adds the GAME-side gate — chat box, market-board /
        // search comment, letter writing, /tell, rename dialogs, etc. —
        // via RaptureAtkModule's own "is a text widget focused" check.
        // Dialogue advance is unaffected: a Talk / SelectYesno addon does
        // NOT register as text input, so IsTextInputActive stays false
        // there and the interact key still advances dialogue.
        if (IsGameTextInputActive()) return;

        // Audio feedback differs by outcome:
        //  - AdvancedDialogue: silent (game plays its own click sounds).
        //  - Interacted / Examined: success chime — the moment a fresh
        //    target is engaged is otherwise audio-feedback-less.
        //  - NothingFound: subtle fail tick so the player knows the key
        //    registered but didn't find anything.
        var result = interactHandler.TryInteract();
        switch (result)
        {
            case InteractResult.InteractedWithTarget:
            case InteractResult.ExaminedPlayer:
            case InteractResult.RodePillion:
                Sfx.PlaySuccess(Configuration);
                break;
            case InteractResult.NothingFound:
                Sfx.PlayFail(Configuration);
                break;
            // AdvancedDialogue intentionally falls through silent.
        }
    }

    /// <summary>
    /// Need / Greed / Pass quick-roll keys. Edge-triggered; work in any
    /// camera state (the loot window is independent of the camera), but
    /// never while typing. LootRoller.RollAll no-ops when no loot window
    /// is up, so a stray press does nothing.
    /// </summary>
    private void HandleLootRollKeys()
    {
        RollKey(Configuration.NeedRollKey,  ref needRollKeyWasDown,  LootRoller.OptionNeed);
        RollKey(Configuration.GreedRollKey, ref greedRollKeyWasDown, LootRoller.OptionGreed);
        RollKey(Configuration.PassRollKey,  ref passRollKeyWasDown,  LootRoller.OptionPass);
    }

    private void RollKey(Dalamud.Game.ClientState.Keys.VirtualKey key, ref bool wasDown, uint option)
    {
        if (key == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY)
        {
            wasDown = false;
            return;
        }
        var isDown = InputBinding.IsDown(key);
        var rising = isDown && !wasDown;
        wasDown = isDown;
        if (!rising) return;
        if (IsGameTextInputActive()) return;
        lootRoller.RollAll(option);
    }

    /// <summary>
    /// True while any FFXIV-native text widget holds input focus (chat,
    /// market-board search, search comment, letters, /tell, rename, etc.).
    /// The game's own keybind dispatch uses this same check to stop binds
    /// firing while you type. Complements ImGui's WantTextInput (which
    /// covers Dalamud / ImGui text fields) for full typing coverage.
    /// </summary>
    private static unsafe bool IsGameTextInputActive()
    {
        var ram = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkModule.Instance();
        return ram != null && ram->AtkModule.IsTextInputActive();
    }

    private void HandleClearTargetKey(bool menuOpen)
    {
        if (Configuration.ClearHardTargetKey == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY) return;

        // Edge state always tracked so toggling the "key clears on press" flag
        // mid-press doesn't fire a phantom clear when this handler re-enables.
        var isDown = InputBinding.IsDown(Configuration.ClearHardTargetKey);
        var rising = isDown && !clearTargetKeyWasDown;
        clearTargetKeyWasDown = isDown;

        // Skip the action entirely when the toggle flag owns clearing. Prevents
        // a previously-saved binding from double-firing with HardTargetKey and
        // turning a clear into a target-swap.
        if (Configuration.HardTargetKeyClearsOnPress) return;

        if (rising && !menuOpen)
            ClearHardTarget();
    }

    private static void ClearHardTarget() => TargetManager.Target = null;

    private static unsafe bool IsMenuOpen()
    {
        // Scripted events, cutscenes, loading screens.
        if (Condition[ConditionFlag.OccupiedInEvent]
            || Condition[ConditionFlag.OccupiedInQuestEvent]
            || Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Condition[ConditionFlag.WatchingCutscene78]
            || Condition[ConditionFlag.BetweenAreas])
            return true;

        // The gathering choice window doesn't set FocusedAddon, so it slips
        // past the check below — detect it explicitly so the cursor shows
        // for the (clickable) gathering UI instead of being re-hidden.
        if (IsGatheringUiOpen()) return true;

        // Any interactive window (inventory, character sheet, map, shop, etc.)
        // sets FocusedAddon when open. Null means only the game world is active.
        var stage = AtkStage.Instance();
        if (stage == null) return false;
        var unitManager = (AtkUnitManager*)stage->RaptureAtkUnitManager;
        return unitManager != null && unitManager->FocusedAddon != null;
    }

    /// <summary>
    /// True while a gathering interaction UI is up (the node item-pick
    /// window, or the collectable masterpiece window). Drives both the
    /// cursor-show (treat as menu) and the auto-resume exemption so the cam
    /// comes back automatically once gathering finishes.
    /// </summary>
    private static unsafe bool IsGatheringUiOpen()
    {
        if (Condition[ConditionFlag.Gathering] || Condition[ConditionFlag.Gathering42])
            return true;
        return IsAddonVisible("Gathering") || IsAddonVisible("GatheringMasterpiece");
    }

    private static unsafe bool IsAddonVisible(string name)
    {
        var addon = GameGui.GetAddonByName(name, 1);
        return addon.Address != 0 && ((AtkUnitBase*)addon.Address)->IsVisible;
    }

    /// <summary>
    /// On the frame the gathering UI opens, force the cursor visible and warp
    /// it onto the centre of the gathering window so the user can immediately
    /// click the item choices. The cam tears down on its own (ReconcileCursorSync
    /// sees the cursor visible) and auto-resumes once the window closes
    /// (gathering is an IsCurrentlyExempted condition). Edge-triggered so we
    /// don't fight the user's cursor while the window stays open.
    /// </summary>
    private unsafe void HandleGatheringCursor()
    {
        var open = IsGatheringUiOpen();
        if (open && !gatheringUiWasOpen)
        {
            cameraController.RequestShowCursor();
            if (TryGetGatheringAddonCenter(out var cx, out var cy))
                cameraController.WarpCursorToClient(cx, cy);
        }
        gatheringUiWasOpen = open;
    }

    /// <summary>Centre of the visible gathering addon in client px.</summary>
    private static unsafe bool TryGetGatheringAddonCenter(out int cx, out int cy)
    {
        cx = cy = 0;
        foreach (var name in new[] { "Gathering", "GatheringMasterpiece" })
        {
            var wrapper = GameGui.GetAddonByName(name, 1);
            if (wrapper.Address == 0) continue;
            var addon = (AtkUnitBase*)wrapper.Address;
            if (!addon->IsVisible) continue;
            var root = addon->RootNode;
            if (root == null) continue;
            cx = (int)(addon->X + root->Width * addon->Scale / 2f);
            cy = (int)(addon->Y + root->Height * addon->Scale / 2f);
            return true;
        }
        return false;
    }

    private void HandleToggleKey(bool menuOpen)
    {
        if (Configuration.ActivationKey == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY) return;

        // No menuOpen gate. The activation key must work in EVERY scenario —
        // including inventory / character / map / skill tree, all of which
        // set FocusedAddon and would otherwise be blocked by IsMenuOpen().
        // The user needs to be able to free the cursor at any time.
        //
        // Trade-off: a chord like Ctrl+V while typing in chat will fire this
        // toggle. Pressing the key again reverts. ImGui text fields are
        // still protected by InputBinding.IsDown's WantTextInput check.
        var isDown = InputBinding.IsDown(Configuration.ActivationKey);
        var rising = isDown && !toggleKeyWasDown;
        toggleKeyWasDown = isDown;
        if (!rising) return;

        userWantsActive = !userWantsActive;
        if (userWantsActive)
            cameraController.RequestHideCursor();
        else
            cameraController.RequestShowCursor();
    }

    private void OnCommand(string command, string args)
    {
        // Tokenise so multi-word subcommands like "testfire 0 5" can be
        // dispatched. Subcommand match is on the first token only;
        // remaining tokens are forwarded to the per-subcommand handler.
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        switch (sub)
        {
            case "on":
                userWantsActive = true;
                ChatGui.Print("[Veiled Aim] Activated.");
                break;
            case "off":
                userWantsActive = false;
                ChatGui.Print("[Veiled Aim] Deactivated.");
                break;
            case "config":
                ConfigWindow.IsOpen = true;
                break;
            case "cleartarget":
                ClearHardTarget();
                break;
            case "debug":
                debugOverlay.Enabled = !debugOverlay.Enabled;
                ChatGui.Print($"[Veiled Aim] Cursor debug overlay: {(debugOverlay.Enabled ? "ON" : "OFF")}");
                break;
            case "dumpaddon":
                // One-shot snapshot of every visible AtkUnitBase — addon name,
                // AtkValue array, and button-id scan. Used to research which
                // callback shape a misbehaving dialog expects. See AddonDumper.
                AddonDumper.Dump();
                break;
            case "testfire":
                // Diagnostic: fire a hotbar slot directly, bypassing the
                // mouse-bind controller's gates. Validates that
                // RaptureHotbarModule.ExecuteSlotById works from the
                // framework thread before the user wires up bindings.
                HandleTestFire(parts);
                break;
            default:
                userWantsActive = !userWantsActive;
                ChatGui.Print(userWantsActive ? "[Veiled Aim] Activated." : "[Veiled Aim] Deactivated.");
                break;
        }
    }

    /// <summary>
    /// Diagnostic command implementation: <c>/actioncam testfire &lt;bar&gt; &lt;slot&gt;</c>.
    /// Bar id is 0..17 (0..9 standard hotbars, 10..17 cross). Slot id is 0..15.
    /// The visible "Hotbar 1" in-game corresponds to bar id 0.
    /// </summary>
    private void HandleTestFire(string[] parts)
    {
        if (parts.Length < 3)
        {
            ChatGui.Print("[Veiled Aim] Usage: /veiled testfire <bar 0-17> <slot 0-15>");
            return;
        }

        if (!uint.TryParse(parts[1], out var bar) || !uint.TryParse(parts[2], out var slot))
        {
            ChatGui.Print("[Veiled Aim] testfire: bar and slot must be non-negative integers.");
            return;
        }

        HotbarFirer.TryGetSlotPreview(bar, slot, out var label, out _);
        var fired = HotbarFirer.Fire(bar, slot);
        ChatGui.Print($"[Veiled Aim] testfire bar={bar} slot={slot} -> {label}: {(fired ? "fired" : "skipped")}");
    }

    private void DrawUI()
    {
        // Render-time re-Hide. Scroll-wheel events (and other input "user is
        // active" signals) write AtkCursor.IsVisible = true via a path that
        // neither the Show hook nor UpdateCursor hook catch — the byte
        // briefly flips true mid-tick between our Framework.Update
        // re-Hide calls, the renderer reads true, draws the cursor sprite
        // for one frame. UiBuilder.Draw fires every render frame at the
        // very moment the game is about to draw its UI layer, so calling
        // Hide() here closes that window. Uses the existing
        // RequestHideCursor() path (which calls AtkCursor.Hide() internally
        // — never writes the field directly, that caused 0.5.16.0's cam
        // breakage).
        if (userWantsActive && !IsVanillaRmbCameraGesture() && !IsMenuOpen())
        {
            cameraController.RequestHideCursor();
        }

        // v0.6.12: render-time mirror of the framework-update nameplate
        // clear. The click-confirm handler can run on an input event
        // between Framework.Update and the render pass, so re-zero +0xE0
        // here too — same belt-and-suspenders pattern the cursor re-Hide
        // above uses for the same timing reason.
        if (userWantsActive)
            ClearNameplateMouseover();

        // BETA mouse-binds (v0.6.4): render-time SoftTarget restore.
        //
        // Game clears SoftTarget at multiple input-pipeline points the
        // plugin can't intercept from Framework.Update alone — UseAction's
        // "consumed the target" bookkeeping (v0.6.3 patched this for the
        // rising-edge frame) AND the end-of-click handler on LMB / RMB
        // release. The release case fires AFTER MouseBindController's
        // edge-detect, so a per-tick post-fire restore misses it.
        //
        // UiBuilder.Draw runs every render frame at the moment the game
        // is about to draw its UI layer — exactly the same window the
        // cursor-hide loop above uses. Writing SoftTarget here means
        // whatever the game cleared mid-frame gets overridden BEFORE the
        // renderer observes the null pointer, so the "target acquired"
        // animation + sound queued on null→entity transition never fires.
        //
        // Gates mirror TargetSelector's: only when the user has the beta
        // feature on AND wants SoftTarget written AND the cam is active
        // AND there's a cone pick to restore AND no hard target exists
        // (hard target would legitimately suppress SoftTarget writes).
        if (Configuration.BetaMouseBindsEnabled
            && Configuration.WriteSoftTarget
            && cameraController.IsActive
            && targetSelector.CachedBest != null
            && TargetManager.Target == null)
        {
            var pick = targetSelector.CachedBest;
            if (TargetManager.SoftTarget?.GameObjectId != pick.GameObjectId)
                TargetSelector.DirectSetSoftTarget(pick);
        }

        WindowSystem.Draw();
        reticleOverlay.Draw();
        interactIndicator.Draw();
        debugOverlay.Draw();
    }
    private void OpenConfigUi() => ConfigWindow.IsOpen = true;
}
