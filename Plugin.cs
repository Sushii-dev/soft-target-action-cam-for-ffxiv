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

    // True when the user wants action cam on (independent of menu suppression).
    private bool userWantsActive;
    private bool toggleKeyWasDown;
    private bool clearTargetKeyWasDown;
    private bool hardTargetKeyWasDown;

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
            userWantsActive = false;
            return;
        }

        // Compute menu state once per frame. Handlers track edge state even
        // while the gate is closed (so a key held across a menu open/close
        // doesn't fire a phantom press when the menu closes), but the action
        // itself is skipped while a menu is open.
        var menuOpen = IsMenuOpen();

        HandleToggleKey(menuOpen);
        HandleClearTargetKey(menuOpen);
        HandleHardTargetKey(menuOpen);
        ReconcileActiveState(menuOpen);
        cameraController.Update();
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

        var isDown = InputBinding.IsDown(Configuration.ClearHardTargetKey);
        if (isDown && !clearTargetKeyWasDown && !menuOpen)
            ClearHardTarget();
        clearTargetKeyWasDown = isDown;
    }

    private static void ClearHardTarget() => TargetManager.Target = null;

    // Separate user intent from actual state so menu suppression is transparent.
    private void ReconcileActiveState(bool menuOpen)
    {
        var shouldBeActive = userWantsActive && !menuOpen;
        if (shouldBeActive && !cameraController.IsActive)
            cameraController.Activate();
        else if (!shouldBeActive && cameraController.IsActive)
            cameraController.Deactivate();
    }

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

        var isDown = InputBinding.IsDown(Configuration.ActivationKey);

        if (Configuration.HoldToActivate)
        {
            // Hold mode: only follow the key while no menu is open. Holding
            // the key while a menu opens preserves the pre-menu intent so the
            // cam transparently resumes on menu close. Pressing the key while
            // a menu is open is ignored — wouldn't want Ctrl-while-typing to
            // arm the cam on chat close.
            if (!menuOpen) userWantsActive = isDown;
        }
        else
        {
            // Toggle mode: rising edge flips intent, but only when not in a
            // menu so Ctrl pressed in chat doesn't keep toggling state.
            if (isDown && !toggleKeyWasDown && !menuOpen)
                userWantsActive = !userWantsActive;
        }
        toggleKeyWasDown = isDown;
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
