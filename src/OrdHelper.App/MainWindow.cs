using System.Windows;
using System.Windows.Controls;
using OrdHelper.Core;

namespace OrdHelper.App;

public sealed class MainWindow : Window
{
    private readonly AppState _state;
    private readonly Wc3Reader _reader;
    private readonly TabControl _tabs = new() { Margin = new Thickness(8, 0, 8, 0) };
    private readonly List<Action> _refreshers = [];
    private readonly TextBlock _status = Ui.Text("워크래프트3 확인 중…", 12, color: Ui.Muted);
    private readonly CancellationTokenSource _closing = new();
    private OverlayWindow? _overlay;
    private byte? _slot;

    public MainWindow(AppState state)
    {
        _state = state;
        _reader = new Wc3Reader(state.Data);
        Title = $"원랜디 조합 도우미 · 맵 {state.Data.MapVersion}";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 560;
        FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic, Segoe UI");

        AddTab("조합도우미", new HelperTab(state));
        AddTab("효율 추천", new EfficiencyTab(state));
        AddTab("유카0 조합", new Yuka0Tab(state));
        AddTab("자동 조합", new AutoTab(state, ToggleOverlay));
        _tabs.SelectionChanged += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _tabs)) RefreshSelected();
        };
        state.Changed += () =>
        {
            RefreshSelected();
            _overlay?.Refresh();
        };
        state.TabRequested += index => _tabs.SelectedIndex = index;

        var auto = new CheckBox { Content = "WC3 자동 인식", IsChecked = true, Margin = new Thickness(12, 0, 4, 0) };
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
        var controls = Ui.Row(auto, Ui.Text("슬롯", 12), slot, topmost, Ui.Button("미니 오버레이", ToggleOverlay));
        controls.Margin = new Thickness(0);
        DockPanel.SetDock(controls, Dock.Right);
        header.Children.Add(controls);
        header.Children.Add(new StackPanel
        {
            Children = { Ui.Text("원피스 랜덤 디펜스 조합 도우미", 16, true), _status },
        });

        var footer = Ui.Text(
            $"데이터: 맵 {state.Data.MapVersion} 조합식 · TMO 클리어 기록 {state.Data.Samples.Count:N0}판 " +
            $"({state.Data.CapturedAt[..10]} 수집) · 유카 = 클리어 시점 필드 유닛 수. 이 프로그램은 게임을 읽기만 하고 입력하지 않습니다.",
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
        Closed += (_, _) =>
        {
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
        _overlay = new OverlayWindow(_state);
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
                try { result = await Task.Run(() => _reader.Read(slot), token); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { result = new Wc3Status($"WC3 읽기 오류: {e.Message}", null, true); }
                _status.Text = result.Message;
                _status.Foreground = result.Error ? Ui.Bad : result.Hand is null ? Ui.Muted : Ui.Ok;
                if (!SameHand(result.Hand, _state.Live))
                {
                    _state.Live = result.Hand;
                    _state.Notify();
                }
            }
            else
            {
                _status.Text = "자동 인식 꺼짐 · 조합도우미 탭에서 직접 O/X로 패를 찍으세요";
                _status.Foreground = Ui.Muted;
            }
            try { await Task.Delay(1000, token); }
            catch (TaskCanceledException) { return; }
        }
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
