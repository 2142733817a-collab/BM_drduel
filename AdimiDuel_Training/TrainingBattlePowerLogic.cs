using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Training
{
    // The original battle implementation expects a mission spawner and caches
    // a roster for the entire mission. Our roster changes each training round.
    internal sealed class TrainingBattlePowerLogic : MissionLogic, IBattlePowerCalculationLogic
    {
        private readonly object _gate = new object();
        private Team _a, _b;
        private float _powerA, _powerB;

        internal void BeginRound(Team a, BasicCharacterObject characterA, Team b, BasicCharacterObject characterB)
        {
            if (a == null || b == null || ReferenceEquals(a, b) || characterA == null || characterB == null)
                throw new InvalidOperationException("训练回合的队伍或兵种无效。");

            float powerA = characterA.GetPower(), powerB = characterB.GetPower();
            if (!ValidPower(powerA) || !ValidPower(powerB))
                throw new InvalidOperationException("原版兵种战力无效，无法开始训练回合。");
            lock (_gate) { _a = a; _b = b; _powerA = powerA; _powerB = powerB; }
        }

        public float GetTotalTeamPower(Team team)
        {
            lock (_gate)
            {
                if (team != null && ReferenceEquals(team, _a)) return _powerA;
                if (team != null && ReferenceEquals(team, _b)) return _powerB;
                return 0f;
            }
        }

        internal void EndRound()
        {
            lock (_gate) { _a = _b = null; _powerA = _powerB = 0f; }
        }

        private static bool ValidPower(float power) => power > 0f && !float.IsNaN(power) && !float.IsInfinity(power);
    }
}
