# Veiled Aim (BDO + ER)

A Dalamud plugin that brings **BDO-style cursor-driven action camera** and **Elden Ring-style soft targeting** to Final Fantasy XIV.

Press a configurable key to **hide the cursor** — you are now "aiming with the camera":

- **Mouse rotates the camera freely.** Your character auto-faces the camera direction.
- **Cone soft-target.** The most-centred hostile in your field of view is locked into the soft / mouseover slot continuously, so vanilla and ReAction pronouns ("Field Target" / "Soft Target") can pick it up.
- **Hard target on demand.** Bind a key (MMB works great) to lock the cone pick as your hard target. Optionally the same key clears the hard target on the next press, making it a single toggle.

Anything that makes the cursor visible — popup, alt-tab, menu — turns the camera off and **keeps it off** until you press the activation key again (BDO-style sticky-off). Per-scenario auto-resume checkboxes are available if you want certain UIs (inventory, cutscenes, etc.) to gracefully resume the camera when they close.

This started as a fork of [grimgrum/action-cam-for-FFXIV](https://github.com/grimgrum/action-cam-for-FFXIV). The camera activation model, targeting pipeline, and input handling have all been rewritten — the upstream project remains the credit for the foundational direct-memory camera control approach.

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
   https://raw.githubusercontent.com/Sushii-dev/soft-target-action-cam-for-ffxiv/main/pluginmaster.json
   ```
4. Open `/xlplugins`, search for **Veiled Aim**, and click **Install**

### Manual

1. Download `latest.zip` from the [Releases](https://github.com/Sushii-dev/soft-target-action-cam-for-ffxiv/releases/latest) page
2. Extract into your Dalamud dev plugins folder
3. Load via `/xlplugins` → Installed → **Load Dev Plugin**

---

## Slash commands

| Command | Effect |
|---|---|
| `/actioncam` | Toggle on/off |
| `/actioncam on` | Activate |
| `/actioncam off` | Deactivate |
| `/actioncam config` | Open settings window |
| `/actioncam cleartarget` | Clear current hard target |

You can also bind keys directly in the settings window — keyboard, modifiers (Ctrl/Shift/Alt), and mouse buttons are all bindable.

---

## How it works

- **Activation key** flips the game's cursor visibility. Cursor hidden ⇒ camera active.
- **Sticky-off:** any external cursor-show event (popup, alt-tab, focused addon) deactivates the camera and clears intent — you press the activation key again to resume.
- **Re-Hide loop:** the game re-asserts cursor visibility when our cone writes MouseOverTarget. We override each frame so the activation stays stable; the override runs before render so there's no flicker.
- **RMB / LMB cooperation:** while RMB (or LMB, if bound to camera-rotate in FFXIV's mouse settings) is held, we step out of the way — the game drives camera rotation, we contribute cone targeting and character facing on top.
- **Hard-target keybind** bypasses the soft-target-promotion hook so its press always lands. While a hard target is set, the soft-target cone is paused (no flickering between hard and soft); when the hard target dies / despawns / is cleared, the cone resumes.
- **Click-to-target suppression** while the camera is active so left-clicks can't accidentally swap or clear your hard target.

---

## Configuration

Open `/actioncam config`. Sections:

- **Activation** — activation keybind, auto-resume exemption checkboxes
- **Mouse** — sensitivity X/Y, invert vertical
- **Character Facing** — auto-rotate character toggle, facing offset
- **Auto-Targeting** — cone FOV / range / scoring weights, target-slot toggles (MouseOver / SoftTarget), aggro filter, suppression options, hard-target & clear-target keybinds
- **Reticle** — visibility, colour
- **Vertical Camera Limits** — min / max pitch

---

## Building from source

**Requirements:** .NET 10 SDK, FFXIV + Dalamud installed (build reads Dalamud DLLs from your local installation).

```powershell
git clone https://github.com/Sushii-dev/soft-target-action-cam-for-ffxiv.git
cd soft-target-action-cam-for-ffxiv
dotnet build ActionCamera.csproj -c Debug -p:Platform=x64
```

Output: `bin\x64\Debug\ActionCameraSoft\` — add the output folder to Dalamud's **Dev Plugin Locations** (`/xlsettings` → Experimental) to load without installing.

---

## License

[AGPL-3.0-or-later](LICENSE)
