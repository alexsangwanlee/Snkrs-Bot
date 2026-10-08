using OrdHelper.Core;
using Xunit;

namespace OrdHelper.Core.Tests;

public class CoreTests
{
    // 흔함 a,b,c / 안흔함 ab = a+b / 전설 top = ab + c + (x 또는 y) / 와일드카드 any = x|y
    private static GameData Sample(IEnumerable<ClearSample>? samples = null) => new(
    [
        new Unit { Id = "a", Name = "A", Grade = "흔함" },
        new Unit { Id = "b", Name = "B", Grade = "흔함" },
        new Unit { Id = "c", Name = "C", Grade = "흔함" },
        new Unit { Id = "x", Name = "X", Grade = "흔함" },
        new Unit { Id = "y", Name = "Y", Grade = "흔함" },
        new Unit { Id = "any", Name = "X 또는 Y", Grade = "선택 재료", AnyOf = ["x", "y"] },
        new Unit { Id = "ab", Name = "AB", Grade = "안흔함", Recipe = [new("a", 1), new("b", 1)], Key = "Z", Hosts = ["a"] },
        new Unit { Id = "top", Name = "TOP", Grade = "초월", Recipe = [new("ab", 1), new("c", 2), new("any", 1)],
            Commands = ["top tr"], Lumber = 5 },
        new Unit { Id = "top2", Name = "TOP2", Grade = "초월", Recipe = [new("ab", 2)] },
    ], samples);

    private static Dictionary<string, int> Hand(params (string Id, int Count)[] units) =>
        units.ToDictionary(u => u.Id, u => u.Count);

    [Fact]
    public void CanCraftUsesWildcardOptions()
    {
        var data = Sample();
        var top = data.Units["top"];
        Assert.True(Crafting.CanCraft(data, top, Hand(("ab", 1), ("c", 2), ("y", 1))));
        Assert.False(Crafting.CanCraft(data, top, Hand(("ab", 1), ("c", 1), ("y", 1))));
        Assert.False(Crafting.CanCraft(data, top, Hand(("ab", 1), ("c", 2))));
        Assert.Equal(["ab"], Crafting.Craftable(data, Hand(("a", 1), ("b", 1))).Select(u => u.Id));
    }

    [Fact]
    public void PlanOrdersStepsAndMarksReady()
    {
        var data = Sample();
        var plan = Crafting.Plan(data, Hand(("a", 1), ("b", 1), ("c", 2), ("x", 1)), ["top"]);
        Assert.Equal(["ab", "top"], plan.Steps.Select(s => s.Result.Id));
        Assert.All(plan.Steps, s => Assert.True(s.Ready));
        Assert.Contains(new Ingredient("x", 1), plan.Steps[1].Uses);
        Assert.True(plan.Complete);
        Assert.Equal(1, plan.Progress);
        Assert.Equal(5, plan.Lumber);
        Assert.Equal("[A] 선택 → Z", Crafting.Instruction(data, plan.Steps[0]));
        Assert.Equal("채팅: top tr", Crafting.Instruction(data, plan.Steps[1]));
    }

    [Fact]
    public void PlanReportsMissingAndBlocksDependentSteps()
    {
        var data = Sample();
        var plan = Crafting.Plan(data, Hand(("a", 2), ("b", 1)), ["top2"]);
        Assert.Equal(["ab", "ab", "top2"], plan.Steps.Select(s => s.Result.Id));
        Assert.Equal([true, false, false], plan.Steps.Select(s => s.Ready));
        Assert.Equal(new Dictionary<string, int> { ["b"] = 1 }, plan.Missing);
        Assert.Equal(1 - 1.0 / 4, plan.Progress);
    }

    [Fact]
    public void OwnedGoalIsAlreadyDone()
    {
        var plan = Crafting.Plan(Sample(), Hand(("top", 1)), ["top"]);
        Assert.Empty(plan.Steps);
        Assert.True(plan.Complete);
    }

    [Fact]
    public void Yuka0BuildsGroupByTopUnits()
    {
        var data = Sample(
        [
            new("신", 0, ["a", "top"]),
            new("신", 0, ["b", "top"]),
            new("신", 7, ["c", "top"]),
            new("신", 3, ["top2"]),
            new("지옥", 0, ["a"]),
        ]);
        var builds = Insights.Yuka0Builds(data, Hand(), minGames: 1);
        Assert.Equal(["TOP", "TOP2"], builds.Select(b => b.Name));
        Assert.Equal((3, 2), (builds[0].Games, builds[0].Yuka0));
        Assert.True(builds[0].Yuka0Rate > builds[1].Yuka0Rate);
        Assert.Empty(Insights.Yuka0Builds(data, Hand(), "지옥", minGames: 1));
    }

    [Fact]
    public void EfficiencyPrefersCloserGoalsAndSkipsOwned()
    {
        var data = Sample([new("신", 0, ["top"]), new("신", 0, ["top2"])]);
        var rows = Insights.Efficiency(data, Hand(("ab", 2)), minGames: 1);
        Assert.Equal("top2", rows[0].Goal.Id);
        Assert.Equal(100, rows[0].Efficiency);
        Assert.DoesNotContain(Insights.Efficiency(data, Hand(("top2", 1)), minGames: 1), r => r.Goal.Id == "top2");
    }

    [Fact]
    public void BundledDataIsConsistent()
    {
        var data = GameData.Load(Path.Combine(AppContext.BaseDirectory, "Data", "ord-data.json"));
        Assert.True(data.Units.Count > 250);
        Assert.True(data.Samples.Count > 30000);
        Assert.All(data.Units.Values.SelectMany(u => u.Recipe), i => Assert.True(data.Units.ContainsKey(i.Id), i.Id));
        // 에이스(안흔함) = 루피 + 해군 총병, 루피 선택 후 Z
        var ace = data.Units["K00h"];
        Assert.Equal(["300h", "900h"], ace.Recipe.Select(i => i.Id).Order());
        Assert.Equal(("Z", "300h"), (ace.Key, ace.Hosts[0]));
        Assert.Equal("H90H", data.Canonical("G90H"));
        Assert.True(data.Units["T80H"].IsWildcard);

        var builds = Insights.Yuka0Builds(data, new Dictionary<string, int>());
        Assert.NotEmpty(builds);
        Assert.All(builds, b => Assert.InRange(b.Yuka0Rate, 0, 1));
        var rows = Insights.Efficiency(data, new Dictionary<string, int>());
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.False(r.Plan.Complete));
    }
}

public class RttiTests
{
    [Fact]
    public void FindsVftableThroughTypeDescriptorAndLocator()
    {
        const ulong moduleBase = 0x140000000;
        var image = new byte[0x200];
        System.Text.Encoding.ASCII.GetBytes(".?AVCUnit@@\0").CopyTo(image, 0x50); // 타입 디스크립터 0x40 + 0x10
        BitConverter.GetBytes(1u).CopyTo(image, 0x100);       // COL signature
        BitConverter.GetBytes(0x40u).CopyTo(image, 0x10C);    // 타입 디스크립터 RVA
        BitConverter.GetBytes(0x100u).CopyTo(image, 0x114);   // COL 자기 RVA
        BitConverter.GetBytes(moduleBase + 0x100).CopyTo(image, 0x180); // vftable[-1] = COL

        Assert.Equal([moduleBase + 0x188], Rtti.FindClassVftables(image, moduleBase, ".?AVCUnit@@"));
        Assert.Empty(Rtti.FindClassVftables(image, moduleBase, ".?AVCItem@@"));
        Assert.Equal("300h", Rtti.FormatRawcode(0x68303033));
    }
}
