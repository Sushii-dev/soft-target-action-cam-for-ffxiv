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
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/actioncam";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("ActionCamera");
    private ConfigWindow ConfigWindow { get; init; }

    private readonly CameraController cameraController;
    private readonly TargetSelector targetSelector;
    private readonly ReticleOverlay reticleOverlay;
    private readonly HardTargetSuppressor hardTargetSuppressor;
    private readonly RotationDriver rotationDriver;

    // True when the user explicitly engaged the cam via the activation key.
    // This is intent only — actual cam state mirrors cursor visibility (so
    // RMB-hold etc. also activate the cam). userWantsActive is consulted for
    // sticky-off semantics (popup → cursor shown → reset intent unless an
    // auto-resume exemption applies) and for auto-resume bookkeeping.
    private bool userWantsActive;
    private bool toggleKeyWasDown;
    private bool clearTargetKeyWasDown;
    private bool hardTargetKeyWasDown;
    private bool wasExemptedLastTick;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        targetSelector       = new TargetSelector(Configuration);
        cameraController     = new CameraController(Configuration, targetSelector);
        reticleOverlay       = new ReticleOverlay(cameraController, Configuration);
        hardTargetSuppressor = new HardTargetSuppressor(Configuration, targetSelector, () => cameraController.IsActive);
        rotationDriver       = new RotationDriver(Configuration, () => cameraController.IsActive, cameraController.GetCameraHRotation);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle action camera mode. Args: on | off | config | cleartarget"
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
        ReconcileCursorSync();
        cameraController.Update();
        // RotationDriver runs after camera so it reads the freshly-updated yaw.
        rotationDriver.Update();
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

        var shouldBeActive = !cursorVisible || rbHeld;

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

        // Any interactive window (inventory, character sheet, map, shop, etc.)
        // sets FocusedAddon when open. Null means only the game world is active.
        var stage = AtkStage.Instance();
        if (stage == null) return false;
        var unitManager = (AtkUnitManager*)stage->RaptureAtkUnitManager;
        return unitManager != null && unitManager->FocusedAddon != null;
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
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                userWantsActive = true;
                ChatGui.Print("[ActionCamera] Activated.");
                break;
            case "off":
                userWantsActive = false;
                ChatGui.Print("[ActionCamera] Deactivated.");
                break;
            case "config":
                ConfigWindow.IsOpen = true;
                break;
            case "cleartarget":
                ClearHardTarget();
                break;
            default:
                userWantsActive = !userWantsActive;
                ChatGui.Print(userWantsActive ? "[ActionCamera] Activated." : "[ActionCamera] Deactivated.");
                break;
        }
    }

    private void DrawUI()
    {
        WindowSystem.Draw();
        reticleOverlay.Draw();
    }
    private void OpenConfigUi() => ConfigWindow.IsOpen = true;
}
