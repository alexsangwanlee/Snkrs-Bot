using System.Windows.Threading;
using OrdHelper.Core;

namespace OrdHelper.App;

/// <summary>
/// 자동 실행 (사용자가 켰을 때만). 지금 가능한 조합 단계를 하나씩 실행하고,
/// 결과 유닛이 패에 늘어난 것을 확인한 뒤 다음 단계로 간다.
/// - 채팅 조합: 워크3가 앞에 있으면 명령을 직접 입력한다 (선택 불필요).
/// - 단축키 조합: 표시된 유닛을 사용자가 클릭하면 조합 키를 누른다.
/// - 대상 지정형/기타: 직접 하도록 안내하고 패가 바뀌길 기다린다.
/// </summary>
public sealed class AutoRunner
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(2.5);
    private readonly AppState _state;
    private readonly Action _requestRefresh;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private MouseHook? _hook;
    private (CraftStep Step, int Before, DateTime Deadline)? _pending;
    private (CraftStep Step, CraftAction Action)? _awaitingClick;
    private IReadOnlyDictionary<string, int>? _plannedFor;
    private DateTime _retryAt;

    public bool Armed { get; private set; }
    public string Status { get; private set; } = "자동 실행 꺼짐";
    /// <summary>지금 사용자가 해야 할 일 (클릭할 유닛 등). 없으면 null.</summary>
    public CraftAction? Waiting => _awaitingClick?.Action;
    public event Action? Changed;

    public AutoRunner(AppState state, Action requestRefresh)
    {
        _state = state;
        _requestRefresh = requestRefresh;
        _timer.Tick += (_, _) => Tick();
    }

    public void Toggle()
    {
        Armed = !Armed;
        _pending = null;
        _awaitingClick = null;
        _plannedFor = null;
        _failures.Clear();
        if (Armed)
        {
            _hook = new MouseHook(OnClick);
            _timer.Start();
            Tick();
        }
        else
        {
            _hook?.Dispose();
            _hook = null;
            _timer.Stop();
            Report("자동 실행 꺼짐");
        }
    }

    /// <summary>패가 바뀌었거나 타이머: 확인 대기 중이면 결과를 보고, 아니면 다음 단계를 시작.</summary>
    public void Tick()
    {
        if (!Armed) return;
        var hand = _state.Hand;
        if (_pending is { } pending)
        {
            if (hand.GetValueOrDefault(pending.Step.Result.Id) > pending.Before)
            {
                _pending = null;
                _failures.Remove(pending.Step.Result.Id);
            }
            else if (DateTime.UtcNow < pending.Deadline) return;
            else
            {
                _pending = null;
                Fail(pending.Step);
                return;
            }
        }
        if (DateTime.UtcNow < _retryAt) return;
        if (!_state.IsLive)
        {
            Report("워크3에서 패를 읽는 중일 때만 실행합니다");
            return;
        }
        // 패가 그대로면 계획도 그대로다 (채팅 입력을 위해 워크3 창을 기다리는 경우만 다시 본다).
        if (ReferenceEquals(hand, _plannedFor)) return;
        _plannedFor = hand;
        var (goals, _) = _state.EffectiveGoals();
        var plan = Crafting.Plan(_state.Data, hand, goals);
        var step = plan.Steps.FirstOrDefault(s => s.Ready);
        _awaitingClick = null;
        if (step is null)
        {
            Report(goals.Count == 0 ? "목표가 없습니다" : plan.Steps.Count == 0 ? "목표 완성" : "재료가 모이길 기다리는 중");
            return;
        }
        var action = Crafting.Action(_state.Data, step);
        switch (action.Kind)
        {
            case ActionKind.Chat when Input.ForegroundWc3() == 0:
                _plannedFor = null;
                Report($"워크3 창을 앞에 두면 '{action.Text}' 를 입력합니다");
                break;
            case ActionKind.Chat:
                Begin(step);
                Input.Chat(action.Text);
                Report($"{step.Result.Name}: 채팅 '{action.Text}' 입력함");
                break;
            case ActionKind.Key:
                _awaitingClick = (step, action);
                Report($"{step.Result.Name}: [{action.Host?.Name}] 를 클릭하면 {action.Text} 를 누릅니다");
                break;
            default:
                Report($"{step.Result.Name}: 직접 조합해 주세요 ({Crafting.Instruction(_state.Data, step)})");
                break;
        }
    }

    /// <summary>사용자가 워크3 화면을 클릭함: 유닛이 선택될 틈을 두고 조합 키를 누른다.</summary>
    private async void OnClick(int x, int y)
    {
        if (!Armed || _pending is not null || _awaitingClick is not { } waiting) return;
        if (!Input.IsGameField(x, y)) return;
        await Task.Delay(120);
        if (!Armed || _awaitingClick?.Step.Result.Id != waiting.Step.Result.Id) return;
        Begin(waiting.Step);
        _awaitingClick = null;
        Input.Tap(waiting.Action.Text[0]);
        Report($"{waiting.Step.Result.Name}: {waiting.Action.Text} 누름, 확인 중");
    }

    private void Begin(CraftStep step) =>
        _pending = (step, _state.Hand.GetValueOrDefault(step.Result.Id), DateTime.UtcNow + ConfirmTimeout);

    /// <summary>확인 실패: 한 번은 연결을 새로고침하고 다시 시도, 같은 단계가 두 번 실패하면 멈춘다.</summary>
    private void Fail(CraftStep step)
    {
        var count = _failures[step.Result.Id] = _failures.GetValueOrDefault(step.Result.Id) + 1;
        if (count >= 2)
        {
            Toggle();
            Report($"{step.Result.Name} 조합이 두 번 확인되지 않아 멈췄습니다. 선택한 유닛·재료·조건을 확인하세요.");
            return;
        }
        _plannedFor = null;
        _retryAt = DateTime.UtcNow.AddSeconds(1.5); // 새로 읽은 패로 다시 판단할 틈
        _requestRefresh();
        Report($"{step.Result.Name} 조합이 확인되지 않아 새로고침 후 다시 시도합니다");
    }

    private void Report(string status)
    {
        if (status == Status) return;
        Status = status;
        Changed?.Invoke();
    }
}
