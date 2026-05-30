using System;
using Dalamud.Hooking;

namespace ActionCamera;

/// <summary>
/// Drops the action-failure log text ("Invalid target.", "Target out of
/// range.", "That action cannot be used at this time." etc.) — but ONLY
/// while a mouse-bind fire is in flight (<see cref="Plugin.MouseBindFireInProgress"/>).
///
/// RaptureLogModule.ShowLogMessage(uint logMessageId) is the canonical entry
/// point for these on-screen / chat-log error toasts, and it is invoked
/// SYNCHRONOUSLY inside the UseAction validation path — i.e. on the same
/// thread, same call stack, while our ExecuteSlotById call is still on the
/// stack. So gating on the fire-in-progress flag captures exactly the text
/// our own bound fire would have produced (no valid target / out of range)
/// and nothing else: normal keyboard play, other plugins, and any message
/// emitted outside the fire window pass through untouched.
///
/// We intentionally do NOT hook RaptureLogModule.Update (the queue-drain),
/// which renders on a LATER frame — by then the flag is already cleared.
/// The synchronous ShowLogMessage entry is the only correct interception
/// point for a tight flag window.
/// </summary>
internal sealed unsafe class LogMessageSuppressor : IDisposable
{
    // void RaptureLogModule.ShowLogMessage(RaptureLogModule* thisPtr, uint logMessageId)
    // First arg is the instance pointer (FFXIVClientStructs MemberFunction).
    private delegate void ShowLogMessageDelegate(nint logModule, uint logMessageId);

    private readonly Hook<ShowLogMessageDelegate>? hook;

    public long SuppressedCount { get; private set; }
    public uint LastSuppressedId { get; private set; }

    public LogMessageSuppressor()
    {
        try
        {
            hook = Plugin.GameInterop.HookFromSignature<ShowLogMessageDelegate>(
                "E9 ?? ?? ?? ?? 40 88 AE", Detour);
            hook.Enable();
            Plugin.Log.Information("LogMessageSuppressor: ShowLogMessage hook installed.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[ActionCamera] Failed to hook ShowLogMessage.");
        }
    }

    private void Detour(nint logModule, uint logMessageId)
    {
        if (Plugin.MouseBindFireInProgress)
        {
            SuppressedCount++;
            LastSuppressedId = logMessageId;
            return; // swallow the error text our own bound fire triggered
        }

        hook!.Original(logModule, logMessageId);
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
