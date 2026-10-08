namespace OrdHelper.Core;

/// <summary>상위 유닛 하나를 목표로 했을 때의 기록·비용·효율.</summary>
public sealed record GoalRow(Unit Goal, int Games, int Yuka0, double AvgYuka, double Yuka0Rate, CraftPlan Plan,
    double Efficiency);

public sealed record SupportUnit(Unit Unit, double Share);

/// <summary>클리어 기록에서 상위 유닛 구성이 같은 판들의 묶음.</summary>
public sealed record BuildRow(IReadOnlyList<Unit> Core, int Games, int Yuka0, double AvgYuka, double Yuka0Rate,
    IReadOnlyList<SupportUnit> Support, CraftPlan Plan)
{
    public string Name => string.Join(" + ", Core.Select(u => u.Name));
}

public static class Insights
{
    public const string AllDifficulties = "전체";
    public static readonly string[] Difficulties = [AllDifficulties, "신", "악몽", "지옥"];
    private static readonly HashSet<string> TopGrades = ["초월", "불멸", "영원", "신비함", "신비", "제한됨"];

    // 판수가 적은 조합이 우연히 100%로 뜨지 않게 전체 평균 쪽으로 당기는 가상 판수.
    private const int PriorGames = 20;

    private static readonly CraftPlan EmptyPlan = new([], new Dictionary<string, int>(), 0, 0, [], 0, 0);

    public static bool IsTop(Unit unit) => TopGrades.Contains(unit.Grade);

    public static IReadOnlyList<ClearSample> Filter(GameData data, string difficulty) =>
        difficulty == AllDifficulties ? data.Samples : data.Samples.Where(s => s.Difficulty == difficulty).ToList();

    private static double Prior(IReadOnlyList<ClearSample> samples) =>
        samples.Count == 0 ? 0 : samples.Count(s => s.Yuka == 0) / (double)samples.Count;

    private static double Smoothed(int zero, int games, double prior) =>
        (zero + PriorGames * prior) / (games + PriorGames);

    /// <summary>
    /// 효율 = 유카0 확률 ÷ (남은 흔함 환산 비용 + 1), 최고값을 100으로 맞춘 점수.
    /// 강하면서 지금 패에서 적게 더 모으면 되는 목표가 위로 온다. 보유 중인 목표는 뺀다.
    /// </summary>
    public static List<GoalRow> Efficiency(GameData data, IReadOnlyDictionary<string, int> hand,
        string difficulty = AllDifficulties, int minGames = 20)
    {
        var (prior, stats) = data.Memo($"unit-stats:{difficulty}", () =>
        {
            var samples = Filter(data, difficulty);
            var table = new Dictionary<string, (int Games, int Zero, long Yuka)>(StringComparer.Ordinal);
            foreach (var sample in samples)
            foreach (var code in sample.Units)
            {
                var t = table.GetValueOrDefault(code);
                table[code] = (t.Games + 1, t.Zero + (sample.Yuka == 0 ? 1 : 0), t.Yuka + sample.Yuka);
            }
            return Tuple.Create(Prior(samples), table);
        });

        var rows = new List<GoalRow>();
        foreach (var unit in data.Units.Values.Where(u => IsTop(u) && u.HasRecipe && hand.GetValueOrDefault(u.Id) == 0))
        {
            if (!stats.TryGetValue(data.Canonical(unit.Id), out var s) || s.Games < minGames) continue;
            var plan = Crafting.Plan(data, hand, [unit.Id]);
            var rate = Smoothed(s.Zero, s.Games, prior);
            rows.Add(new GoalRow(unit, s.Games, s.Zero, s.Yuka / (double)s.Games, rate, plan,
                rate / (plan.MissingCost + 1)));
        }
        var best = rows.Count == 0 ? 1 : rows.Max(r => r.Efficiency);
        return rows.Select(r => r with { Efficiency = best <= 0 ? 0 : 100 * r.Efficiency / best })
            .OrderByDescending(r => r.Efficiency).ToList();
    }

    /// <summary>
    /// 유카0을 볼 수 있는 조합: 클리어 기록을 상위 유닛 구성으로 묶어 유카0 확률 순으로.
    /// Support는 그 구성의 유카0 판에서 자주 같이 쓴 나머지 유닛.
    /// </summary>
    public static List<BuildRow> Yuka0Builds(GameData data, IReadOnlyDictionary<string, int> hand,
        string difficulty = AllDifficulties, int minGames = 20, int supportCount = 8)
    {
        var builds = data.Memo($"builds:{difficulty}:{minGames}:{supportCount}", () =>
        {
            var samples = Filter(data, difficulty);
            var prior = Prior(samples);
            var list = new List<BuildRow>();
            var groups = samples.GroupBy(s => string.Join(",", s.Units
                .Where(c => data.Units.TryGetValue(c, out var u) && IsTop(u)).Order(StringComparer.Ordinal)));
            foreach (var group in groups)
            {
                var games = group.ToList();
                if (group.Key.Length == 0 || games.Count < minGames) continue;
                var coreIds = group.Key.Split(',');
                var zero = games.Where(s => s.Yuka == 0).ToList();
                var basis = zero.Count >= 5 ? zero : games;
                var support = basis.SelectMany(s => s.Units).Where(c => !coreIds.Contains(c))
                    .GroupBy(c => c).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Take(supportCount)
                    .Select(g => new SupportUnit(data.Units[g.Key], g.Count() / (double)basis.Count)).ToList();
                list.Add(new BuildRow(coreIds.Select(c => data.Units[c]).ToList(), games.Count, zero.Count,
                    games.Average(s => s.Yuka), Smoothed(zero.Count, games.Count, prior), support, EmptyPlan));
            }
            return list;
        });
        var rows = builds.Select(b => b with { Plan = Crafting.Plan(data, hand, b.Core.Select(u => u.Id)) }).ToList();
        return rows.OrderByDescending(r => r.Yuka0Rate).ThenByDescending(r => r.Games).ToList();
    }
}
