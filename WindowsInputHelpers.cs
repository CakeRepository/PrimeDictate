using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SharpHook;
using SharpHook.Data;

namespace PrimeDictate;

internal sealed record ForegroundInputTarget(
    IntPtr WindowHandle,
    IntPtr FocusedWindowHandle,
    uint ProcessId,
    string? Title,
    string? ProcessName)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(this.Title)
            ? $"window 0x{this.WindowHandle.ToInt64():X}"
            : $"{this.Title} (0x{this.WindowHandle.ToInt64():X})";

    public bool IsStillForeground()
    {
        var current = Capture();
        return current is not null &&
            current.WindowHandle == this.WindowHandle &&
            current.ProcessId == this.ProcessId;
    }

    public static ForegroundInputTarget? Capture()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return new ForegroundInputTarget(
            handle,
            GetFocusedWindow(handle),
            processId,
            GetWindowTitle(handle),
            GetProcessName(processId));
    }

    public bool TryInjectTextDirectly(string text)
    {
        if (this.FocusedWindowHandle == IntPtr.Zero ||
            !NativeMethods.IsWindow(this.FocusedWindowHandle) ||
            (this.FocusedWindowHandle != this.WindowHandle &&
             !NativeMethods.IsChild(this.WindowHandle, this.FocusedWindowHandle)))
        {
            return false;
        }

        return WindowsFocusedTextControl.TryReplaceSelection(this.FocusedWindowHandle, text);
    }

    public bool TryRestoreForInput() => WindowsInputActivation.TryRestore(this);

    private static IntPtr GetFocusedWindow(IntPtr handle)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(handle, out _);
        if (threadId == 0)
        {
            return IntPtr.Zero;
        }

        var guiThreadInfo = new NativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };
        return NativeMethods.GetGUIThreadInfo(threadId, ref guiThreadInfo)
            ? guiThreadInfo.FocusWindow
            : IntPtr.Zero;
    }

    private static string? GetWindowTitle(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return null;
        }

        var title = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(handle, title, title.Capacity) > 0
            ? title.ToString()
            : null;
    }

    private static string? GetProcessName(uint processId)
    {
        if (processId > int.MaxValue)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.IsNullOrWhiteSpace(process.ProcessName) ? null : process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal static class WindowsMousePointerIndicator
{
    private const uint SpiGetMouseSonar = 0x101C;

    private static readonly EventSimulator EventSimulator = new();

    public static void PulseIfMouseSonarEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var enabled = false;
        if (!NativeMethods.SystemParametersInfo(SpiGetMouseSonar, 0, ref enabled, 0) || !enabled)
        {
            return;
        }

        _ = EventSimulator.SimulateKeyStroke(new[] { KeyCode.VcLeftControl });
    }
}

internal static class WindowsUnicodeInput
{
    private const int InputKeyboard = 1;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyReturn = 0x0D;
    private const ushort VirtualKeyTab = 0x09;
    private const int VirtualKeyCapital = 0x14;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFUnicode = 0x0004;
    private const int ShiftModifier = 1;
    private const int CtrlModifier = 2;
    private const int AltModifier = 4;
    private const int MaxInputEventsPerBatch = 128;
    private static readonly TimeSpan ModifierReleaseWait = TimeSpan.FromMilliseconds(750);

    public static void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (WindowsFocusedTextControl.TryReplaceSelection(text))
        {
            AppLog.Info("Text injection used focused edit-control insertion.");
            return;
        }

        AppLog.Info("Text injection using keyboard simulation fallback.");
        WaitForModifierKeysReleased(ModifierReleaseWait);

