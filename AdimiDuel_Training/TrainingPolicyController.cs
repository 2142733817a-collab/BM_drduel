using System;
using System.Collections.Generic;
using System.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Training
{
    // Optional component. It receives detached candidates on the mission
    // thread and never reads files, sockets, or Agent state from the callback.
    // Every actual component change is queued before mutating any input.
    internal sealed class TrainingPolicyController : AgentComponent
    {
        private const int MaxTraces = 128;
        private readonly object _gate = new object();
        private readonly int _agentId;
        private readonly Queue<TrainingComponentOutput> _outputs = new Queue<TrainingComponentOutput>();
        private Candidate _candidate;
        private Candidate _latched;
        private long _attackControlStartedTimestamp;
        private long _attackHoldUntilTimestamp;
        private bool _attackStartedWithoutCandidate;
        private bool _allowNewAttack;
        private bool _observedRelease;
        private bool _experimentalMovement;
        private long _suppressed;

        private sealed class Candidate
        {
            public string SessionId;
            public string EpisodeId;
            public long Sequence;
            public string ModelId;
            public string ModelSha256;
            public string Attack;
            public float? MoveX;
            public float? MoveY;
            public long ValidUntilTimestamp;
        }

        internal TrainingPolicyController(Agent agent, int agentId) : base(agent)
        {
            if (agentId < 0) throw new ArgumentOutOfRangeException("agentId");
            _agentId = agentId;
        }

        // releasing is sampled on the mission thread. A release-to-ready
        // transition starts a new swing even when native supplies no zero flag.
        internal bool Publish(TrainingPolicyDecision decision, bool allowNewAttack, bool releasing, bool experimentalMovement = false)
        {
            if (!ValidDecision(decision, _agentId)) { WithdrawCandidate(); return false; }
            var candidate = new Candidate { SessionId = decision.SessionId, EpisodeId = decision.EpisodeId,
                Sequence = decision.Sequence, ModelId = decision.ModelId, ModelSha256 = decision.ModelSha256,
                Attack = decision.Attack, MoveX = decision.MoveX, MoveY = decision.MoveY, ValidUntilTimestamp = decision.ValidUntilTimestamp };
            lock (_gate)
            {
                if ((_candidate != null && (_candidate.SessionId != candidate.SessionId || _candidate.EpisodeId != candidate.EpisodeId))
                    || (_latched != null && (_latched.SessionId != candidate.SessionId || _latched.EpisodeId != candidate.EpisodeId))) ResetSwing();
                if (_observedRelease && !releasing) ResetSwing();
                _observedRelease = releasing;
                _candidate = candidate;
                _allowNewAttack = allowNewAttack;
                _experimentalMovement = experimentalMovement;
                return true;
            }
        }

        // Called every mission tick even when the trainer has disconnected.
        // A release-to-ready transition ends the previous swing without
        // requiring a fresh proposal or refreshing any proposal's deadline.
        internal void ObserveAttackPhase(bool allowNewAttack, bool releasing)
        {
            lock (_gate)
            {
                if (_observedRelease && !releasing) ResetSwing();
                _observedRelease = releasing;
                _allowNewAttack = allowNewAttack && _candidate != null;
            }
        }

        // Existing traces remain available after clearing; the caller drains
        // them before finalizing the round so provenance is not lost.
        // Withdrawal forbids starting new actions while allowing a previously
        // changed attack to hold its direction until release or the hard limit.
        internal void WithdrawCandidate()
        {
            lock (_gate)
            {
                _candidate = null;
                _allowNewAttack = false;
                _experimentalMovement = false;
                if (_latched != null && _attackControlStartedTimestamp == 0)
                { ClearLatch(); _attackStartedWithoutCandidate = true; }
            }
        }

        // Explicit terminal, removal or shutdown cancels all control now.
        internal void Clear()
        {
            lock (_gate)
            {
                _candidate = null;
                _allowNewAttack = false;
                _experimentalMovement = false;
                if (_latched != null) _attackStartedWithoutCandidate = true;
                ClearLatch();
            }
        }

        internal int PendingOutputs { get { lock (_gate) return _outputs.Count; } }
        internal long SuppressedOutputs { get { lock (_gate) return _suppressed; } }

        internal TrainingComponentOutput[] DrainOutputs()
        {
            lock (_gate)
            {
                var result = new TrainingComponentOutput[_outputs.Count];
                for (int i = 0; i < result.Length; i++) result[i] = _outputs.Dequeue();
                return result;
            }
        }

        public override void OnAIInputSet(ref Agent.EventControlFlag eventFlag, ref Agent.MovementControlFlag movementFlag, ref Vec2 inputVector)
        {
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                if (_candidate != null && now > _candidate.ValidUntilTimestamp)
                {
                    _candidate = null;
                    _allowNewAttack = false;
                    _experimentalMovement = false;
                }
                if (_latched != null && _attackControlStartedTimestamp != 0 && now > _attackHoldUntilTimestamp)
                { ClearLatch(); _attackStartedWithoutCandidate = true; }
                if ((eventFlag & (Agent.EventControlFlag.Kick | Agent.EventControlFlag.Mount | Agent.EventControlFlag.Dismount
                        | Agent.EventControlFlag.Rear | Agent.EventControlFlag.Jump | Agent.EventControlFlag.ToggleAlternativeWeapon)) != 0
                    || (movementFlag & (Agent.MovementControlFlag.DefendMask | Agent.MovementControlFlag.DefendBlock)) != 0
                    || (movementFlag & Agent.MovementControlFlag.Action) != 0
                    || !Finite(inputVector.x) || !Finite(inputVector.y))
                {
                    if ((movementFlag & Agent.MovementControlFlag.AttackMask) == Agent.MovementControlFlag.None) ResetSwing();
                    else { ClearLatch(); _attackStartedWithoutCandidate = true; }
                    return;
                }

                Agent.MovementControlFlag attack = movementFlag & Agent.MovementControlFlag.AttackMask;
                uint bits = (uint)attack;
                if (bits != 0 && (bits & (bits - 1u)) != 0)
                { ClearLatch(); _attackStartedWithoutCandidate = true; return; }
                if (attack == Agent.MovementControlFlag.None) ResetSwing();
                else if (_latched == null && !_attackStartedWithoutCandidate)
                {
                    if (_allowNewAttack && _candidate != null) _latched = _candidate;
                    else _attackStartedWithoutCandidate = true;
                }

                Agent.MovementControlFlag outputMovement = movementFlag;
                Candidate selected = _candidate;
                if (attack != Agent.MovementControlFlag.None && _latched != null)
                {
                    // A decision can start an attack only before its original
                    // deadline. A direction already changed by this component
                    // may be held for that same swing, with a hard 1.5 s cap.
                    if (now <= _latched.ValidUntilTimestamp
                        || (_attackControlStartedTimestamp != 0 && now <= _attackHoldUntilTimestamp))
                    {
                        selected = _latched;
                        Agent.MovementControlFlag direction = Direction(selected.Attack);
                        if (direction != Agent.MovementControlFlag.None)
                            outputMovement = (movementFlag & ~Agent.MovementControlFlag.AttackMask) | direction;
                    }
                    else
                    {
                        ClearLatch();
                        _attackStartedWithoutCandidate = true;
                    }
                }

                float outputX = inputVector.x, outputY = inputVector.y;
                if (_experimentalMovement && selected != null && selected.MoveX.HasValue && selected.MoveY.HasValue
                    && now <= selected.ValidUntilTimestamp)
                { outputX = selected.MoveX.Value; outputY = selected.MoveY.Value; }

                if (outputMovement == movementFlag && outputX == inputVector.x && outputY == inputVector.y) return;
                if (_outputs.Count >= MaxTraces)
                {
                    _suppressed++;
                    if (outputMovement != movementFlag) { ClearLatch(); _attackStartedWithoutCandidate = true; }
                    return;
                }
                bool changedAttack = outputMovement != movementFlag;
                bool continuedAttack = changedAttack && _attackControlStartedTimestamp != 0;
                if (changedAttack && !continuedAttack)
                {
                    _attackControlStartedTimestamp = now;
                    long limit = Stopwatch.Frequency + Stopwatch.Frequency / 2L;
                    _attackHoldUntilTimestamp = now > long.MaxValue - limit ? long.MaxValue : now + limit;
                }
                var trace = new TrainingComponentOutput
                {
                    AgentId = _agentId, PolicySessionId = selected.SessionId, PolicyEpisodeId = selected.EpisodeId,
                    ObservationSequence = selected.Sequence, CallbackTimestamp = now, ModelId = selected.ModelId, ModelSha256 = selected.ModelSha256,
                    CandidateValidUntilTimestamp = selected.ValidUntilTimestamp,
                    AttackControlStartedTimestamp = _attackControlStartedTimestamp, ContinuedAttack = continuedAttack,
                    NativeEventFlags = (uint)eventFlag, NativeMovementFlags = (uint)movementFlag,
                    NativeInputX = inputVector.x, NativeInputY = inputVector.y,
                    ComponentEventFlags = (uint)eventFlag, ComponentMovementFlags = (uint)outputMovement,
                    ComponentInputX = outputX, ComponentInputY = outputY, ExperimentalMovement = _experimentalMovement
                };
                _outputs.Enqueue(trace);
                movementFlag = outputMovement;
                inputVector = new Vec2(outputX, outputY);
            }
        }

        private void ClearLatch() { _latched = null; _attackControlStartedTimestamp = 0; _attackHoldUntilTimestamp = 0; }
        private void ResetSwing() { ClearLatch(); _attackStartedWithoutCandidate = false; }

        private static Agent.MovementControlFlag Direction(string value)
        {
            switch (value)
            {
                case "up": return Agent.MovementControlFlag.AttackUp;
                case "down": return Agent.MovementControlFlag.AttackDown;
                case "left": return Agent.MovementControlFlag.AttackLeft;
                case "right": return Agent.MovementControlFlag.AttackRight;
                default: return Agent.MovementControlFlag.None;
            }
        }

        private static bool ValidDecision(TrainingPolicyDecision d, int agentId)
        {
            Guid parsed;
            if (d == null || d.AgentId != agentId || d.Sequence < 0 || d.ValidUntilTimestamp < Stopwatch.GetTimestamp()
                || !Guid.TryParseExact(d.SessionId, "N", out parsed) || parsed.ToString("N") != d.SessionId
                || !Guid.TryParseExact(d.EpisodeId, "N", out parsed) || parsed.ToString("N") != d.EpisodeId
                || string.IsNullOrEmpty(d.ModelId) || d.ModelId.Length > 128 || d.ModelSha256 == null || d.ModelSha256.Length != 64
                || (d.Attack != "native" && d.Attack != "up" && d.Attack != "down" && d.Attack != "left" && d.Attack != "right")
                || d.MoveX.HasValue != d.MoveY.HasValue) return false;
            foreach (char c in d.ModelId) if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')) return false;
            foreach (char c in d.ModelSha256) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return !d.MoveX.HasValue || (Finite(d.MoveX.Value) && Finite(d.MoveY.Value)
                && Math.Abs(d.MoveX.Value) <= 1f && Math.Abs(d.MoveY.Value) <= 1f);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
