using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OrdHelper.Core;

namespace OrdHelper.App;

/// <summary>
/// 자동 조합: 목표까지의 조합 순서를 패가 바뀔 때마다 다시 계산한다.
/// 위쪽은 남은 % 링과 지금 할 행동(키캡), 자동 실행 상태. 아래는 전체 단계와 부족한 재료.
/// </summary>
public sealed class AutoTab : TabBase
{
    private sealed record Pick(Unit Unit)
    {
        public override string ToString() => $"{Unit.Name} · {Unit.Tier}";
    }

    private readonly AutoRunner _runner;
    private readonly Func<string> _hotkey;
    private readonly Ring _ring = new(132, 12);
    private readonly StackPanel _next = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
    private readonly Button _toggle;
    private readonly ComboBox _picker = new() { IsEditable = true, Width = 260, Margin = new Thickness(4, 0, 4, 0) };
    private readonly CheckBox _autoGoal = new() { Content = "목표가 없으면 효율 1위로", IsChecked = true, Margin = new Thickness(12, 0, 0, 0) };
    private readonly WrapPanel _goals = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly StackPanel _steps = new();
    private readonly StackPanel _side = new() { Margin = new Thickness(12, 0, 4, 12) };
    private readonly DockPanel _view = new() { Margin = new Thickness(0, 8, 0, 8) };

    public AutoTab(AppState state, AutoRunner runner, Func<string> hotkey, Action toggleOverlay) : base(state)
    {
        _runner = runner;
        _hotkey = hotkey;
        _toggle = Ui.Button("", runner.Toggle);
        _toggle.Margin = new Thickness(0);
        _picker.ItemsSource = Data.Units.Values.Where(u => u.HasRecipe && !u.IsWildcard)
            .OrderByDescending(u => Ui.GradeRank(u.Grade)).ThenBy(u => u.Name).Select(u => new Pick(u)).ToList();
        _autoGoal.Click += (_, _) =>
        {
            State.AutoGoal = _autoGoal.IsChecked == true;
            State.Notify();
        };

        var hero = new DockPanel { Margin = new Thickness(4, 4, 0, 14) };
        DockPanel.SetDock(_ring, Dock.Left);
        hero.Children.Add(_ring);
        hero.Children.Add(_next);

        var goalRow = Ui.Row(Ui.Text("목표"), _picker,
            Ui.Button("목표 추가", () =>
            {
                if (_picker.SelectedItem is Pick pick) State.AddGoals([pick.Unit.Id]);
            }),
            Ui.Button("목표 비우기", () =>
            {
                State.Goals.Clear();
                State.Notify();
            }),
            _autoGoal, Ui.Button("미니 오버레이", toggleOverlay));
        var top = new StackPanel { Children = { hero, goalRow, _goals } };
        DockPanel.SetDock(top, Dock.Top);
        _view.Children.Add(top);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        var right = new ScrollViewer { Content = _side, Background = Ui.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(right, 1);
        body.Children.Add(new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(right);
        _view.Children.Add(body);
    }

    public override FrameworkElement View => _view;

    public override void Refresh()
    {
        _autoGoal.IsChecked = State.AutoGoal;
        var (goals, auto) = State.EffectiveGoals();
        _goals.Children.Clear();
        foreach (var id in goals)
        {
            var unit = Data.Units[id];
            _goals.Children.Add(Ui.Chip(unit, auto ? " (효율 1위 자동)" : "  ✕", auto ? null : () =>
            {
                State.Goals.Remove(id);
                State.Notify();
            }));
        }
        _steps.Children.Clear();
        _side.Children.Clear();
        _next.Children.Clear();
        _toggle.Content = _runner.Armed ? $"자동 실행 끄기 ({_hotkey()})" : $"자동 실행 켜기 ({_hotkey()})";
        _toggle.Style = _runner.Armed ? null : (Style)Application.Current.Resources["PrimaryButton"];
        if (goals.Count == 0)
        {
            _ring.Set(0);
            _next.Children.Add(Ui.Text("목표를 고르세요", 22, true));
            _next.Children.Add(Ui.Text("조합도우미·효율 추천·유카0 조합 탭에서도 보낼 수 있습니다.", 12, color: Ui.Muted));
            return;
        }

        var plan = Crafting.Plan(Data, State.Hand, goals);
        _ring.Set(plan.Progress);
        BuildNext(plan);

        if (plan.Steps.Count == 0) _steps.Children.Add(Ui.Text("이미 모두 보유하고 있습니다.", 13, color: Ui.Ok));
        // 지금 가능한 단계는 앞의 지금 가능한 단계에만 기대므로, 위로 모아도 순서가 유지된다.
        var ordered = plan.Steps.OrderBy(s => !s.Ready).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == 0 && ordered[i].Ready) _steps.Children.Add(Header("지금 할 조합 (위에서부터)"));
            if (!ordered[i].Ready && (i == 0 || ordered[i - 1].Ready)) _steps.Children.Add(Header("재료가 모이면"));
            _steps.Children.Add(StepRow(i + 1, ordered[i]));
        }

        _side.Children.Add(Header("부족한 재료"));
        var missing = new WrapPanel();
        foreach (var (id, count) in plan.Missing.OrderByDescending(m => m.Value))
            missing.Children.Add(Ui.Chip(Data.Units[id], $" ×{count}", border: Ui.Bad));
        if (missing.Children.Count == 0) missing.Children.Add(Ui.Text("없음", 12, color: Ui.Ok));
        _side.Children.Add(missing);
        _side.Children.Add(Header("자원·조건"));
        _side.Children.Add(Ui.Text($"목재 {plan.Lumber} · 골드 {plan.Gold}", 12));
        foreach (var note in plan.Notes) _side.Children.Add(Ui.Text(note, 12, color: Ui.Muted));
        _side.Children.Add(Header("자동 실행"));
        _side.Children.Add(Ui.Text(
            "채팅 조합(히든·초월·불멸·영원 등)은 프로그램이 명령어를 직접 입력합니다. " +
            "단축키 조합은 안내된 유닛을 클릭하면 프로그램이 키를 누릅니다. " +
            "패에서 결과가 확인되면 다음 단계로 넘어가고, 확인이 안 되면 새로고침 후 한 번 더 시도합니다.", 12, color: Ui.Muted));
    }

