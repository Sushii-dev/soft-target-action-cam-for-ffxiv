# Action Camera for FFXIV

A Dalamud plugin that brings Guild Wars 2-style action camera to Final Fantasy XIV.

Press a configurable key to enter action camera mode. While active:

- **Mouse freely rotates the camera** — cursor is hidden and locked to centre
- **Your character auto-faces the camera direction** — no more manually turning to match
- **Nearest hostile enemy in your field of view is soft-targeted automatically**

Press the key again (or release it, in hold mode) to return to normal camera control.

---

## Requirements

- [FFXIV](https://www.finalfantasyxiv.com/) with [XIVLauncher](https://goatcorp.github.io/) and [Dalamud](https://github.com/goatcorp/Dalamud)

---

## Installation

### Custom plugin repo (recommended)

1. Open Dalamud settings: `/xlsettings`
2. Go to **Experimental** → **Custom Plugin Repositories**
3. Add the following URL and click **Save**:
   ```
   https://raw.githubusercontent.com/grimgrum/action-cam-for-ffxiv/main/pluginmaster.json
   ```
4. Open `/xlplugins`, search for **Action Camera**, and click **Install**

### Manual

1. Download `latest.zip` from the [Releases](https://github.com/grimgrum/action-cam-for-ffxiv/releases/latest) page
2. Extract into your Dalamud dev plugins folder
3. Load via `/xlplugins` → Installed → **Load Dev Plugin**

---

## Usage

| Command | Effect |
|---|---|
| `/actioncam` | Toggle action camera on/off |
| `/actioncam on` | Activate |
| `/actioncam off` | Deactivate |
| `/actioncam config` | Open settings window |

Or bind a key in the settings window and use that instead of the slash command.

---

## Configuration

Open `/actioncam config` to adjust:

| Setting | Default | Description |
|---|---|---|
| Activation key | *(none)* | Key that toggles or holds action cam |
| Hold to activate | Off | Hold key = active, release = off |
| Horizontal sensitivity | 0.003 | Mouse X speed |
| Vertical sensitivity | 0.003 | Mouse Y speed |
| Invert vertical axis | Off | Flip up/down |
| Auto-rotate character | On | Character faces camera direction |
| Facing offset | 180° | Angle correction between camera and character forward |
| Auto-target | On | Soft-target nearest enemy in view |
| FOV cone | 30° | Half-angle of the targeting cone |
| Max distance | 30y | Maximum range for auto-target candidates |
| Angle weight | 2.0 | Higher = prefer centred targets; lower = prefer closer |
| Min/max pitch | −83° / 37° | Vertical camera travel limits |

### First-time calibration

The first time you activate action camera, check which way your character faces:

- **Character faces the same direction as the camera** — working correctly (default 180° offset)
- **Character faces toward the camera lens** — set the Facing Offset slider to **0°** in the config window

---

## Building from source

**Requirements:** .NET 8 SDK, FFXIV + Dalamud installed (the build reads Dalamud DLLs from your local installation)

```powershell
git clone https://github.com/grimgrum/action-cam-for-ffxiv.git
cd action-cam-for-ffxiv
dotnet build ActionCamera.csproj -c Debug -p:Platform=x64
```

Output: `bin\x64\Debug\ActionCamera\ActionCamera.dll`

Add the output folder to Dalamud's **Dev Plugin Locations** (`/xlsettings` → Experimental) to load without installing.

### Cutting a release

```powershell
# Bumps version in csproj + pluginmaster.json, commits, and creates a git tag.
.\bump-version.ps1 1.1.0.0

# Push the tag — CI builds and publishes the GitHub Release automatically.
git push && git push --tags
```

---

## Technical notes

Camera rotation is written directly to `GameCamera.HRotation` / `VRotation` in game memory at offsets `0x130` / `0x134`. These offsets have been stable across many patches but may shift after major updates. If the camera stops responding after a patch, open an issue.

---

## License

[AGPL-3.0-or-later](LICENSE)
