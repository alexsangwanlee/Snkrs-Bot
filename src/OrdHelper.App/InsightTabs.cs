using System.Windows;
using System.Windows.Controls;
using OrdHelper.Core;

namespace OrdHelper.App;

/// <summary>난이도·최소 판수 필터를 공유하는 통계 탭의 바탕.</summary>
public abstract class StatsTab : TabBase
{
    protected int MinGames { get; private set; } = 20;
    private readonly ComboBox _difficulty;

    protected StatsTab(AppState state) : base(state)
    {
        _difficulty = Ui.Choice(Insights.Difficulties, state.Difficulty, value =>
        {
            if (value == State.Difficulty) return;
            State.Difficulty = value;
            State.Notify();
        });
    }

    protected StackPanel Filters(params UIElement[] extra)
    {
        _difficulty.SelectedItem = State.Difficulty;
        var row = Ui.Row(Ui.Text("난이도"), _difficulty, Ui.Text("최소 기록"),
            Ui.Choice(["10", "20", "50", "100"], MinGames.ToString(), value =>
            {
                MinGames = int.Parse(value);
                Refresh();
            }));
        foreach (var element in extra) row.Children.Add(element);
        row.Margin = new Thickness(0, 8, 0, 4);
        return row;
    }

    protected void SyncDifficulty() => _difficulty.SelectedItem = State.Difficulty;

    protected string NextAction(CraftPlan plan)
    {
        var ready = plan.Steps.FirstOrDefault(s => s.Ready);
        if (ready is not null) return $"▶ {ready.Result.Name}: {Crafting.Instruction(Data, ready)}";
        if (plan.Complete) return "완성";
        return "부족: " + string.Join(", ", plan.Missing.OrderByDescending(m => m.Value).Take(3)
            .Select(m => $"{Ui.Name(m.Key)}×{m.Value}"));
    }
}

/// <summary>최고 효율 조합 추천: 유카0 확률 ÷ 남은 비용.</summary>
public sealed class EfficiencyTab : StatsTab
{
    public sealed record Row(string Id, string Name, string Tier, double Efficiency, double Rate, int Games, double AvgYuka,
        double Remaining, double Missing, double Total, string Next);

    private readonly DataGrid _grid = Ui.Grid();
    private readonly DockPanel _view = new() { Margin = new Thickness(0, 0, 0, 8) };

    public EfficiencyTab(AppState state) : base(state)
    {
        _grid.Columns.Add(Ui.Column("목표", "Name", width: 130));
        _grid.Columns.Add(Ui.Column("등급", "Tier"));
        _grid.Columns.Add(Ui.Column("효율", "Efficiency", "0"));
        _grid.Columns.Add(Ui.Column("유카0 확률", "Rate", "P0"));
        _grid.Columns.Add(Ui.Column("평균 유카", "AvgYuka", "0.0"));
        _grid.Columns.Add(Ui.Column("기록", "Games", "N0"));
        _grid.Columns.Add(Ui.Column("남은 %", "Remaining", "P0"));
        _grid.Columns.Add(Ui.Column("남은 비용", "Missing", "0"));
        _grid.Columns.Add(Ui.Column("총 비용", "Total", "0"));
        _grid.Columns.Add(Ui.Column("다음 행동", "Next", width: 360));
        _grid.MouseDoubleClick += (_, _) => UseSelected();

        var top = new StackPanel
        {
            Children =
            {
                Filters(Ui.Button("선택한 목표로 자동 조합", UseSelected)),
                Ui.Text("효율 = 유카0 확률 ÷ (지금 패에서 더 모아야 할 흔함 환산 비용 + 1), 1위를 100으로. " +
                        "유카0 확률은 그 상위 유닛이 들어간 클리어 기록 중 유카 0으로 끝난 비율(적은 표본 보정). " +
                        "이미 가진 상위는 뺍니다. 더블클릭하면 자동 조합 목표가 됩니다.", 12, color: Ui.Muted),
            },
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(top, Dock.Top);
        _view.Children.Add(top);
        _view.Children.Add(_grid);
    }

    public override FrameworkElement View => _view;

    public override void Refresh()
    {
        SyncDifficulty();
        _grid.ItemsSource = Insights.Efficiency(Data, State.Hand, State.Difficulty, MinGames)
            .Select(r => new Row(r.Goal.Id, r.Goal.Name, r.Goal.Tier, r.Efficiency, r.Yuka0Rate, r.Games, r.AvgYuka,
                1 - r.Plan.Progress, r.Plan.MissingCost, r.Plan.TotalCost, NextAction(r.Plan)))
            .ToList();
    }

    private void UseSelected()
    {
        if (_grid.SelectedItem is Row row) State.AddGoals([row.Id]);
    }
}

/// <summary>유카0을 볼 수 있는 조합: 클리어 기록의 상위 구성별 유카0 비율.</summary>
public sealed class Yuka0Tab : StatsTab
{
    public sealed record Row(BuildRow Build, string Name, double Rate, int Yuka0, int Games, double AvgYuka, double Remaining,
        string Support);

