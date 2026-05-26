# Veiled Aim (BDO + ER) — Claude Context

## READ THIS FIRST, EVERY SESSION

The authoritative project state lives at:

```
~/.claude-context/projects/veiled-aim/STATE.md
```

**Before doing anything else in this repo:**

1. `Read` that STATE.md file in full.
2. Run `git log --oneline -10` and `git status` to verify the recorded state still matches reality (file is a snapshot — git is authoritative for actual current state).
3. If they disagree, **trust git**, and update STATE.md to match before continuing.

## STATE.md updates are MANDATORY, NOT OPTIONAL

**Default behaviour:** after any non-trivial action in this repo, update STATE.md before ending the turn. Do not ask for permission, do not wait to be reminded. The user has explicitly said "always update the state.md note" — treat that as a standing instruction for every session in this repo.

**Trigger list — if any of these happen, STATE.md gets updated in the same response:**

- Shipping a new version → bump the version table + summarize what shipped
- Discovering a new game-pipeline / hook pattern → add to "Architectural notes / hard-won knowledge"
- Hitting a regression and fixing it → note the fix (with the version it was fixed in)
- Auditing existing code and finding bugs (even pre-commit) → record the bug + fix under an audit/pass entry
- User redirects scope, sets a new direction, or gives durable feedback
- Mid-flight work being interrupted (commit-ready code that hasn't been pushed)
- Any session resume — verify STATE.md still matches reality, edit drift away

**What NOT to put in STATE.md:**

- `git log` / `git diff` output (git is authoritative — STATE.md is for the *why*)
- Ephemeral todos already captured in TaskCreate
- Step-by-step replays of the conversation

## Repo basics

- **Plugin name:** "Veiled Aim (BDO + ER)" (InternalName still `ActionCameraSoft` for backward compat)
- **Forked from:** `grimgrum/action-cam-for-FFXIV` — has diverged significantly, treat as our own
- **Distribution:** Dalamud custom repo, pluginmaster.json is read directly by the game — no PR flow
- **CI:** GitHub Actions builds .NET 10 Dalamud plugin on push to main + on tag
- **Release flow:** bump `ActionCamera.csproj` AssemblyVersion → bump `pluginmaster.json` (AssemblyVersion + LastUpdate epoch) → commit → push → tag → release workflow

## File map (high-level)

| File | Purpose |
|------|---------|
| `Plugin.cs` | Lifecycle, OnFrameworkUpdate orchestration, key handlers |
| `Configuration.cs` | All persistent config — keybinds, exemptions, FoV |
| `CameraController.cs` | Cursor-sync state machine, two-stage spike guard |
| `RotationDriver.cs` | Cooperative character/mount rotation via MovementDirectionUpdate + RMIFly hooks |
| `HardTargetSuppressor.cs` | SetHardTarget hook with AllowNext + LMB click suppression |
| `TargetSelector.cs` | Cone scan for soft-target, CachedBest accessor |
| `InputBinding.cs` | GetAsyncKeyState polling, focus gating |
| `InteractHandler.cs` | (NEW v0.5.13.0) NPC interact + dialogue advance |
| `Windows/ConfigWindow.cs` | ImGui config UI, DrawKeyPicker |
| `pluginmaster.json` | Dalamud custom-repo manifest — gates user-visible release |

## Conventions

- **No half-finished implementations.** If a feature ships, it ships complete with config UI and a test plan in the commit body.
- **Big patches are fine** — user explicitly prefers robustness over churn.
- **Always ask before destructive git operations** (force-push, reset --hard, branch delete) per global instructions.
- **Code comments:** rare, and only for non-obvious WHY. Don't narrate WHAT.
