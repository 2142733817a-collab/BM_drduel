using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// The mission thread publishes a detached decision. Native may call the input
// hook on a worker thread, so that hook only reads guarded local scalar state.
internal sealed class AdimiToolsAiArenaLearnedAttack : AgentComponent
{
    private const double InterventionProbability = 0.25d;
    private readonly object _gate = new();
    private readonly AdimiToolsAiArenaAttackTactics? _tactics;
    private readonly AdimiToolsAiArenaHitCounter? _counter;
    private readonly AdimiToolsAiArenaParryCounter? _parry;
    private readonly AdimiToolsAiArenaKickPunish? _punish;
    private bool _enabled;
    private bool _allowNewAttack;
    private bool _latched;
    private bool _observedRelease;
    private Agent.MovementControlFlag _proposed;
    private Agent.MovementControlFlag _chosen;

    public AdimiToolsAiArenaLearnedAttack(Agent agent, AdimiToolsAiArenaAttackTactics? tactics = null,
        AdimiToolsAiArenaHitCounter? counter = null, AdimiToolsAiArenaParryCounter? parry = null,
        AdimiToolsAiArenaKickPunish? punish = null)
        : base(agent)
    {
        _tactics = tactics;
        _counter = counter;
        _parry = parry;
        _punish = punish;
    }

    // These random values are generated on the mission thread, never in the
    // native callback. Only a fresh attack may latch a newly proposed choice.
    public void Publish(AdimiToolsAiLearnedPolicy? policy, bool allowNewAttack, double interventionRoll, double directionRoll,
        bool releasing = false)
    {
        if (policy == null || policy.Samples <= 0 || !double.IsFinite(interventionRoll)
            || interventionRoll < 0d || interventionRoll >= 1d)
        {
            Clear();
            return;
        }

        Agent.MovementControlFlag proposed = Agent.MovementControlFlag.None;
        if (interventionRoll < InterventionProbability)
        {
            proposed = policy.PickDirection(directionRoll) switch
            {
                0 => Agent.MovementControlFlag.AttackUp,
                1 => Agent.MovementControlFlag.AttackDown,
                2 => Agent.MovementControlFlag.AttackLeft,
                3 => Agent.MovementControlFlag.AttackRight,
                _ => Agent.MovementControlFlag.None,
            };
        }

        lock (_gate)
        {
            // Some native input streams do not contain an all-zero attack
            // frame between two swings. A sampled release-to-ready transition
            // is also sufficient to start a fresh selection.
            if (_observedRelease && !releasing)
            {
                _latched = false;
                _chosen = Agent.MovementControlFlag.None;
            }

            _observedRelease = releasing;
            _enabled = true;
            _allowNewAttack = allowNewAttack;
            _proposed = proposed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _enabled = false;
            _allowNewAttack = false;
            _latched = false;
            _observedRelease = false;
            _proposed = Agent.MovementControlFlag.None;
            _chosen = Agent.MovementControlFlag.None;
        }
    }

    public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag,
        ref Vec2 inputVector)
    {
        if (_tactics?.OwnsAttackInput == true || _counter?.HasPendingOrControl == true
            || _parry?.HasPendingOrControl == true || _punish?.HasPendingOrControl == true)
        {
            return;
        }

        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
            if (attack == Agent.MovementControlFlag.None)
            {
                _latched = false;
                _chosen = Agent.MovementControlFlag.None;
                return;
            }

            uint bits = (uint)attack;
            if ((bits & (bits - 1u)) != 0
                || (movementFlag & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0
                || (eventFlag & Agent.EventControlFlag.Kick) != 0)
            {
                return;
            }

            if (!_latched)
            {
                if (!_allowNewAttack)
                {
                    return;
                }

                _chosen = _proposed;
                _latched = true;
            }

            if (_chosen != Agent.MovementControlFlag.None)
            {
                movementFlag = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | _chosen;
            }
        }
    }
}
