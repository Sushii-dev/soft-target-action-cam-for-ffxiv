using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ActionCamera.Windows;

namespace ActionCamera;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IGamepadState GamepadState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/actioncam";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("ActionCamera");
    private ConfigWindow ConfigWindow { get; init; }

    private readonly CameraController cameraController;

    // Debounce state for the toggle key
    private bool toggleKeyWasDown;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        cameraController = new CameraController(Configuration);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle action camera mode. Args: on | off | config"
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
        cameraController.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
        {
            // If player logs out while action cam is active, deactivate cleanly.
            if (cameraController.IsActive) cameraController.Deactivate();
            return;
        }

        HandleToggleKey();
        cameraController.Update();
    }

    private void HandleToggleKey()
    {
        if (Configuration.ActivationKey == Dalamud.Game.ClientState.Keys.VirtualKey.NO_KEY) return;

        var isDown = KeyState[Configuration.ActivationKey];

        if (Configuration.HoldToActivate)
        {
            // Activate while held, deactivate on release.
            if (isDown && !cameraController.IsActive) cameraController.Activate();
            else if (!isDown && cameraController.IsActive) cameraController.Deactivate();
        }
        else
        {
            // Toggle on leading edge of key press.
            if (isDown && !toggleKeyWasDown)
            {
                if (cameraController.IsActive) cameraController.Deactivate();
                else cameraController.Activate();
            }
            toggleKeyWasDown = isDown;
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                cameraController.Activate();
                ChatGui.Print("[ActionCamera] Activated.");
                break;
            case "off":
                cameraController.Deactivate();
                ChatGui.Print("[ActionCamera] Deactivated.");
                break;
            case "config":
                ConfigWindow.IsOpen = true;
                break;
            default:
                if (cameraController.IsActive)
                {
                    cameraController.Deactivate();
                    ChatGui.Print("[ActionCamera] Deactivated.");
                }
                else
                {
                    cameraController.Activate();
                    ChatGui.Print("[ActionCamera] Activated.");
                }
                break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();
    private void OpenConfigUi() => ConfigWindow.IsOpen = true;
}
