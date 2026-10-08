using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrdHelper.Core;

namespace OrdHelper.App;

/// <summary>
/// OX 조합기 / TMO 조합도우미 방식: 전 유닛을 등급(·딜 타입)별 세로 열로 펼쳐 두고 체크로 보유를 찍는다.
/// 체크 = 보유(O). 줄 클릭 +1, 우클릭 −1. 워크3 자동 인식 중에는 읽은 패를 보여주기만 한다.
/// </summary>
public sealed class HelperTab : TabBase
{
    private sealed record Tile(Unit Unit, Border Root, CheckBox Check, TextBlock Count);
    private sealed record Section(string Title, Border Card, CheckBox All, List<Tile> Tiles);

    private const int SectionRows = 22;
    private const double ColumnWidth = 220;
    private readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);
    private readonly List<Section> _sections = [];
    private readonly Grid _board = new() { Margin = new Thickness(0, 0, 8, 12) };
    private readonly TextBox _search = new() { Width = 180, Margin = new Thickness(4, 0, 8, 0) };
    private readonly CheckBox _ownedOnly = new() { Content = "보유·조합 가능만", Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _side = new() { Margin = new Thickness(12, 0, 4, 12) };
    private readonly Grid _view = new();
    private int _columns;
    private string? _selected;

    public HelperTab(AppState state) : base(state)
    {
        var units = Data.Units.Values.Where(u => !u.IsWildcard && !u.Name.StartsWith("미확인"))
            .OrderBy(u => Ui.GradeRank(u.Grade)).ThenBy(u => u.Role).ThenBy(u => u.Name);
        foreach (var group in units.GroupBy(u => u.Role.Length > 0 ? $"{u.Grade} {u.Role}" : u.Grade))
        {
            var chunks = group.Chunk(SectionRows).ToList();
            for (var i = 0; i < chunks.Count; i++)
                _sections.Add(MakeSection(chunks.Count > 1 ? $"{group.Key} {i + 1}" : group.Key, chunks[i]));
        }

        _search.TextChanged += (_, _) => ApplyFilter();
        _ownedOnly.Click += (_, _) => ApplyFilter();
        var toolbar = Ui.Row(Ui.Text("검색"), _search, _ownedOnly,
            Ui.Button("패 비우기", () =>
            {
                State.Manual.Clear();
                State.Notify();
            }),
            Hint("체크 = 보유(O) · 줄 클릭 +1 · 우클릭 −1 · 금색 = 지금 조합 가능"));
        toolbar.Margin = new Thickness(0, 8, 0, 0);

        var scroll = new ScrollViewer { Content = _board, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SizeChanged += (_, _) => Layout((int)Math.Max(1, (scroll.ActualWidth - 20) / ColumnWidth));
        var left = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        left.Children.Add(toolbar);
        left.Children.Add(scroll);

        var right = new ScrollViewer { Content = _side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Panel };
        _view.ColumnDefinitions.Add(new ColumnDefinition());
        _view.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(right, 1);
        _view.Children.Add(left);
        _view.Children.Add(right);
    }

    public override FrameworkElement View => _view;

    /// <summary>창 너비에 맞춰 열 수를 정하고, 섹션을 가장 짧은 열에 차례로 쌓는다 (OX 조합기 배치).</summary>
    private void Layout(int columns)
    {
        if (columns == _columns) return;
        _columns = columns;
        foreach (var column in _board.Children.OfType<StackPanel>()) column.Children.Clear();
        _board.Children.Clear();
        _board.ColumnDefinitions.Clear();
        var stacks = Enumerable.Range(0, columns).Select(i =>
        {
            _board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth) });
            var stack = new StackPanel();
            Grid.SetColumn(stack, i);
            _board.Children.Add(stack);
            return (Stack: stack, Rows: 0);
        }).ToArray();
        foreach (var section in _sections)
        {
            var shortest = Array.IndexOf(stacks, stacks.MinBy(s => s.Rows));
            stacks[shortest].Stack.Children.Add(section.Card);
            stacks[shortest].Rows += section.Tiles.Count + 2;
        }
    }

    private Section MakeSection(string title, Unit[] units)
    {
        var grade = units[0].Grade;
        var color = Ui.GradeBrush(grade);
        var all = new CheckBox { VerticalAlignment = VerticalAlignment.Center, ToolTip = "이 열 전체 보유 / 해제" };
        var header = new DockPanel { Margin = new Thickness(6, 4, 6, 4) };
        DockPanel.SetDock(all, Dock.Right);
        header.Children.Add(all);
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center,
        });
        var rows = new StackPanel { Margin = new Thickness(4, 0, 4, 4) };
        var tiles = units.Select(MakeTile).ToList();
        tiles.ForEach(t => rows.Children.Add(t.Root));
        var tint = ((SolidColorBrush)color).Color;
        var card = new Border
        {
            Width = ColumnWidth - 8, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6),
            BorderBrush = color, BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(40, tint.R, tint.G, tint.B)),
            Child = new StackPanel { Children = { header, rows } },
        };
        all.Click += (_, _) =>
        {
            if (State.IsLive) return;
            foreach (var tile in tiles)
                if (all.IsChecked == true) State.Manual[tile.Unit.Id] = Math.Max(1, State.Manual.GetValueOrDefault(tile.Unit.Id));
                else State.Manual.Remove(tile.Unit.Id);
            State.Notify();
        };
        return new Section(title, card, all, tiles);
    }

    private Tile MakeTile(Unit unit)
    {
        var check = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
        var count = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Ui.Ok, VerticalAlignment = VerticalAlignment.Center };
        var memo = new TextBlock
        {
            Text = unit.Memo, FontSize = 10, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), MaxWidth = 92,
        };
        var name = new TextBlock
        {
            Text = unit.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
        };
        var line = new DockPanel { LastChildFill = true };
        foreach (var (element, dock) in new (UIElement, Dock)[] { (Ui.Face(unit, 24), Dock.Left), (check, Dock.Right), (count, Dock.Right), (memo, Dock.Right) })
        {
            DockPanel.SetDock(element, dock);
            line.Children.Add(element);
        }
        line.Children.Add(name);
        var root = new Border
        {
            Child = line, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(2), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(2), Cursor = Cursors.Hand, ToolTip = Ui.Describe(unit),
        };
        // 체크박스 클릭은 CheckBox가 처리(Handled)하므로 여기까지 오지 않는다.
        root.MouseLeftButtonUp += (_, _) => Click(unit.Id, +1);
        root.MouseRightButtonUp += (_, e) =>
        {
            Click(unit.Id, -1);
            e.Handled = true;
        };
        check.Click += (_, _) =>
        {
            _selected = unit.Id;
            if (State.IsLive) { State.Notify(); return; }
            if (check.IsChecked == true) State.Manual[unit.Id] = Math.Max(1, State.Manual.GetValueOrDefault(unit.Id));
            else State.Manual.Remove(unit.Id);
            State.Notify();
        };
        var tile = new Tile(unit, root, check, count);
        _tiles[unit.Id] = tile;
        return tile;
    }

    private void Click(string id, int delta)
    {
        _selected = id;
        if (!State.IsLive)
        {
            var count = State.Manual.GetValueOrDefault(id) + delta;
            if (count > 0) State.Manual[id] = count;
            else State.Manual.Remove(id);
        }
        State.Notify();
    }

    public override void Refresh()
    {
        var hand = State.Hand;
        var craftable = Crafting.Craftable(Data, hand).Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var tile in _tiles.Values)
        {
            var count = hand.GetValueOrDefault(tile.Unit.Id);
            var canCraft = craftable.Contains(tile.Unit.Id);
            tile.Check.IsChecked = count > 0;
            tile.Check.IsEnabled = !State.IsLive;
            tile.Count.Text = count > 1 ? $"×{count}" : "";
            tile.Root.Background = count > 0 ? Ui.ReadyBack : Ui.Panel;
            tile.Root.BorderBrush = tile.Unit.Id == _selected ? Ui.Sail : canCraft ? Ui.Gold : Brushes.Transparent;
        }
        foreach (var section in _sections)
        {
            section.All.IsChecked = section.Tiles.All(t => hand.GetValueOrDefault(t.Unit.Id) > 0);
            section.All.IsEnabled = !State.IsLive;
        }
        _craftable = craftable;
        ApplyFilter();
        BuildSide(hand, craftable);
    }

    private HashSet<string> _craftable = [];

    private void ApplyFilter()
    {
        var text = _search.Text.Trim();
        var ownedOnly = _ownedOnly.IsChecked == true;
        var hand = State.Hand;
        foreach (var section in _sections)
        {
            var any = false;
            foreach (var tile in section.Tiles)
            {
                var show = (text.Length == 0 || tile.Unit.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            tile.Unit.Memo.Contains(text, StringComparison.OrdinalIgnoreCase)) &&
                           (!ownedOnly || hand.GetValueOrDefault(tile.Unit.Id) > 0 || _craftable.Contains(tile.Unit.Id));
                tile.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                any |= show;
            }
            section.Card.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void BuildSide(IReadOnlyDictionary<string, int> hand, HashSet<string> craftable)
    {
        _side.Children.Clear();
        _side.Children.Add(Heading(State.IsLive ? "내 패 · 워크3에서 읽는 중" : "내 패 · 직접 입력 (OX)"));
        _side.Children.Add(Ui.Text($"유닛 {hand.Values.Sum()}기 · {hand.Count}종", 12, color: Ui.Muted));

        _side.Children.Add(Heading("패 능력치 합계"));
        var stats = new WrapPanel();
        foreach (var chip in AbilityTotals(hand)) stats.Children.Add(Pill(chip));
        if (stats.Children.Count == 0) stats.Children.Add(Ui.Text("능력치 표기된 유닛이 없습니다.", 12, color: Ui.Muted));
        _side.Children.Add(stats);
        _side.Children.Add(Ui.Text("TMO 조합도우미 표기값의 단순 합 (중첩 규칙 미반영)", 11, color: Ui.Muted));

        _side.Children.Add(Heading($"지금 조합 가능 ({craftable.Count})"));
        var ready = new WrapPanel();
        foreach (var unit in craftable.Select(id => Data.Units[id]).OrderByDescending(u => Ui.GradeRank(u.Grade)))
            ready.Children.Add(Ui.Chip(unit, onClick: () => Select(unit.Id), border: Ui.Gold));
        _side.Children.Add(ready);

        if (_selected is not null && Data.Units.TryGetValue(_selected, out var selected)) BuildDetail(selected, hand);
        else
        {
            var hint = Hint("유닛을 누르면 조합식·상위 조합과 남은 %가 여기에 나옵니다.");
            hint.Margin = new Thickness(0, 12, 0, 0);
            _side.Children.Add(hint);
        }
    }

    private void BuildDetail(Unit unit, IReadOnlyDictionary<string, int> hand)
    {
        _side.Children.Add(Heading("선택한 유닛"));
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        title.Children.Add(Ui.Face(unit, 44));
        title.Children.Add(new StackPanel
        {
            Margin = new Thickness(8, 0, 0, 0),
            Children = { Ui.Text(unit.Name, 16, true), Ui.Text($"{unit.Tier} · 보유 {hand.GetValueOrDefault(unit.Id)}", 12, color: Ui.GradeBrush(unit.Grade)) },
        });
        _side.Children.Add(title);
        if (unit.Abilities.Count > 0)
            _side.Children.Add(Ui.Text(string.Join(" · ", unit.Abilities.Select(a => Ui.FormatAbility(a.Key, a.Value))), 12));
        if (unit.Description.Length > 0) _side.Children.Add(Ui.Text(unit.Description, 12, color: Ui.Muted));

        if (unit.HasRecipe)
        {
            // OX 도우미처럼 유닛마다 남은 %를 바로 보여 준다.
            var plan = Crafting.Plan(Data, hand, [unit.Id]);
            var ring = new Ring(72, 8) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            ring.Set(plan.Progress);
            var ringRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { ring } };
            var ringText = Ui.Text($"남은 조합 {plan.Steps.Count}단계\n부족 재료 {plan.Missing.Values.Sum()}기", 12, color: Ui.Muted);
            ringText.Margin = new Thickness(12, 8, 0, 0);
            ringText.VerticalAlignment = VerticalAlignment.Center;
            ringRow.Children.Add(ringText);
            _side.Children.Add(ringRow);
            _side.Children.Add(Heading("조합식"));
            var tree = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
            foreach (var ingredient in unit.Recipe) tree.Items.Add(Node(ingredient.Id, ingredient.Count, hand, 0));
            _side.Children.Add(tree);
            var how = new List<string>();
            if (unit.Key.Length > 0) how.Add($"[{string.Join("/", unit.Hosts.Select(Ui.Name))}] 선택 → {unit.Key}");
            if (unit.Commands.Count > 0) how.Add("채팅: " + string.Join(" / ", unit.Commands));
            if (unit.Lumber > 0 || unit.Gold > 0) how.Add($"목재 {unit.Lumber} · 골드 {unit.Gold}");
            how.AddRange(unit.Notes.Select(n => "조건: " + n));
            how.Add($"출처: {unit.RecipeSource}");
            foreach (var line in how) _side.Children.Add(Ui.Text(line, 12));

            var actions = Ui.Row(Ui.Button("목표에 추가", () => State.AddGoals([unit.Id])));
            actions.Margin = new Thickness(-4, 8, 0, 0);
            if (!State.IsLive) actions.Children.Add(Ui.Button("가능한 만큼 조합 (OX)", () => CraftInManual(unit.Id)));
            _side.Children.Add(actions);
        }

        var usedIn = Data.UsedIn(unit.Id).Where(u => !u.IsWildcard).OrderBy(u => Ui.GradeRank(u.Grade)).ToList();
        if (usedIn.Count > 0)
        {
            _side.Children.Add(Heading($"재료로 쓰이는 곳 ({usedIn.Count})"));
            var wrap = new WrapPanel();
            foreach (var parent in usedIn) wrap.Children.Add(Ui.Chip(parent, onClick: () => Select(parent.Id)));
            _side.Children.Add(wrap);
        }
    }

    private TreeViewItem Node(string id, int need, IReadOnlyDictionary<string, int> hand, int depth)
    {
        var unit = Data.Units[id];
        var have = unit.IsWildcard ? unit.AnyOf.Sum(hand.GetValueOrDefault) : hand.GetValueOrDefault(id);
        var ok = have >= need;
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Ui.Face(unit, 20));
        header.Children.Add(new TextBlock
        {
            Text = $" {(ok ? "✔" : "✘")} {unit.Name}  {have}/{need}", VerticalAlignment = VerticalAlignment.Center,
            Foreground = ok ? Ui.Ok : unit.HasRecipe || unit.IsWildcard ? Ui.Sail : Ui.Bad,
        });
        var item = new TreeViewItem { Header = header, IsExpanded = !ok && depth < 2, ToolTip = Ui.Describe(unit) };
        item.MouseDoubleClick += (_, e) =>
        {
            Select(id);
            e.Handled = true;
        };
        if (depth < 8)
        {
            if (unit.IsWildcard) foreach (var option in unit.AnyOf) item.Items.Add(Node(option, 1, hand, depth + 1));
            else foreach (var ingredient in unit.Recipe) item.Items.Add(Node(ingredient.Id, ingredient.Count, hand, depth + 1));
        }
        return item;
    }

    private void Select(string id)
    {
        _selected = id;
        Refresh();
    }

    /// <summary>OX 패에서 목표까지 지금 가능한 조합 단계를 실제로 적용한다 (재료 차감, 결과 추가).</summary>
    private void CraftInManual(string id)
    {
        foreach (var step in Crafting.Plan(Data, State.Manual, [id]).Steps.Where(s => s.Ready))
        {
            foreach (var use in step.Uses)
                if ((State.Manual[use.Id] -= use.Count) <= 0) State.Manual.Remove(use.Id);
            State.Manual[step.Result.Id] = State.Manual.GetValueOrDefault(step.Result.Id) + 1;
        }
        State.Notify();
    }

    private IEnumerable<string> AbilityTotals(IReadOnlyDictionary<string, int> hand)
    {
        var numbers = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var flags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, count) in hand)
        {
            if (!Data.Units.TryGetValue(id, out var unit)) continue;
            foreach (var (key, value) in unit.Abilities)
            {
                if (value.ValueKind == JsonValueKind.Number) numbers[key] = numbers.GetValueOrDefault(key) + value.GetDouble() * count;
                else if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() == "true")
                    flags.Add(key);
            }
        }
        return numbers.Select(p => $"{p.Key} {p.Value:0.##}").Concat(flags);
    }

    private static TextBlock Hint(string text)
    {
        var block = Ui.Text(text, 12, color: Ui.Muted);
        block.Margin = new Thickness(12, 0, 0, 0);
        return block;
    }

    private static TextBlock Heading(string text)
    {
        var block = Ui.Text(text, 13, true);
        block.Margin = new Thickness(0, 12, 0, 4);
        return block;
    }

    private static Border Pill(string text) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 12, Foreground = Ui.Sail }, Background = Ui.Deck, BorderBrush = Ui.Rope,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 4, 4),
    };
}