        var layout = GetForegroundKeyboardLayout();
        var batch = new List<NativeMethods.Input>(MaxInputEventsPerBatch);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                continue;
            }

            AddCharacterInput(batch, text[i], layout);

            if (batch.Count >= MaxInputEventsPerBatch)
            {
                SendInputBatch(CollectionsMarshal.AsSpan(batch));
                batch.Clear();
                Thread.Sleep(1);
            }
        }

        if (batch.Count > 0)
        {
            SendInputBatch(CollectionsMarshal.AsSpan(batch));
        }
    }

    private static void AddCharacterInput(List<NativeMethods.Input> inputs, char character, IntPtr layout)
    {
        if (character is '\r' or '\n')
        {
            AddVirtualKeyStroke(inputs, VirtualKeyReturn, withShift: false);
            return;
        }

        if (character == '\t')
        {
            AddVirtualKeyStroke(inputs, VirtualKeyTab, withShift: false);
            return;
        }

        var translated = NativeMethods.VkKeyScanEx(character, layout);
        if (translated != -1)
        {
            var virtualKey = (ushort)(translated & 0xFF);
            var modifiers = (translated >> 8) & 0xFF;
            if ((modifiers & (CtrlModifier | AltModifier)) == 0)
            {
                var withShift = (modifiers & ShiftModifier) != 0;
                if (char.IsLetter(character) && IsCapsLockEnabled())
                {
                    withShift = !withShift;
                }

                AddVirtualKeyStroke(inputs, virtualKey, withShift);
                return;
            }
        }

        inputs.Add(CreateUnicodeInput(character, keyUp: false));
        inputs.Add(CreateUnicodeInput(character, keyUp: true));
    }

    private static void AddVirtualKeyStroke(List<NativeMethods.Input> inputs, ushort virtualKey, bool withShift)
    {
        if (withShift)
        {
            inputs.Add(CreateVirtualKeyInput(VirtualKeyShift, keyUp: false));
        }

        inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: false));
        inputs.Add(CreateVirtualKeyInput(virtualKey, keyUp: true));

        if (withShift)
        {
            inputs.Add(CreateVirtualKeyInput(VirtualKeyShift, keyUp: true));
        }
    }

    private static void SendInputBatch(ReadOnlySpan<NativeMethods.Input> inputs)
    {
        if (inputs.IsEmpty)
        {
            return;
        }

        var inputArray = inputs.ToArray();
        var sent = NativeMethods.SendInput(
            (uint)inputArray.Length,
            inputArray,
            Marshal.SizeOf<NativeMethods.Input>());
        if (sent != inputArray.Length)
        {
            throw new InvalidOperationException(
                $"Text injection sent {sent:N0} of {inputArray.Length:N0} input events. Win32 error: {Marshal.GetLastWin32Error()}.");
        }
    }

    private static NativeMethods.Input CreateUnicodeInput(char character, bool keyUp) =>
        new()
        {
            Type = InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    Scan = (ushort)character,
                    Flags = KeyEventFUnicode | (keyUp ? KeyEventFKeyUp : 0)
                }
            }
        };

    private static NativeMethods.Input CreateVirtualKeyInput(ushort virtualKey, bool keyUp) =>
        new()
        {
            Type = InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventFKeyUp : 0
                }
            }
        };

    private static void WaitForModifierKeysReleased(TimeSpan maxWait)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < maxWait && IsAnyModifierKeyDown())
        {
            Thread.Sleep(10);
        }
    }

    private static bool IsAnyModifierKeyDown() =>
        IsKeyDown(0x10) || // Shift
        IsKeyDown(0x11) || // Control
        IsKeyDown(0x12) || // Alt
        IsKeyDown(0x5B) || // Left Windows
        IsKeyDown(0x5C);   // Right Windows

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsCapsLockEnabled() =>
        (NativeMethods.GetKeyState(VirtualKeyCapital) & 1) != 0;

    private static IntPtr GetForegroundKeyboardLayout()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var threadId = foregroundWindow == IntPtr.Zero
            ? NativeMethods.GetCurrentThreadId()
            : NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        return NativeMethods.GetKeyboardLayout(threadId);
    }
}

