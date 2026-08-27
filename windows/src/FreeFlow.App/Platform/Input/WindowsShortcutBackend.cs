using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FreeFlow.Core.Shortcuts;

namespace FreeFlow.App.Platform.Input;

public sealed class GlobalShortcutBackendException : Exception
{
    public GlobalShortcutBackendException(string message) : base(message) { }
}

/// <summary>
/// Watches the whole keyboard with a low-level hook and reports normalized events.
/// </summary>
/// <remarks>
/// <para>
/// This is the Windows replacement for the macOS <c>CGEventTap</c> in
/// <c>Sources/GlobalShortcutBackend.swift</c>. It reports the same
/// <see cref="ShortcutInputEvent"/> values, so the entire matching state machine in
/// <c>FreeFlow.Core</c> is shared and unchanged.
/// </para>
/// <para>
/// Two Windows-specific constraints shape the design:
/// </para>
/// <list type="bullet">
/// <item>
/// A <c>WH_KEYBOARD_LL</c> hook is dispatched on the thread that installed it, and
/// only while that thread pumps messages. The hook therefore lives on its own
/// dedicated STA thread with a message loop, rather than on the UI thread, so a busy
/// or blocked UI can never make the hotkey miss keystrokes or trip the system's hook
/// timeout and get silently unregistered.
/// </item>
/// <item>
/// The callback runs synchronously in the input path of every application. It must
/// stay fast and allocation-light, so it does only state reduction and hands
/// higher-level work to the caller's handler.
/// </item>
/// </list>
/// <para>
/// Unlike macOS, no accessibility permission is required to install the hook. It does
/// fail against windows running elevated when this process is not, which is a Windows
/// security boundary and not something the app can work around.
/// </para>
/// </remarks>
public sealed class WindowsShortcutBackend : IDisposable
{
    /// <summary>
    /// Marks input this app synthesizes, so the hook can ignore its own paste
    /// keystrokes instead of treating them as user input.
    /// </summary>
    public static readonly IntPtr InjectedMarker = new(0x46464C57); // "FFLW"

    private readonly object _gate = new();

    private IntPtr _hookHandle;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private NativeMethods.LowLevelKeyboardProc? _callback;
    private ManualResetEventSlim? _started;
    private Exception? _startupError;

    /// <summary>
    /// Called for every keyboard event. Return <see cref="ShortcutConsumeDecision.Consume"/>
    /// to swallow the key so it never reaches the focused application.
    /// </summary>
    /// <remarks>Runs on the hook thread and must return quickly.</remarks>
    public Func<ShortcutInputEvent, ShortcutConsumeDecision>? OnInputEvent { get; set; }

    /// <summary>Called when Escape is pressed. Return true to swallow the key.</summary>
    public Func<bool>? OnEscapePressed { get; set; }

    public bool IsRunning
    {
        get { lock (_gate) return _hookHandle != IntPtr.Zero; }
    }

    public void Start()
    {
        Stop();

        _started = new ManualResetEventSlim(false);
        _startupError = null;

        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "FreeFlow keyboard hook",
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();

        // Surface an installation failure to the caller rather than failing silently.
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new GlobalShortcutBackendException(
                "Global shortcut monitoring did not start in time.");
        }

        if (_startupError is not null)
        {
            throw new GlobalShortcutBackendException(
                $"Global shortcut monitoring could not start: {_startupError.Message}");
        }

        // Seed the matcher with whatever is already held, so starting mid-keypress
        // does not leave the state machine out of sync with the keyboard.
        PublishModifierSnapshot();
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;