    private readonly DataGrid _grid = Ui.Grid();
    private readonly StackPanel _detail = new() { Margin = new Thickness(12, 4, 4, 12) };
    private readonly Grid _view = new();
    private string? _selected;

    public Yuka0Tab(AppState state) : base(state)
    {
        _grid.Columns.Add(Ui.Column("상위 구성", "Name", width: 200));
        _grid.Columns.Add(Ui.Column("유카0 확률", "Rate", "P0"));
        _grid.Columns.Add(Ui.Column("유카0 판", "Yuka0", "N0"));
        _grid.Columns.Add(Ui.Column("기록", "Games", "N0"));
        _grid.Columns.Add(Ui.Column("평균 유카", "AvgYuka", "0.0"));
        _grid.Columns.Add(Ui.Column("남은 %", "Remaining", "P0"));
        _grid.Columns.Add(Ui.Column("같이 쓴 유닛", "Support", width: 300));
        _grid.SelectionChanged += (_, _) =>
        {
            if (_grid.SelectedItem is not Row row) return;
            _selected = row.Name;
            ShowDetail(row.Build);
        };

        var left = new DockPanel();
        var top = new StackPanel
        {
            Children =
            {
                Filters(),
                Ui.Text("유카 = 클리어 순간 필드에 남은 유닛 수. 유카 0 = 매 라운드 다 잡고 깬 판. " +
                        "TMO 클리어 기록을 상위 유닛 구성으로 묶어 유카0 비율 순으로 보여줍니다.", 12, color: Ui.Muted),
            },
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        left.Children.Add(_grid);

        var right = new ScrollViewer { Content = _detail, Background = Ui.Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _view.ColumnDefinitions.Add(new ColumnDefinition());
        _view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        Grid.SetColumn(right, 1);
        _view.Children.Add(left);
        _view.Children.Add(right);
    }

    public override FrameworkElement View => _view;

    public override void Refresh()
    {
        SyncDifficulty();
        var rows = Insights.Yuka0Builds(Data, State.Hand, State.Difficulty, MinGames)
            .Select(b => new Row(b, b.Name, b.Yuka0Rate, b.Yuka0, b.Games, b.AvgYuka, 1 - b.Plan.Progress,
                string.Join(", ", b.Support.Take(5).Select(s => s.Unit.Name))))
            .ToList();
        _grid.ItemsSource = rows;
        _grid.SelectedItem = rows.FirstOrDefault(r => r.Name == _selected) ?? rows.FirstOrDefault();
        if (_grid.SelectedItem is null) _detail.Children.Clear();
    }

    private void ShowDetail(BuildRow build)
    {
        var hand = State.Hand;
        _detail.Children.Clear();
        _detail.Children.Add(Ui.Text(build.Name, 16, true));
        _detail.Children.Add(Ui.Text($"유카0 {build.Yuka0Rate:P0} ({build.Yuka0}/{build.Games}판) · 평균 유카 {build.AvgYuka:0.0}", 12));

        _detail.Children.Add(Header("상위 유닛"));
        var core = new WrapPanel();
        foreach (var unit in build.Core)
            core.Children.Add(Ui.Chip(unit, hand.GetValueOrDefault(unit.Id) > 0 ? " ✔" : "",
                border: hand.GetValueOrDefault(unit.Id) > 0 ? Ui.Ok : null));
        _detail.Children.Add(core);

        _detail.Children.Add(Header("유카0 판에서 같이 쓴 유닛"));
        var support = new WrapPanel();
        foreach (var s in build.Support)
            support.Children.Add(Ui.Chip(s.Unit, $" {s.Share:P0}" + (hand.GetValueOrDefault(s.Unit.Id) > 0 ? " ✔" : ""),
                border: hand.GetValueOrDefault(s.Unit.Id) > 0 ? Ui.Ok : null));
        _detail.Children.Add(support);

        _detail.Children.Add(Header("지금 패에서"));
        var ring = new Ring(88, 9) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
        ring.Set(build.Plan.Progress);
        _detail.Children.Add(ring);
        _detail.Children.Add(Ui.Text($"남은 조합 {build.Plan.Steps.Count}단계 · " +
                                     $"지금 가능 {build.Plan.Steps.Count(s => s.Ready)}단계", 12));
        _detail.Children.Add(Ui.Text(NextAction(build.Plan), 12));

        var buttons = Ui.Row(
            Ui.Button("상위 유닛을 자동 조합 목표로", () => State.AddGoals(build.Core.Select(u => u.Id))),
            Ui.Button("같이 쓴 유닛까지", () => State.AddGoals(build.Core.Concat(build.Support.Select(s => s.Unit))
                .Where(u => u.HasRecipe).Select(u => u.Id))));
        buttons.Margin = new Thickness(-4, 10, 0, 0);
        _detail.Children.Add(buttons);
    }

    private static TextBlock Header(string text)
    {
        var block = Ui.Text(text, 13, true);
        block.Margin = new Thickness(0, 12, 0, 4);
        return block;
    }
}
