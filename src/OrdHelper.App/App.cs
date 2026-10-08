using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrdHelper.Core;

namespace OrdHelper.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        GameData data;
        try
        {
            data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "ord-data.json"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Data\\ord-data.json 을 읽을 수 없습니다.\n{e.Message}\n\n압축을 푼 폴더 그대로 실행해 주세요.",
                "원랜디 조합 도우미", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        Ui.Data = data;
        app.Run(new MainWindow(new AppState(data)));
    }
}

/// <summary>탭들이 함께 보는 상태. 패가 바뀌면 Changed로 다시 그린다.</summary>
public sealed class AppState(GameData data)
{
    public GameData Data { get; } = data;
    public Dictionary<string, int> Manual { get; } = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, int>? Live { get; set; }
    public bool AutoSync { get; set; } = true;
    public List<string> Goals { get; } = [];
    public bool AutoGoal { get; set; } = true;
    public string Difficulty { get; set; } = Insights.AllDifficulties;

    /// <summary>워크3에서 패를 읽고 있으면 그 패, 아니면 직접 찍은 OX 패.</summary>
    public IReadOnlyDictionary<string, int> Hand => AutoSync && Live is not null ? Live : Manual;
    public bool IsLive => AutoSync && Live is not null;

    public event Action? Changed;
    public event Action<int>? TabRequested;
    public void Notify() => Changed?.Invoke();
    public void ShowTab(int index) => TabRequested?.Invoke(index);

    public void AddGoals(IEnumerable<string> ids)
    {
        foreach (var id in ids)
            if (!Goals.Contains(id)) Goals.Add(id);
        Notify();
        ShowTab(3);
    }

    /// <summary>자동 조합에 쓸 목표. 비어 있고 자동 선택이 켜져 있으면 효율 1위.</summary>
    public (IReadOnlyList<string> Goals, bool Auto) EffectiveGoals()
    {
        if (Goals.Count > 0 || !AutoGoal) return (Goals, false);
        var best = Insights.Efficiency(Data, Hand, Difficulty).FirstOrDefault();
        return (best is null ? [] : [best.Goal.Id], true);
    }
}

/// <summary>코드로 만드는 WPF 화면 조각들.</summary>
public static class Ui
{
    public static readonly string[] GradeOrder =
    [
        "흔함", "안흔함", "특별함", "희귀함", "전설", "히든", "초월", "불멸", "영원", "신비함", "신비", "제한됨",
        "변화된", "왜곡됨", "특수함", "해적선", "세라핌", "랜덤유닛", "기타",
    ];

    public static readonly Brush Ok = Brush("#2E7D32");
    public static readonly Brush Bad = Brush("#C62828");
    public static readonly Brush Muted = Brush("#757575");
    public static readonly Brush Gold = Brush("#E0A800");
    public static readonly Brush ReadyBack = Brush("#E8F5E9");
    public static readonly Brush Panel = Brush("#F5F6F8");

    private static readonly Dictionary<string, ImageSource?> Portraits = new(StringComparer.Ordinal);

    public static GameData Data { get; set; } = null!;

    public static string Name(string id) => Data.Find(id)?.Name ?? id;

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static Brush GradeBrush(string grade) => grade switch
    {
        "흔함" => Brush("#9E9E9E"),
        "안흔함" => Brush("#43A047"),
        "특별함" => Brush("#1E88E5"),
        "희귀함" => Brush("#8E24AA"),
        "전설" => Brush("#FB8C00"),
        "히든" => Brush("#00897B"),
        "초월" => Brush("#E53935"),
        "불멸" => Brush("#AD1457"),
        "영원" => Brush("#F9A825"),
        "신비함" or "신비" => Brush("#D81B60"),
        "제한됨" => Brush("#6D4C41"),
        "변화된" => Brush("#5E35B1"),
        "왜곡됨" => Brush("#3949AB"),
        "특수함" => Brush("#00ACC1"),
        "세라핌" => Brush("#7CB342"),
        _ => Brush("#78909C"),
    };

    public static int GradeRank(string grade)
    {
        var index = Array.IndexOf(GradeOrder, grade);
        return index < 0 ? GradeOrder.Length : index;
    }

