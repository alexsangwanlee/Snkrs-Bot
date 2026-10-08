namespace OrdHelper.Core;

/// <summary>조합 한 번. Uses는 선택 재료까지 실제 유닛으로 정해진 소모 재료.</summary>
public sealed record CraftStep(Unit Result, IReadOnlyList<Ingredient> Uses, bool Ready);

public sealed record CraftPlan(
    IReadOnlyList<CraftStep> Steps,
    IReadOnlyDictionary<string, int> Missing,
    int Gold,
    int Lumber,
    IReadOnlyList<string> Notes,
    double TotalCost,
    double MissingCost)
{
    public double Progress => TotalCost <= 0 ? 1 : Math.Clamp(1 - MissingCost / TotalCost, 0, 1);
    public bool Complete => Missing.Count == 0;
}

public static class Crafting
{
    /// <summary>지금 패로 바로 만들 수 있는가 (직접 재료만, 하위 조합 없이).</summary>
    public static bool CanCraft(GameData data, Unit unit, IReadOnlyDictionary<string, int> hand)
    {
        if (!unit.HasRecipe) return false;
        var stock = new Dictionary<string, int>(hand, StringComparer.Ordinal);
        // 정확한 재료부터 빼고 선택 재료는 남은 후보로 채운다.
        foreach (var ingredient in unit.Recipe.OrderBy(i => IsWildcard(data, i.Id) ? 1 : 0))
        {
            var need = ingredient.Count;
            foreach (var id in Candidates(data, ingredient.Id))
            {
                var take = Math.Min(need, stock.GetValueOrDefault(id));
                stock[id] = stock.GetValueOrDefault(id) - take;
                need -= take;
            }
            if (need > 0) return false;
        }
        return true;
    }

    public static IEnumerable<Unit> Craftable(GameData data, IReadOnlyDictionary<string, int> hand) =>
        data.Units.Values.Where(u => !u.IsWildcard && CanCraft(data, u, hand));

    /// <summary>
    /// 목표들을 만드는 조합 순서. 보유 유닛을 먼저 쓰고 모자라면 하위 조합을 펼친다.
    /// 단계는 하위→상위 순이며, Ready는 앞의 Ready 단계를 실행했다고 칠 때 지금 바로 가능한지.
    /// 이미 보유한 목표는 완료로 본다.
    /// </summary>
    public static CraftPlan Plan(GameData data, IReadOnlyDictionary<string, int> hand, IEnumerable<string> goals)
    {
        var stock = new Dictionary<string, int>(hand, StringComparer.Ordinal);
        var raw = new List<(Unit Unit, List<Ingredient> Uses)>();
        var missing = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new List<string>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int gold = 0, lumber = 0;

        List<Ingredient> Need(string id, int count)
        {
            var uses = new List<Ingredient>();
            if (!data.Units.TryGetValue(id, out var unit))
            {
                missing[id] = missing.GetValueOrDefault(id) + count;
                return [new Ingredient(id, count)];
            }
            foreach (var candidate in Candidates(data, id))
            {
                var take = Math.Min(count, stock.GetValueOrDefault(candidate));
                if (take == 0) continue;
                stock[candidate] -= take;
                count -= take;
                uses.Add(new Ingredient(candidate, take));
            }
            if (count == 0) return uses;
            if (unit.IsWildcard)
            {
                uses.AddRange(Need(unit.AnyOf.MinBy(data.BaseCost)!, count));
                return uses;
            }
            if (!unit.HasRecipe || !visiting.Add(id))
            {
                missing[id] = missing.GetValueOrDefault(id) + count;
                uses.Add(new Ingredient(id, count));
                return uses;
            }
            for (var i = 0; i < count; i++)
            {
                var stepUses = unit.Recipe.SelectMany(ingredient => Need(ingredient.Id, ingredient.Count)).ToList();
                raw.Add((unit, stepUses));
                gold += unit.Gold;
                lumber += unit.Lumber;
                foreach (var note in unit.Notes.Select(note => $"{unit.Name}: {note}"))
                    if (!notes.Contains(note)) notes.Add(note);
            }
            visiting.Remove(id);
            uses.Add(new Ingredient(id, count));
            return uses;
        }

        var goalList = goals.Distinct().ToList();
        foreach (var goal in goalList) Need(goal, 1);

        // 실행 가능 여부 시뮬레이션: 앞 단계 결과물을 다음 단계가 쓴다.
        var sim = new Dictionary<string, int>(hand, StringComparer.Ordinal);
        var steps = new List<CraftStep>();
        foreach (var (unit, uses) in raw)
        {
            var merged = uses.GroupBy(u => u.Id).Select(g => new Ingredient(g.Key, g.Sum(x => x.Count))).ToList();
            var ready = merged.All(u => sim.GetValueOrDefault(u.Id) >= u.Count);
            if (ready)
            {
                foreach (var use in merged) sim[use.Id] -= use.Count;
                sim[unit.Id] = sim.GetValueOrDefault(unit.Id) + 1;
            }
            steps.Add(new CraftStep(unit, merged, ready));
        }

        return new CraftPlan(steps, missing, gold, lumber, notes,
            goalList.Sum(data.BaseCost),
            missing.Sum(pair => pair.Value * data.BaseCost(pair.Key)));
    }

    /// <summary>단계 실행 안내: 어떤 유닛을 선택하고 어떤 키(또는 채팅)를 쓰는지.</summary>
    public static string Instruction(GameData data, CraftStep step)
    {
        var unit = step.Result;
        if (unit.Key.Length > 0)
        {
            var host = unit.Hosts.FirstOrDefault(h => step.Uses.Any(u => u.Id == h))
                       ?? unit.Hosts.FirstOrDefault() ?? step.Uses.FirstOrDefault()?.Id;
            var hostName = host is null ? "재료" : data.Find(host)?.Name ?? host;
            return $"[{hostName}] 선택 → {unit.Key}";
        }
        if (unit.Commands.Count > 0) return $"채팅: {unit.Commands[0]}";
        return "게임 내 조합 메뉴에서 선택";
    }

    private static bool IsWildcard(GameData data, string id) =>
        data.Units.TryGetValue(id, out var unit) && unit.IsWildcard;

    private static IEnumerable<string> Candidates(GameData data, string id) =>
        data.Units.TryGetValue(id, out var unit) && unit.IsWildcard ? unit.AnyOf : [id];
}
