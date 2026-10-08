using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using OrdHelper.Core;

namespace OrdHelper.App;

public sealed class MainWindow : Window
{
    private readonly AppState _state;
    private readonly Wc3Reader _reader;
    private readonly AutoRunner _runner;
    // 워크3 기본 단축키와 겹치지 않는 후보. 다른 프로그램과 겹치면 바꿀 수 있게 고르게 둔다.
    private static readonly Dictionary<string, uint> Hotkeys = new() { ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["`"] = 0xC0 };
    private string _hotkey = "F6";
    private nint _hwnd;
    private readonly TabControl _tabs = new() { Margin = new Thickness(8, 0, 8, 0) };
    private readonly List<Action> _refreshers = [];
    private readonly TextBlock _status = Ui.Text("워크3 확인 중", 12, color: Ui.Muted);
    private readonly CancellationTokenSource _closing = new();
    private OverlayWindow? _overlay;
    private byte? _slot;
    private volatile bool _resetReader;
    private DateTime _lastRecover = DateTime.MinValue;

    public MainWindow(AppState state)
    {
        _state = state;
        _reader = new Wc3Reader(state.Data);
        _runner = new AutoRunner(state, Reload);
        Title = $"원랜디 조합 도우미 · 맵 {state.Data.MapVersion}";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 560;
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["BodyFont"];
        FontSize = 12;
        Background = Ui.Ink;
        Foreground = Ui.Sail;

        AddTab("조합도우미", new HelperTab(state));
        AddTab("효율 추천", new EfficiencyTab(state));
        AddTab("유카0 조합", new Yuka0Tab(state));
        AddTab("자동 조합", new AutoTab(state, _runner, () => _hotkey));
        _tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _tabs)) RefreshSelected();
        };
        state.Changed += () =>
        {
            RefreshSelected();
            _overlay?.Refresh();
            _runner.Invalidate();
            _runner.Tick();
        };
        _runner.Changed += () =>
        {
            if (_tabs.SelectedIndex == 3) RefreshSelected();
            _overlay?.Refresh();
        };
        state.TabRequested += index => _tabs.SelectedIndex = index;

        var auto = new CheckBox { Content = "워크3 자동 인식", IsChecked = true, Margin = new Thickness(12, 0, 4, 0) };
        auto.Click += (_, _) =>
        {
            state.AutoSync = auto.IsChecked == true;
            state.Notify();
        };
        var slot = Ui.Choice(["자동", "1P", "2P", "3P", "4P"], "자동", value =>
            _slot = value == "자동" ? null : (byte)(value[0] - '1'));
        slot.ToolTip = "내 플레이어 번호. '자동'은 확인된 워크3 빌드에서만 직접 읽고, 아니면 1P로 봅니다.";
        var topmost = new CheckBox { Content = "항상 위", Margin = new Thickness(8, 0, 4, 0) };
        topmost.Click += (_, _) => Topmost = topmost.IsChecked == true;

        var header = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        ComboBox? hotkey = null;
        hotkey = Ui.Choice(Hotkeys.Keys, _hotkey, value =>
        {
            if (value == _hotkey || RegisterHotkey(value)) _hotkey = value;
            else hotkey!.SelectedItem = _hotkey; // 새 키를 못 잡으면 예전 키 유지
            RefreshSelected();
        });
        hotkey.ToolTip = "자동 실행 켜기/끄기 단축키 (게임 중에도 동작)";
        TextBlock Label(string text)
        {
            var label = Ui.Text(text, 12, color: Ui.Muted);
            label.Margin = new Thickness(14, 0, 0, 0);
            return label;
        }
        var controls = Ui.Row(auto, Label("내 번호"), slot, Label("자동 실행 키"), hotkey, topmost,
            Ui.Button("오버레이 열기", ToggleOverlay), Ui.Button("새로고침", Reload));
        controls.Margin = new Thickness(0);
        DockPanel.SetDock(controls, Dock.Right);
        header.Children.Add(controls);
        header.Children.Add(new StackPanel
        {
            Children = { Ui.Text("원피스 랜덤 디펜스 조합 도우미", 16, true), _status },
        });

        var footer = Ui.Text(
            $"데이터: 맵 {state.Data.MapVersion} 조합식 · TMO 클리어 기록 {state.Data.Samples.Count:N0}판 " +
            $"({state.Data.CapturedAt[..10]} 수집) · 유카 = 클리어 시점 필드 유닛 수. 자동 실행을 켰을 때만 워크3에 키와 채팅을 입력합니다 (블리자드 약관상 자동 조작은 제재될 수 있음).",
            11, color: Ui.Muted);
        footer.Margin = new Thickness(10, 4, 10, 6);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(_tabs);
        Content = root;

        Loaded += (_, _) =>
        {
            RefreshSelected();
            _ = PollAsync(_closing.Token);
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd).AddHook((nint _, int message, nint wParam, nint _, ref bool handled) =>
            {
                if (message == 0x0312 && wParam == HotkeyId) // WM_HOTKEY
                {
                    _runner.Toggle();
                    handled = true;
                }
                return 0;
            });
            RegisterHotkey(_hotkey);
        };
        Closed += (_, _) =>
        {
            if (_runner.Armed) _runner.Toggle();
            _closing.Cancel();
            _overlay?.Close();
            _reader.Dispose();
        };
    }

    private void AddTab(string header, TabBase tab)
    {
        _tabs.Items.Add(new TabItem { Header = header, Content = tab.View, Padding = new Thickness(14, 4, 14, 4) });
        _refreshers.Add(tab.Refresh);
    }

    private void RefreshSelected()
    {
        if (_tabs.SelectedIndex < 0) _tabs.SelectedIndex = 0;
        _refreshers[_tabs.SelectedIndex]();
    }

    private void ToggleOverlay()
    {
        if (_overlay is { IsLoaded: true })
        {
            _overlay.Close();
            return;
        }
        _overlay = new OverlayWindow(_state, _runner);
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    /// <summary>1초마다 워크3 패를 읽는다. 구조 스캔은 몇 초 걸릴 수 있어 작업 스레드에서.</summary>
    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_state.AutoSync)
            {
                var slot = _slot;
                Wc3Status result;
                try
                {
                    result = await Task.Run(() =>
                    {
                        // 리더는 이 작업 스레드에서만 만진다: 새로고침 요청도 여기서 처리.
                        if (_resetReader)
                        {
                            _resetReader = false;
                            _reader.Reset();
                        }
                        return _reader.Read(slot);
                    }, token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { result = new Wc3Status($"워크3 읽기 오류: {e.Message}", null, true); }
                _status.Text = result.Message;
                _status.Foreground = result.Error ? Ui.Bad : result.Hand is null ? Ui.Muted : Ui.Ok;
                // 여기서 예외가 새면 읽기 루프가 조용히 멈춘다: 잡아서 새로고침하고 계속 돈다.
                try
                {
                    if (!SameHand(result.Hand, _state.Live))
                    {
                        _state.Live = result.Hand;
                        _state.Notify();
                    }
                }
                catch (Exception e) { Recover(e); }
            }
            else
            {
                _status.Text = "자동 인식 꺼짐 · 조합도우미 탭에서 직접 O/X로 패를 찍으세요";
                _status.Foreground = Ui.Muted;
            }
            // 자동 실행 중에는 조합 결과를 빨리 확인하려고 더 자주 읽는다.
            try { await Task.Delay(_runner.Armed ? 250 : 1000, token); }
            catch (TaskCanceledException) { return; }
        }
    }

    private const int HotkeyId = 0x4F52;

    /// <summary>새 키를 먼저 잡고, 성공했을 때만 예전 키를 놓는다.</summary>
    private bool RegisterHotkey(string key)
    {
        if (_hwnd == 0) return false;
        var id = key == _hotkey ? HotkeyId : HotkeyId + 1;
        if (!RegisterHotKey(_hwnd, id, 0x4000 /* MOD_NOREPEAT */, Hotkeys[key]))
        {
            _status.Text = $"단축키 {key}: 다른 프로그램이 쓰는 중입니다. 다른 키를 고르세요.";
            _status.Foreground = Ui.Bad;
            return false;
        }
        if (id != HotkeyId)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            UnregisterHotKey(_hwnd, id);
            RegisterHotKey(_hwnd, HotkeyId, 0x4000, Hotkeys[key]);
        }
        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>워크3 연결을 처음부터 다시 잡고 화면을 다시 그린다.</summary>
    private void Reload()
    {
        _resetReader = true;
        _state.Live = null; // 다시 읽기 전까지 예전 패로 판단하지 않는다
        _status.Text = "새로고침 중";
        _state.Notify();
    }

    /// <summary>예상 못 한 오류: 기록하고 새로고침. 다시 그리기에서 같은 오류가 반복돼도 2초에 한 번만.</summary>
    public void Recover(Exception error)
    {
        Program.Log(error);
        if (_runner.Armed) _runner.Toggle(); // 같은 오류로 입력을 반복하지 않게 자동 실행은 끈다
        _status.Text = $"오류가 나서 새로고침했습니다 ({error.Message}). 기록: {Program.LogPath}";
        _status.Foreground = Ui.Bad;
        if (DateTime.UtcNow - _lastRecover < TimeSpan.FromSeconds(2)) return;
        _lastRecover = DateTime.UtcNow;
        // 복구 중 다시 그리기가 또 실패해도 여기서 끝낸다 (처리기 안의 예외는 앱을 종료시킨다).
        try { Reload(); }
        catch (Exception again) { Program.Log(again); }
    }

    private static bool SameHand(IReadOnlyDictionary<string, int>? a, IReadOnlyDictionary<string, int>? b) =>
        a is null || b is null ? ReferenceEquals(a, b) : a.Count == b.Count && a.All(p => b.GetValueOrDefault(p.Key) == p.Value);
}

/// <summary>탭 하나: 화면과 다시 그리기.</summary>
public abstract class TabBase(AppState state)
{
    protected AppState State { get; } = state;
    protected GameData Data => State.Data;
    public abstract FrameworkElement View { get; }
    public abstract void Refresh();
}