    public static ImageSource? Portrait(string id)
    {
        if (Portraits.TryGetValue(id, out var cached)) return cached;
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "images", id + ".png");
        BitmapImage? image = null;
        if (File.Exists(path))
        {
            try
            {
                image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 64;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
            }
            catch (Exception e) when (e is IOException or NotSupportedException) { image = null; }
        }
        return Portraits[id] = image;
    }

    /// <summary>초상화 (없으면 이름 첫 글자).</summary>
    public static FrameworkElement Face(Unit unit, double size)
    {
        var image = Portrait(unit.Id);
        if (image is not null) return new Image { Source = image, Width = size, Height = size, Stretch = Stretch.UniformToFill };
        return new Border
        {
            Width = size, Height = size, Background = GradeBrush(unit.Grade), CornerRadius = new CornerRadius(4),
            Child = new TextBlock
            {
                Text = unit.Name.Length > 0 ? unit.Name[..1] : "?", Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                FontSize = size * 0.4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static TextBlock Text(string text, double size = 13, bool bold = false, Brush? color = null) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = color ?? Brushes.Black, TextWrapping = TextWrapping.Wrap,
    };

    public static Button Button(string text, Action onClick)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>작은 유닛 칩: 초상화 + 이름 (+ 꼬리말). 클릭 동작 선택.</summary>
    public static FrameworkElement Chip(Unit unit, string suffix = "", Action? onClick = null, Brush? border = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Face(unit, 22));
        panel.Children.Add(new TextBlock
        {
            Text = unit.Name + suffix, Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var chip = new Border
        {
            Child = panel, Padding = new Thickness(3), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = border ?? GradeBrush(unit.Grade), Background = Brushes.White,
            ToolTip = Describe(unit),
        };
        if (onClick is not null)
        {
            chip.Cursor = System.Windows.Input.Cursors.Hand;
            chip.MouseLeftButtonUp += (_, _) => onClick();
        }
        return chip;
    }

    public static string Describe(Unit unit)
    {
        var lines = new List<string> { $"{unit.Name} · {unit.Tier}" };
        if (unit.IsWildcard) lines.Add("아무거나 1기: " + string.Join(", ", unit.AnyOf.Select(Name)));
        if (unit.HasRecipe)
            lines.Add("조합: " + string.Join(" + ", unit.Recipe.Select(i => i.Count > 1 ? $"{Name(i.Id)}×{i.Count}" : Name(i.Id))));
        if (unit.Lumber > 0 || unit.Gold > 0) lines.Add($"목재 {unit.Lumber} · 골드 {unit.Gold}");
        if (unit.Key.Length > 0) lines.Add($"단축키: [{string.Join("/", unit.Hosts.Select(Name))}] 선택 → {unit.Key}");
        if (unit.Commands.Count > 0) lines.Add("채팅: " + string.Join(" / ", unit.Commands));
        if (unit.Abilities.Count > 0) lines.Add(string.Join(", ", unit.Abilities.Select(a => FormatAbility(a.Key, a.Value))));
        if (unit.Description.Length > 0) lines.Add(unit.Description);
        return string.Join("\n", lines);
    }

    public static string FormatAbility(string key, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => key,
        JsonValueKind.Number => $"{key} {value.GetDouble():0.##}",
        _ => $"{key} {value}",
    };

    public static ComboBox Choice(IEnumerable<string> items, string selected, Action<string> onChange)
    {
        var box = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected, MinWidth = 70, Margin = new Thickness(4, 0, 8, 0) };
        box.SelectionChanged += (_, _) => onChange((string)box.SelectedItem);
        return box;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var child in children)
        {
            if (child is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(child);
        }
        return row;
    }

    public static DataGridTextColumn Column(string header, string path, string? format = null, double width = double.NaN) => new()
    {
        Header = header,
        Binding = new System.Windows.Data.Binding(path) { StringFormat = format },
        Width = double.IsNaN(width) ? DataGridLength.Auto : new DataGridLength(width),
    };

    public static DataGrid Grid() => new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        CanUserAddRows = false, AlternatingRowBackground = Brush("#FAFAFA"), RowHeight = 26,
    };
}
