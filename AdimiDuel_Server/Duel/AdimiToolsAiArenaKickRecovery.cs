using System;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Read only on the mission thread. Native owns the action, its timing and
// collision; a match identifies one specific Native miss animation, not a
// general permission to interrupt or an assertion that all misses use it.
internal static class AdimiToolsAiArenaKickRecovery
{
    private const string MissStepBackAction = "act_kick_right_leg_continue_step_back";
    private static int _expectedActionIndex = -1;

    internal static bool IsMissStepBack(Agent? target)
    {
        if (target == null)
        {
            return false;
        }

        var reader = new NativeActionReader(target);
        return IsMissStepBack(ref reader);
    }

    // A small reader boundary permits deterministic tests without entering the
    // native engine. Each read still uses a single action-index snapshot.
    internal interface IActionReader
    {
        Agent.ActionCodeType GetActionType(int channel);
        int CaptureActionIndex(int channel);
        int GetExpectedActionIndex();
        string? GetCapturedActionName();
    }

    internal static bool IsMissStepBack<TReader>(ref TReader reader)
        where TReader : struct, IActionReader
    {
        try
        {
            Agent.ActionCodeType lower = reader.GetActionType(0);
            Agent.ActionCodeType upper = reader.GetActionType(1);
            if (IsActiveAlternativeAttack(lower) || IsActiveAlternativeAttack(upper))
            {
                return false;
            }

            return IsMissStepBackChannel(ref reader, 0, lower) || IsMissStepBackChannel(ref reader, 1, upper);
        }
        catch (Exception)
        {
            // Native calls can fail while a mission is changing. Do not turn
            // an incomplete observation into a confirmed missed kick.
            return false;
        }
    }

    private static bool IsActiveAlternativeAttack(Agent.ActionCodeType action)
    {
        return action == Agent.ActionCodeType.Kick || action == Agent.ActionCodeType.KickHit
            || action == Agent.ActionCodeType.WeaponBash;
    }

    private static bool IsMissStepBackChannel<TReader>(ref TReader reader, int channel, Agent.ActionCodeType action)
        where TReader : struct, IActionReader
    {
        if (action != Agent.ActionCodeType.KickContinue)
        {
            return false;
        }

        int currentIndex = reader.CaptureActionIndex(channel);
        if (currentIndex < 0)
        {
            return false;
        }

        int expectedIndex = reader.GetExpectedActionIndex();
        return expectedIndex >= 0
            && currentIndex == expectedIndex
            && string.Equals(reader.GetCapturedActionName(), MissStepBackAction, StringComparison.Ordinal);
    }

    private struct NativeActionReader : IActionReader
    {
        private readonly Agent _agent;
        private ActionIndexCache _capturedAction;

        internal NativeActionReader(Agent agent)
        {
            _agent = agent;
            _capturedAction = default;
        }

        public Agent.ActionCodeType GetActionType(int channel) => _agent.GetCurrentActionType(channel);

        public int CaptureActionIndex(int channel)
        {
            _capturedAction = _agent.GetCurrentAction(channel);
            return _capturedAction.Index;
        }

        public int GetExpectedActionIndex()
        {
            // This lookup is deferred until a valid KickContinue is observed,
            // and cached only after Native resolves it successfully. Native
            // action-code indexes are assumed stable for this game process.
            if (_expectedActionIndex < 0)
            {
                int resolved = ActionIndexCache.Create(MissStepBackAction).Index;
                if (resolved >= 0)
                {
                    _expectedActionIndex = resolved;
                }
            }

            return _expectedActionIndex;
        }

        public string? GetCapturedActionName() => _capturedAction.GetName();
    }
}
