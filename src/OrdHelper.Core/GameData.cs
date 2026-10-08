using System.Collections.Concurrent;
using System.Text.Json;

namespace OrdHelper.Core;

public sealed record Ingredient(string Id, int Count);

public sealed class Unit
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Grade { get; init; } = "";
    public string Role { get; init; } = "";
    public List<Ingredient> Recipe { get; init; } = [];
    public int Gold { get; init; }
    public int Lumber { get; init; }
    /// <summary>자동 판정할 수 없는 조건(아이템, 상위 제한 등). 화면에 안내만 한다.</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>조합 단축키. Hosts 중 하나를 선택한 상태에서 누른다.</summary>
    public string Key { get; init; } = "";
    public List<string> Hosts { get; init; } = [];
    /// <summary>채팅 조합 명령 (상위 유닛).</summary>
    public List<string> Commands { get; init; } = [];
    public Dictionary<string, JsonElement> Abilities { get; init; } = [];
    public string Description { get; init; } = "";
    /// <summary>OX 조합기식 짧은 능력 요약 (예: "0.5스턴 깍11").</summary>
    public string Memo { get; init; } = "";
    public string RecipeSource { get; init; } = "";
    /// <summary>대상 유닛을 클릭해 시전해야 하는 조합 (랜덤전용 유닛 지정).</summary>
    public bool Targeted { get; init; }
    /// <summary>선택 재료: 이 중 아무 유닛 1기로 대신한다.</summary>
    public List<string> AnyOf { get; init; } = [];

    public bool IsWildcard => AnyOf.Count > 0;
    public bool HasRecipe => Recipe.Count > 0;
    public string Tier => Role.Length == 0 ? Grade : $"{Grade} [{Role}]";
    public override string ToString() => Name;
}

/// <summary>TMO 클리어 기록 한 판. Yuka = 클리어 시점 필드 유닛 카운트.</summary>
public sealed record ClearSample(string Difficulty, int Yuka, string[] Units);

public sealed class GameData
{
    private readonly Dictionary<string, double> _cost = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Unit>> _usedIn = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _memo = new(StringComparer.Ordinal);

    public string MapVersion { get; }
    public string CapturedAt { get; }
    public IReadOnlyList<string> Sources { get; }
    public IReadOnlyDictionary<string, Unit> Units { get; }
    public IReadOnlyDictionary<string, string> Aliases { get; }
    public IReadOnlyList<ClearSample> Samples { get; }

    public GameData(IEnumerable<Unit> units, IEnumerable<ClearSample>? samples = null,
        IReadOnlyDictionary<string, string>? aliases = null, string mapVersion = "", string capturedAt = "",
        IReadOnlyList<string>? sources = null)
    {
        Units = units.ToDictionary(u => u.Id, StringComparer.Ordinal);
        Samples = samples?.ToList() ?? [];
        Aliases = aliases ?? new Dictionary<string, string>();
        MapVersion = mapVersion;
        CapturedAt = capturedAt;
        Sources = sources ?? [];
        foreach (var unit in Units.Values)
        foreach (var ingredient in unit.Recipe)
        {
            var ids = Units.TryGetValue(ingredient.Id, out var material) && material.IsWildcard
                ? material.AnyOf.Append(material.Id)
                : [ingredient.Id];
            foreach (var id in ids)
            {
                if (!_usedIn.TryGetValue(id, out var list)) _usedIn[id] = list = [];
                if (!list.Contains(unit)) list.Add(unit);
            }
        }
    }

    public static GameData Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var units = root.GetProperty("units").Deserialize<List<Unit>>(options)!;
        var aliases = root.GetProperty("aliases").Deserialize<Dictionary<string, string>>()!;
        var clears = root.GetProperty("clears");
        var samples = clears.GetProperty("samples").EnumerateArray().Select(s => new ClearSample(
            s[0].GetString()!, s[1].GetInt32(), s[2].EnumerateArray().Select(x => x.GetString()!).ToArray()));
        return new GameData(units, samples, aliases, root.GetProperty("mapVersion").GetString()!,
            clears.GetProperty("capturedAt").GetString()!,
            root.GetProperty("sources").Deserialize<List<string>>()!);
    }

    public Unit? Find(string id) => Units.GetValueOrDefault(id) ?? Units.GetValueOrDefault(Canonical(id));

    /// <summary>강화 폼 등 별칭 rawcode를 대표 코드로.</summary>
    public string Canonical(string id) => Aliases.GetValueOrDefault(id, id);

    /// <summary>패와 무관한 계산(클리어 기록 집계 등)을 키별로 한 번만.</summary>
    public T Memo<T>(string key, Func<T> make) where T : class => (T)_memo.GetOrAdd(key, _ => make());

    /// <summary>이 유닛을 재료로 쓰는 조합들 (TMO의 "상위 조합").</summary>
    public IReadOnlyList<Unit> UsedIn(string id) => _usedIn.GetValueOrDefault(id) ?? [];

    /// <summary>
    /// 흔함 환산 비용(추정). 조합식이 있으면 재료 비용의 합, 없으면 등급 가중치.
    /// ponytail: 등급 가중치는 어림값. 도박·퀘스트 실제 기대 비용을 알면 표로 교체.
    /// </summary>
    public double BaseCost(string id) => BaseCost(id, []);

    private double BaseCost(string id, HashSet<string> visiting)
    {
        if (_cost.TryGetValue(id, out var cached)) return cached;
        if (!Units.TryGetValue(id, out var unit) || !visiting.Add(id)) return 1;
        double cost;
        if (unit.IsWildcard) cost = unit.AnyOf.Min(option => BaseCost(option, visiting));
        else if (unit.HasRecipe) cost = unit.Recipe.Sum(i => i.Count * BaseCost(i.Id, visiting));
        else cost = unit.Grade switch { "흔함" => 1, "안흔함" => 2, "특별함" or "희귀함" or "랜덤유닛" => 4, _ => 3 };
        visiting.Remove(id);
        return _cost[id] = cost;
    }
}