    /// <summary>링 오른쪽: 지금 할 행동을 가장 크게.</summary>
    private void BuildNext(CraftPlan plan)
    {
        var done = plan.Steps.Count == 0;
        var step = plan.Steps.FirstOrDefault(s => s.Ready);
        _next.Children.Add(Ui.Text(
            done ? "완성" : step is null ? "재료를 모으는 중" : $"다음: {step.Result.Name}", 22, true,
            done ? Ui.Ok : null));
        _next.Children.Add(Ui.Text($"남은 조합 {plan.Steps.Count}단계 · 지금 가능 {plan.Steps.Count(s => s.Ready)}단계", 12, color: Ui.Muted));
        if (step is not null)
        {
            var action = Crafting.Action(Data, step);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
            switch (action.Kind)
            {
                case ActionKind.Key:
                    line.Children.Add(Ui.Chip(action.Host ?? step.Result, " 선택"));
                    line.Children.Add(KeyCap(action.Text));
                    break;
                case ActionKind.Chat:
                    line.Children.Add(Ui.Text("채팅", 13, color: Ui.Muted));
                    line.Children.Add(KeyCap(action.Text));
                    break;
                default:
                    line.Children.Add(Ui.Text(Crafting.Instruction(Data, step), 14, true));
                    break;
            }
            _next.Children.Add(line);
        }
        var status = Ui.Text(_runner.Status, 12, color: _runner.Armed ? Ui.Gold : Ui.Muted);
        status.Margin = new Thickness(0, 0, 0, 8);
        _next.Children.Add(status);
        _next.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _toggle } });
    }

    /// <summary>물리 키처럼 보이는 키캡 (아래 테두리 2px).</summary>
    public static Border KeyCap(string text) => new()
    {
        Child = new TextBlock
        {
            Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.Sail,
            FontFamily = (FontFamily)Application.Current.Resources["NumFont"],
        },
        Background = Ui.Deck, BorderBrush = Ui.Rope, BorderThickness = new Thickness(1, 1, 1, 3),
        CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border StepRow(int index, CraftStep step)
    {
        var line = new DockPanel();
        var number = Ui.Text($"{index,2}. {(step.Ready ? "▶" : "·")}", 13, true, step.Ready ? Ui.Ok : Ui.Muted);
        number.Width = 52;
        DockPanel.SetDock(number, Dock.Left);
        line.Children.Add(number);
        var face = Ui.Face(step.Result, 28);
        DockPanel.SetDock(face, Dock.Left);
        line.Children.Add(face);
        var how = Ui.Text(Crafting.Instruction(Data, step), 13, true, step.Ready ? Ui.Sail : Ui.Muted);
        how.Width = 240;
        DockPanel.SetDock(how, Dock.Right);
        line.Children.Add(how);
        line.Children.Add(new StackPanel
        {
            Margin = new Thickness(8, 0, 8, 0),
            Children =
            {
                Ui.Text($"{step.Result.Name}  ({step.Result.Tier})", 13, true, Ui.GradeBrush(step.Result.Grade)),
                Ui.Text(string.Join(" + ", step.Uses.Select(u => u.Count > 1 ? $"{Ui.Name(u.Id)}×{u.Count}" : Ui.Name(u.Id))), 12, color: Ui.Muted),
            },
        });
        return new Border
        {
            Child = line, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(4),
            Background = step.Ready ? Ui.ReadyBack : Ui.Panel, ToolTip = Ui.Describe(step.Result),
        };
    }

    private static TextBlock Header(string text)
    {
        var block = Ui.Text(text, 13, true);
        block.Margin = new Thickness(0, 10, 0, 4);
        return block;
    }
}

