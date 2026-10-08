using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OrdHelper.App;

/// <summary>
/// 워크3에 키·마우스 입력을 보낸다 (사용자가 켠 자동 실행 전용).
/// 게임이 앞에 있을 때만 보내고, 포커스를 빼앗지 않는다.
/// </summary>
public static class Input
{
    private const string ProcessName = "Warcraft III";

    /// <summary>앞에 떠 있는 창이 워크3면 그 창 핸들, 아니면 0.</summary>
    public static nint ForegroundWc3()
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName == ProcessName ? hwnd : 0;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return 0; } // 그새 종료됨
    }

    /// <summary>
    /// 앞에 있는 워크3 창의 게임 화면 안 좌표인가 (아래 명령 UI 띠 제외).
    /// ponytail: 아래 22%를 UI로 고정. 해상도·UI 배율별로 다르면 비율만 조정.
    /// </summary>
    public static bool IsGameField(int x, int y)
    {
        var hwnd = ForegroundWc3();
        if (hwnd == 0 || !GetClientRect(hwnd, out var rect)) return false;
        // 오버레이 등 다른 창 위를 누른 것은 게임 클릭이 아니다.
        if (GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */) != hwnd) return false;
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);
        return x >= origin.X && x < origin.X + rect.Right && y >= origin.Y && y < origin.Y + rect.Bottom * 0.78;
    }

    /// <summary>키 한 번. 스캔코드로 보내야 게임 단축키가 한/영 상태와 무관하게 먹는다. 워크3가 앞에 없으면 false.</summary>
    public static bool Tap(char key)
    {
        var hwnd = ForegroundWc3();
        var vk = (ushort)(VkKeyScan(char.ToLowerInvariant(key)) & 0xFF);
        var scan = (ushort)MapVirtualKey(vk, 0);
        return Send(hwnd, Key(scan, KeyScanCode), Key(scan, KeyScanCode | KeyUp));
    }

    /// <summary>채팅 명령: Enter → 글자(유니코드라 IME 영향 없음) → Enter. 중간에 워크3가 뒤로 가면 멈추고 false.</summary>
    public static bool Chat(string text)
    {
        var hwnd = ForegroundWc3();
        if (!Enter(hwnd)) return false;
        Thread.Sleep(80);
        foreach (var c in text)
            if (!Send(hwnd, Unicode(c, 0), Unicode(c, KeyUp))) return false;
        Thread.Sleep(40);
        return Enter(hwnd);
    }

    private static bool Enter(nint hwnd) => Send(hwnd, Key(0x1C, KeyScanCode), Key(0x1C, KeyScanCode | KeyUp));

    /// <summary>보내기 직전에 워크3가 여전히 맨 앞인지 확인한다. 아니면 보내지 않는다.</summary>
    private static bool Send(nint wc3, params INPUT[] inputs)
    {
        if (wc3 == 0 || GetForegroundWindow() != wc3) return false;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException($"입력 전송 실패 ({Marshal.GetLastWin32Error()}). 워크3가 관리자 권한이면 이 프로그램도 관리자로 실행하세요.");
        return true;
    }

    private const uint KeyUp = 0x0002, KeyUnicode = 0x0004, KeyScanCode = 0x0008;

    private static INPUT Key(ushort scan, uint flags) =>
        new() { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Scan = scan, Flags = flags } } };

    private static INPUT Unicode(char c, uint flags) =>
        new() { Type = 1, U = new InputUnion { Ki = new KEYBDINPUT { Scan = c, Flags = KeyUnicode | flags } } };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char c);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

}

/// <summary>
/// 실제 마우스 왼쪽 클릭(뗄 때)을 알려주는 전역 저수준 훅. 자동 실행 중에만 건다.
/// 전용 스레드의 메시지 루프에서 돌려서 UI가 바빠도 마우스가 끊기지 않고 Windows가 훅을 떼지 않는다.
/// 프로그램이 보낸 클릭(INJECTED)은 무시한다.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly HookProc _proc;
    private readonly Action<int, int> _onLeftClick;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private uint _threadId;
    private nint _hook;

    public bool Installed => _hook != 0;

    public MouseHook(Action<int, int> onLeftClick)
    {
        _onLeftClick = onLeftClick;
        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _proc = Callback; // 대리자가 GC되지 않게 필드로 붙잡아 둔다
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(14, _proc, GetModuleHandle(null), 0); // WH_MOUSE_LL
            ready.Set();
            if (_hook == 0) return;
            while (GetMessage(out var message, 0, 0, 0) > 0) DispatchMessage(ref message);
            UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "mouse hook" }.Start();
        ready.Wait();
    }

    private nint Callback(int code, nint message, nint data)
    {
        if (code >= 0 && message == 0x0202) // WM_LBUTTONUP
        {
            var info = Marshal.PtrToStructure<MouseInfo>(data);
            if ((info.Flags & 1) == 0) // LLMHF_INJECTED 아님
                _ui.BeginInvoke(() => _onLeftClick(info.Point.X, info.Point.Y));
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, 0, 0);
    }

    private delegate nint HookProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInfo
    {
        public Input.POINT Point;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public Input.POINT Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProc proc, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? name);
}
