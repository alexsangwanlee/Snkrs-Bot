using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OrdHelper.Core;

namespace OrdHelper.App;

/// <summary>
/// 자동 조합: 목표까지의 조합 순서를 패가 바뀔 때마다 다시 계산해
/// "어느 유닛 선택 → 어떤 키(또는 채팅)"를 순서대로 보여준다. 입력은 사용자가 직접 한다.
/// </summary>
public sealed class AutoTab : TabBase
{
    private sealed record Pick(Unit Unit)
    {
        public override string ToString() => $"{Unit.Name} · {Unit.Tier}";
    }

    private readonly ComboBox _picker = new() { IsEditable = true, Width = 260, Margin = new Thickness(4, 0, 4, 0) };
    private readonly CheckBox _autoGoal = new() { Content = "목표가 없으면 효율 1위를 자동 선택", IsChecked = true, Margin = new Thickness(12, 0, 0, 0) };
    private readonly WrapPanel _goals = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _summary = Ui.Text("", 13, true);
    private readonly ProgressBar _progress = new() { Height = 10, Margin = new Thickness(0, 4, 0, 8) };
    private readonly StackPanel _steps = new();
    private readonly StackPanel _side = new() { Margin = new Thickness(12, 0, 4, 12) };
    private readonly DockPanel _view = new() { Margin = new Thickness(0, 8, 0, 8) };

    public AutoTab(AppState state, Action toggleOverlay) : base(state)
    {
        _picker.ItemsSource = Data.Units.Values.Where(u => u.HasRecipe && !u.IsWildcard)
            .OrderByDescending(u => Ui.GradeRank(u.Grade)).ThenBy(u => u.Name).Select(u => new Pick(u)).ToList();
        _autoGoal.Click += (_, _) =>
        {
            State.AutoGoal = _autoGoal.IsChecked == true;
            State.Notify();
        };
        var top = new StackPanel
        {
            Children =
            {
                Ui.Row(Ui.Text("목표"), _picker,
                    Ui.Button("추가", () =>
                    {
                        if (_picker.SelectedItem is Pick pick) State.AddGoals([pick.Unit.Id]);
                    }),
                    Ui.Button("목표 비우기", () =>
                    {
                        State.Goals.Clear();
                        State.Notify();
                    }),
                    _autoGoal, Ui.Button("미니 오버레이", toggleOverlay)),
                _goals, _summary, _progress,
            },
        };
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
            _goals.Children.Add(Ui.Chip(unit, auto ? " (자동: 효율 1위)" : "  ✕", auto ? null : () =>
            {
                State.Goals.Remove(id);
                State.Notify();
            }));
        }
        _steps.Children.Clear();
        _side.Children.Clear();
        if (goals.Count == 0)
        {
            _summary.Text = "목표를 고르세요. 조합도우미·효율 추천·유카0 조합 탭에서도 보낼 수 있습니다.";
            _progress.Value = 0;
            return;
        }

        var plan = Crafting.Plan(Data, State.Hand, goals);
        var ready = plan.Steps.Count(s => s.Ready);
        _summary.Text = plan.Complete && ready == plan.Steps.Count
            ? $"재료 완비 · 조합 {plan.Steps.Count}단계를 순서대로 실행하세요"
            : $"진행률 {plan.Progress:P0} · 지금 가능 {ready}단계 · 남은 조합 {plan.Steps.Count}단계";
        _progress.Value = plan.Progress * 100;

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
        foreach (var note in plan.Notes) _side.Children.Add(Ui.Text("· " + note, 12, color: Ui.Muted));
        _side.Children.Add(Ui.Text("\n▶ 표시는 위에서부터 차례로 하면 지금 바로 되는 조합입니다. 조합 후 워크3 패가 다시 읽히면 목록이 갱신됩니다.",
            11, color: Ui.Muted));
    }

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
        var how = Ui.Text(Crafting.Instruction(Data, step), 13, true, step.Ready ? Brushes.Black : Ui.Muted);
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
            Background = step.Ready ? Ui.ReadyBack : Brushes.White, ToolTip = Ui.Describe(step.Result),
        };
    }

    private static TextBlock Header(string text)
    {
        var block = Ui.Text(text, 13, true);
        block.Margin = new Thickness(0, 10, 0, 4);
        return block;
    }
}

/// <summary>게임 위에 띄우는 작은 창: 지금 할 조합만. 포커스를 뺏지 않는다. 드래그로 이동, 우클릭 닫기.</summary>
public sealed class OverlayWindow : Window
{
    private readonly AppState _state;
    private readonly StackPanel _body = new() { Margin = new Thickness(10, 8, 10, 8) };

    public OverlayWindow(AppState state)
    {
        _state = state;
        Title = "원랜디 자동 조합";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Ui.Brush("#D9101418");
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = 40;
        Top = 80;
        FontFamily = new FontFamily("Malgun Gothic, Segoe UI");
        Content = _body;
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
        _body.Children.Clear();
        var (goals, _) = _state.EffectiveGoals();
        if (goals.Count == 0)
        {
            _body.Children.Add(Line("자동 조합 목표 없음", "#BDBDBD"));
            return;
        }
        var data = _state.Data;
        var plan = Crafting.Plan(data, _state.Hand, goals);
        _body.Children.Add(Line($"{string.Join(", ", goals.Select(Ui.Name))}  {plan.Progress:P0}", "#FFD54F", 14));
        foreach (var step in plan.Steps.Where(s => s.Ready).Take(6))
            _body.Children.Add(Line($"▶ {Crafting.Instruction(data, step)}  ⇒ {step.Result.Name}", "#A5D6A7"));
        var waiting = plan.Steps.Count(s => !s.Ready);
        if (waiting > 0) _body.Children.Add(Line($"대기 {waiting}단계", "#BDBDBD", 12));
        if (plan.Missing.Count > 0)
            _body.Children.Add(Line("부족: " + string.Join(", ", plan.Missing.OrderByDescending(m => m.Value).Take(5)
                .Select(m => $"{Ui.Name(m.Key)}×{m.Value}")), "#EF9A9A", 12));
        if (plan.Steps.Count == 0) _body.Children.Add(Line("완성!", "#A5D6A7"));
    }

    private static TextBlock Line(string text, string color, double size = 13) => new()
    {
        Text = text, Foreground = Ui.Brush(color), FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 1),
    };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