internal static class WindowsInputActivation
{
    private const int SwRestore = 9;
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromMilliseconds(500);

    public static bool TryRestore(ForegroundInputTarget target)
    {
        if (!OperatingSystem.IsWindows() || !NativeMethods.IsWindow(target.WindowHandle))
        {
            return false;
        }

        if (NativeMethods.IsIconic(target.WindowHandle))
        {
            _ = NativeMethods.ShowWindowAsync(target.WindowHandle, SwRestore);
        }

        var currentThreadId = NativeMethods.GetCurrentThreadId();
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var foregroundThreadId = foregroundWindow == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThreadId = NativeMethods.GetWindowThreadProcessId(target.WindowHandle, out _);
        var attachedThreadIds = new List<uint>(capacity: 2);

        try
        {
            AttachToThread(currentThreadId, foregroundThreadId, attachedThreadIds);
            AttachToThread(currentThreadId, targetThreadId, attachedThreadIds);

            _ = NativeMethods.BringWindowToTop(target.WindowHandle);
            _ = NativeMethods.SetForegroundWindow(target.WindowHandle);
            _ = NativeMethods.SetActiveWindow(target.WindowHandle);

            if (target.FocusedWindowHandle != IntPtr.Zero &&
                NativeMethods.IsWindow(target.FocusedWindowHandle) &&
                (target.FocusedWindowHandle == target.WindowHandle ||
                 NativeMethods.IsChild(target.WindowHandle, target.FocusedWindowHandle)))
            {
                _ = NativeMethods.SetFocus(target.FocusedWindowHandle);
            }
        }
        finally
        {
            foreach (var threadId in attachedThreadIds)
            {
                _ = NativeMethods.AttachThreadInput(currentThreadId, threadId, false);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ActivationTimeout)
        {
            if (target.IsStillForeground())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return target.IsStillForeground();
    }

    private static void AttachToThread(uint currentThreadId, uint otherThreadId, List<uint> attachedThreadIds)
    {
        if (otherThreadId == 0 || otherThreadId == currentThreadId || attachedThreadIds.Contains(otherThreadId))
        {
            return;
        }

        if (NativeMethods.AttachThreadInput(currentThreadId, otherThreadId, true))
        {
            attachedThreadIds.Add(otherThreadId);
        }
    }
}

internal static class WindowsFocusedTextControl
{
    private const int EmReplaceSel = 0x00C2;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ReplaceSelectionTimeoutMs = 1_000;

    public static bool TryReplaceSelection(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        var guiThreadInfo = new NativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };

        if (!NativeMethods.GetGUIThreadInfo(foregroundThreadId, ref guiThreadInfo))
        {
            return false;
        }

        return TryReplaceSelection(guiThreadInfo.FocusWindow, text);
    }

    public static bool TryReplaceSelection(IntPtr focusedWindow, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (focusedWindow == IntPtr.Zero || !NativeMethods.IsWindow(focusedWindow) || !IsEditLikeWindow(focusedWindow))
        {
            return false;
        }

        var delivered = NativeMethods.SendMessageTimeout(
            focusedWindow,
            EmReplaceSel,
            new IntPtr(1),
            text,
            SmtoAbortIfHung,
            ReplaceSelectionTimeoutMs,
            out _);
        return delivered != IntPtr.Zero;
    }

    private static bool IsEditLikeWindow(IntPtr windowHandle)
    {
        var className = GetClassName(windowHandle);
        return className.Contains("Edit", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetClassName(IntPtr windowHandle)
    {
        var className = new StringBuilder(256);
        return NativeMethods.GetClassName(windowHandle, className, className.Capacity) > 0
            ? className.ToString()
            : string.Empty;
    }
}

internal static partial class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WsExTransparent = 0x00000020;
    internal const int WsExToolWindow = 0x00000080;
    internal const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo guiThreadInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern short VkKeyScanEx(char character, IntPtr keyboardLayout);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }
}
