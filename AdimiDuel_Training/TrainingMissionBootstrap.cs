using System;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Training
{
    internal sealed class TrainingMissionBootstrap : MissionLogic
    {
        private readonly TrainingCasualtyHandler _casualties;

        internal TrainingMissionBootstrap(TrainingCasualtyHandler casualties)
        {
            _casualties = casualties ?? throw new ArgumentNullException(nameof(casualties));
        }

        public override void OnCreated()
        {
            base.OnCreated();
            // In 1.4.8, InitializeStartingBehaviors iterates a separate array,
            // and all OnBehaviorInitialize calls happen later. No team or
            // TeamQuerySystem may exist when its cached handler is replaced.
            var handlers = Mission.MissionBehaviors.OfType<CasualtyHandler>().ToArray();
            if (Mission.Teams.Any() || handlers.Length != 1 || handlers[0].GetType() != typeof(CasualtyHandler))
                throw new InvalidOperationException("训练场伤亡处理器初始化顺序不兼容，训练未启动。");
            Mission.RemoveMissionBehavior(handlers[0]);
            Mission.AddMissionBehavior(_casualties);
            if (!ReferenceEquals(Mission.GetMissionBehavior<CasualtyHandler>(), _casualties))
                throw new InvalidOperationException("无法安装训练场伤亡处理器，训练未启动。");
        }
    }
}
