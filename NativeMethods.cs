using System.Runtime.InteropServices;

namespace TachoClicker;

internal static class NativeMethods
{
    public const int VK_F2 = 0x71;
    public const int VK_F3 = 0x72;
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

    public static void ClickAt(int x, int y, int holdMs = 15)
    {
        SetCursorPos(x, y);

        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;

        inputs[1].type = INPUT_MOUSE;
        inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;

        // Send Down, hold briefly so the game frame registers it, then send Up
        SendInput(1, new[] { inputs[0] }, Marshal.SizeOf<INPUT>());
        Thread.Sleep(holdMs);
        SendInput(1, new[] { inputs[1] }, Marshal.SizeOf<INPUT>());
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
}