        lock (_gate)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
            _hookThread = null;
            _hookThreadId = 0;
        }

        if (thread is null) return;

        if (threadId != 0) MessageLoop.PostQuit(threadId);
        thread.Join(TimeSpan.FromSeconds(2));

        _started?.Dispose();
        _started = null;

        NotifyBackendReset();
    }

    private void HookThreadMain()
    {
        try
        {
            // The delegate is stored in a field so it is not collected while the
            // unmanaged hook still points at it.
            _callback = HookCallback;

            var moduleHandle = NativeMethods.GetModuleHandle(null);
            var handle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, _callback, moduleHandle, 0);

            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _startupError = new GlobalShortcutBackendException(
                    $"SetWindowsHookEx failed (error {error}).");
                _started?.Set();
                return;
            }

            lock (_gate)
            {
                _hookHandle = handle;
                _hookThreadId = MessageLoop.CurrentThreadId;
            }

            _started?.Set();

            // The hook is only delivered while this thread pumps messages.
            MessageLoop.Run();
        }
        catch (Exception error)
        {
            _startupError = error;
            _started?.Set();
        }
        finally
        {
            IntPtr handle;
            lock (_gate)
            {
                handle = _hookHandle;
                _hookHandle = IntPtr.Zero;
            }

            if (handle != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(handle);
            _callback = null;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // Never react to input this app synthesized, or the paste keystrokes would
        // be read back as a new shortcut press.
        if (data.dwExtraInfo == InjectedMarker ||
            (data.flags & NativeMethods.LLKHF_INJECTED) != 0)
        {
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

        if (!isDown && !isUp)
        {
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        var keyCode = NormalizeVirtualKey((ushort)data.vkCode);

        if (isDown && keyCode == VirtualKeys.Escape && OnEscapePressed?.Invoke() == true)
        {
            return new IntPtr(1);
        }

        ShortcutInputEvent inputEvent = VirtualKeys.ModifierKeyCodes.Contains(keyCode)
            ? new ShortcutInputEvent.ModifierChanged(keyCode, isDown)
            // The hook gives no repeat flag, so auto-repeat shows up as repeated
            // key-down messages. Treat a down for an already-held key as a repeat,
            // which is what the matcher expects.
            : new ShortcutInputEvent.KeyChanged(keyCode, isDown, IsRepeat(keyCode, isDown));

        var decision = OnInputEvent?.Invoke(inputEvent) ?? ShortcutConsumeDecision.Passthrough;

        return decision == ShortcutConsumeDecision.Consume
            ? new IntPtr(1)
            : NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private readonly HashSet<ushort> _heldKeys = new();

    private bool IsRepeat(ushort keyCode, bool isDown)
    {
        if (!isDown)
        {
            _heldKeys.Remove(keyCode);
            return false;
        }

        return !_heldKeys.Add(keyCode);
    }

    /// <summary>
    /// Maps the generic modifier codes Windows sometimes reports to a specific side.
    /// </summary>
    /// <remarks>
    /// A low-level hook normally reports side-specific codes already, but the generic
    /// VK_CONTROL / VK_MENU / VK_SHIFT values still arrive from some remapping tools
    /// and virtual keyboards. Resolving them to the left-hand key keeps side-aware
    /// bindings from silently failing.
    /// </remarks>
    private static ushort NormalizeVirtualKey(ushort keyCode) => keyCode switch
    {
        0x10 => VirtualKeys.LShift,
        0x11 => VirtualKeys.LControl,
        0x12 => VirtualKeys.LMenu,
        _ => keyCode,
    };

    /// <summary>Publishes the set of modifier keys currently held.</summary>
    public void PublishModifierSnapshot()
    {
        var pressed = new HashSet<ushort>();
        foreach (var keyCode in VirtualKeys.ModifierKeyCodes)
        {
            if ((NativeMethods.GetAsyncKeyState(keyCode) & 0x8000) != 0) pressed.Add(keyCode);
        }

        OnInputEvent?.Invoke(new ShortcutInputEvent.ModifierSnapshot(pressed));
    }

    private void NotifyBackendReset()
        => OnInputEvent?.Invoke(new ShortcutInputEvent.BackendReset());

    public void Dispose() => Stop();
}
