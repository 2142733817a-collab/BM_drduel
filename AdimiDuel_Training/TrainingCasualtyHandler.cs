using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Training
{
    // TeamQuerySystem caches a CasualtyHandler reference and the 1.4.8 class
    // offers no reset API. Use one instance for the mission, validate its two
    // fields strictly, and clear those existing dictionaries at round boundaries.
    internal sealed class TrainingCasualtyHandler : CasualtyHandler
    {
        private sealed class TrackedActor
        {
            internal Formation Formation;
            internal float Power;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<Agent, TrackedActor> _actors = new Dictionary<Agent, TrackedActor>();
        private readonly HashSet<Agent> _counted = new HashSet<Agent>();
        private readonly Queue<TrackedActor> _pending = new Queue<TrackedActor>();
        private readonly Dictionary<Formation, int> _counts;
        private readonly Dictionary<Formation, float> _losses;
        private bool _accept;

        internal TrainingCasualtyHandler()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            FieldInfo counts = typeof(CasualtyHandler).GetField("_casualtyCounts", flags);
            FieldInfo losses = typeof(CasualtyHandler).GetField("_powerLoss", flags);
            if (counts == null || counts.FieldType != typeof(Dictionary<Formation, int>) ||
                losses == null || losses.FieldType != typeof(Dictionary<Formation, float>))
                throw new InvalidOperationException("当前游戏版本的原版伤亡统计不兼容，训练未启动。");
            _counts = counts.GetValue(this) as Dictionary<Formation, int>;
            _losses = losses.GetValue(this) as Dictionary<Formation, float>;
            if (_counts == null || _losses == null)
                throw new InvalidOperationException("无法初始化原版伤亡统计，训练未启动。");
        }

        internal void BeginRound()
        {
            lock (_gate)
            {
                _accept = false;
                _actors.Clear(); _counted.Clear(); _pending.Clear();
                _counts.Clear(); _losses.Clear();
                _accept = true;
            }
        }

        internal void Track(Agent agent, Formation formation, float power)
        {
            if (agent == null || formation == null || power <= 0f || float.IsNaN(power) || float.IsInfinity(power))
                throw new InvalidOperationException("无法登记训练角色的回合战力。");
            lock (_gate)
            {
                if (!_accept || _actors.ContainsKey(agent)) throw new InvalidOperationException("训练角色重复登记。");
                _actors.Add(agent, new TrackedActor { Formation = formation, Power = power });
            }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState state, KillingBlow blow)
        {
            // Do not read engine objects or mutate the base dictionaries from a callback.
            QueueLoss(affectedAgent);
        }

        public override void OnAgentFleeing(Agent affectedAgent) { QueueLoss(affectedAgent); }

        private void QueueLoss(Agent agent)
        {
            if (agent == null) return;
            lock (_gate)
            {
                TrackedActor actor;
                if (_accept && _actors.TryGetValue(agent, out actor) && _counted.Add(agent)) _pending.Enqueue(actor);
            }
        }

        // Mission.OnPreTick waits for the parallel tick before calling behaviors.
        // Apply queued values here so native query code reads stable dictionaries.
        internal bool FlushPending()
        {
            bool changed = false;
            lock (_gate)
            {
                while (_pending.Count > 0)
                {
                    TrackedActor actor = _pending.Dequeue();
                    int count; float loss;
                    _counts.TryGetValue(actor.Formation, out count);
                    _losses.TryGetValue(actor.Formation, out loss);
                    _counts[actor.Formation] = count + 1;
                    _losses[actor.Formation] = loss + actor.Power;
                    changed = true;
                }
            }
            return changed;
        }

        internal void EndRound()
        {
            lock (_gate)
            {
                _accept = false;
                _actors.Clear(); _counted.Clear(); _pending.Clear();
                _counts.Clear(); _losses.Clear();
            }
        }
    }
}
