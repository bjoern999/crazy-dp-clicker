using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TachoClicker;

internal static class NativeMethods
{
    public const int VK_F1 = 0x70;
    public const int VK_F2 = 0x71;
    public const int VK_F3 = 0x72;
    public const int VK_F4 = 0x73;
    public const int VK_F5 = 0x74;
    public const int VK_F6 = 0x75;
    public const int VK_F7 = 0x76;
    public const int VK_F8 = 0x77;
    public const int VK_F9 = 0x78;
    public const int VK_F10 = 0x79;
    public const int VK_ADD = 0x6B;
    public const int VK_SUBTRACT = 0x6D;
    public const int VK_OEM_PLUS = 0xBB;
    public const int VK_OEM_MINUS = 0xBD;
    public const int VK_PRIOR = 0x21; // PageUp (Bild auf)
    public const int VK_NEXT = 0x22;  // PageDown (Bild ab)

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    private static readonly double MsToTicks = Stopwatch.Frequency / 1000.0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

    public static bool TryGetDwmTiming(
        out long qpcVBlank,
        out long qpcRefreshPeriod,
        out long cFramesPending,
        out long cFramesDropped)
    {
        var info = new DWM_TIMING_INFO
        {
            cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>()
        };
        if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref info) == 0 &&
            info.qpcVBlank > 0 &&
            info.qpcRefreshPeriod > 0)
        {
            qpcVBlank = info.qpcVBlank;
            qpcRefreshPeriod = info.qpcRefreshPeriod;
            cFramesPending = info.cFramesPending;
            cFramesDropped = info.cFramesDropped;
            return true;
        }

        qpcVBlank = 0;
        qpcRefreshPeriod = 0;
        cFramesPending = 0;
        cFramesDropped = 0;
        return false;
    }

    /// <summary>
    /// Berechnet die exakte VSync-Phase eines QPC-Zeitstempels innerhalb des Hardware-Refresh-Takts [0 .. 16,667 ms).
    /// </summary>
    public static bool TryGetVSyncPhaseMs(
        long qpcTimestamp,
        out double phaseMs,
        out double refreshPeriodMs,
        out long framesPending,
        out long framesDropped)
    {
        if (TryGetDwmTiming(out long qpcVBlank, out long qpcPeriod, out framesPending, out framesDropped))
        {
            long remTicks = ((qpcTimestamp - qpcVBlank) % qpcPeriod + qpcPeriod) % qpcPeriod;
            phaseMs = remTicks * TicksToMs;
            refreshPeriodMs = qpcPeriod * TicksToMs;
            return true;
        }

        phaseMs = -1.0;
        refreshPeriodMs = 0.0;
        framesPending = 0;
        framesDropped = 0;
        return false;
    }

    public static bool IsKeyJustPressed(int vKey, ref bool wasPressed)
    {
        bool isPressed = (GetAsyncKeyState(vKey) & 0x8000) != 0;
        bool justPressed = isPressed && !wasPressed;
        wasPressed = isPressed;
        return justPressed;
    }

    public static bool IsAnyKeyJustPressed(int vKeyA, int vKeyB, ref bool wasPressed)
    {
        bool isPressed = ((GetAsyncKeyState(vKeyA) & 0x8000) != 0) || ((GetAsyncKeyState(vKeyB) & 0x8000) != 0);
        bool justPressed = isPressed && !wasPressed;
        wasPressed = isPressed;
        return justPressed;
    }

    /// <summary>
    /// Fuehrt einen praezisen Mausklick aus: verzichtet auf redundantes SetCursorPos, wenn die Maus bereits am Ziel steht,
    /// und nutzt einen Stopwatch-Spin-Wait fuer holdMs statt Thread.Sleep (0 ms Jitter auf MouseDown UND MouseUp).
    /// Gibt den exakten QPC-Zeitstempel (Stopwatch.GetTimestamp) des MouseDown-Events zurueck.
    /// </summary>
    public static long ClickAt(int x, int y, int holdMs = 15)
    {
        if (!GetCursorPos(out var curPos) || Math.Abs(curPos.X - x) > 2 || Math.Abs(curPos.Y - y) > 2)
        {
            SetCursorPos(x, y);
        }

        var downInput = new INPUT[1];
        downInput[0].type = INPUT_MOUSE;
        downInput[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;

        var upInput = new INPUT[1];
        upInput[0].type = INPUT_MOUSE;
        upInput[0].mi.dwFlags = MOUSEEVENTF_LEFTUP;

        int inputSize = Marshal.SizeOf<INPUT>();

        // MouseDown senden & exakten QPC-Zeitstempel nehmen
        SendInput(1, downInput, inputSize);
        long downQpc = Stopwatch.GetTimestamp();

        // Praezises Halten per Spin-Wait (kein Thread.Sleep-Jitter auf MouseUp!)
        if (holdMs > 0)
        {
            long targetUpQpc = downQpc + (long)(holdMs * MsToTicks);
            if (holdMs > 25)
            {
                long spinThresholdQpc = targetUpQpc - (long)(3.5 * MsToTicks);
                while (Stopwatch.GetTimestamp() < spinThresholdQpc)
                    Thread.Sleep(1);
            }
            while (Stopwatch.GetTimestamp() < targetUpQpc)
                Thread.SpinWait(50);
        }

        SendInput(1, upInput, inputSize);
        return downQpc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UNSIGNED_RATIO
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public UNSIGNED_RATIO rateRefresh;
        public long qpcRefreshPeriod;
        public UNSIGNED_RATIO rateCompose;
        public long qpcVBlank;
        public long cRefresh;
        public uint cDXRefresh;
        public long qpcCompose;
        public long cFrame;
        public uint cDXPresent;
        public long cRefreshFrame;
        public long cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public long cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public long cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public long cFramesLate;
        public uint cFramesOutstanding;
        public long cFrameDisplayed;
        public long qpcFrameDisplayed;
        public long cRefreshFrameDisplayed;
        public long cFrameComplete;
        public long qpcFrameComplete;
        public long cFramePending;
        public long qpcFramePending;
        public long cFramesDisplayed;
        public long cFramesComplete;
        public long cFramesPending;
        public long cFramesAvailable;
        public long cFramesDropped;
        public long cFramesMissed;
        public long cRefreshNextDisplayed;
        public long cRefreshNextPresented;
        public long cRefreshesDisplayed;
        public long cRefreshesPresented;
        public long cRefreshStarted;
        public long cPixelsReceived;
        public long cPixelsDrawn;
        public long cBuffersEmpty;
    }
}