/// <summary>게임 위에 띄우는 작은 창: 남은 % 링 + 지금 할 행동. 포커스를 뺏지 않는다. 드래그로 이동, 우클릭 닫기.</summary>
public sealed class OverlayWindow : Window
{
    private readonly AppState _state;
    private readonly AutoRunner _runner;
    private readonly Ring _ring = new(48, 6, caption: false);
    private readonly StackPanel _lines = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public OverlayWindow(AppState state, AutoRunner runner)
    {
        _state = state;
        _runner = runner;
        Title = "원랜디 자동 조합";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Ui.Brush("#E610141A");
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 40;
        Top = 80;
        FontFamily = (FontFamily)Application.Current.Resources["BodyFont"];
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 12, 8), Children = { _ring, _lines },
        };
        MouseLeftButtonDown += (_, _) => DragMove();
        MouseRightButtonUp += (_, _) => Close();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x80); // NOACTIVATE | TOOLWINDOW
        };
        Refresh();
    }

    public void Refresh()
    {
        _lines.Children.Clear();
        var (goals, _) = _state.EffectiveGoals();
        if (goals.Count == 0)
        {
            _ring.Set(0);
            _lines.Children.Add(Line("자동 조합 목표 없음", Ui.Muted));
            return;
        }
        var data = _state.Data;
        var plan = Crafting.Plan(data, _state.Hand, goals);
        _ring.Set(plan.Progress);
        _lines.Children.Add(Line(string.Join(", ", goals.Select(Ui.Name)), Ui.Sail, 14));
        var step = plan.Steps.FirstOrDefault(s => s.Ready);
        _lines.Children.Add(step is null
            ? Line(plan.Steps.Count == 0 ? "완성" : "재료 모으는 중 · 부족: " + string.Join(", ", plan.Missing
                .OrderByDescending(m => m.Value).Take(4).Select(m => $"{Ui.Name(m.Key)}×{m.Value}")), plan.Steps.Count == 0 ? Ui.Ok : Ui.Muted)
            : Line($"{step.Result.Name}: {Crafting.Instruction(data, step)}", Ui.Gold, 15));
        if (_runner.Armed) _lines.Children.Add(Line(_runner.Status, Ui.Muted, 12));
    }

    private static TextBlock Line(string text, Brush color, double size = 13) => new()
    {
        Text = text, Foreground = color, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 1),
    };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
