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
    }

    /// <summary>
    /// Cam state mirrors cursor visibility — cursor hidden ⇒ cam active —
    /// PLUS an explicit RMB-held branch so the game's native "right-click to
    /// rotate camera" gesture also gives the user cam features (cone target,
    /// character facing) even though it doesn't necessarily flip the UI-layer
    /// AtkCursor.IsVisible flag.
    ///
    /// userWantsActive is consulted only for intent management while cursor
    /// is visible:
    ///   - exempted condition active: defer (cam off, intent kept).
    ///   - just-cleared exemption + cursor still visible: auto-resume by
    ///     calling Hide() again.
    ///   - sustained cursor-visible without exemption: sticky off (intent
    ///     reset, fresh activation press required).
    /// </summary>
    private void ReconcileCursorSync()
    {
        var cursorVisible = CameraController.IsGameCursorVisible();
        var exempted = IsCurrentlyExempted();

        if (cursorVisible && userWantsActive)
        {
            if (exempted)
            {
                // Defer.
            }
            else if (wasExemptedLastTick)
            {
                cameraController.RequestHideCursor();
            }
            else
            {
                userWantsActive = false;
            }
        }
        wasExemptedLastTick = exempted;

        // Re-read after the potential auto-resume Hide() above.
        cursorVisible = CameraController.IsGameCursorVisible();
        var rbHeld = CameraController.IsRmbHeld();

        // Cam runs when the UI says cursor is hidden (CTRL-driven session)
        // OR when RMB is held (game-driven camera rotation, we add cone +
        // facing on top).
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

        // Toggle only. Hold-to-activate was removed when cursor-sync became
        // the activation model.
        //
        // The menuOpen gate prevents toggling while the player is typing in
        // chat / any focused addon — actual cam state mirrors cursor
        // visibility, but we still don't want a key in chat to swap the
        // hide/show state.
        var isDown = InputBinding.IsDown(Configuration.ActivationKey);
        var rising = isDown && !toggleKeyWasDown;
        toggleKeyWasDown = isDown;
        if (!rising || menuOpen) return;

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
