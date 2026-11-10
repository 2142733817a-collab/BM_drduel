using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Training
{
    internal interface ITrainingPolicyMailbox
    {
        bool Publish(string episodeId, long sequence, double gameSeconds, TrainingPolicyActor[] actors);
        bool TryGetDecision(string episodeId, int agentId, out TrainingPolicyDecision decision);
        void InvalidateCandidates();
        bool PublishTerminal(string episodeId, long sequence, double gameSeconds, int? winningSide,
            string reason, bool terminated, bool truncated);
        void Dispose();
    }

    internal interface ITrainingPolicyTraceSink
    {
        bool Record(double now, TrainingComponentOutput output);
        void MarkIncomplete();
    }

    internal sealed class TrainingMailboxAdapter : ITrainingPolicyMailbox
    {
        private readonly TrainingPolicyBridge _bridge;
        internal TrainingMailboxAdapter(TrainingPolicyBridge bridge) { _bridge = bridge; }
        public bool Publish(string episodeId, long sequence, double gameSeconds, TrainingPolicyActor[] actors)
        { return _bridge.Publish(episodeId, sequence, gameSeconds, actors); }
        public bool TryGetDecision(string episodeId, int agentId, out TrainingPolicyDecision decision)
        { return _bridge.TryGetDecision(episodeId, agentId, out decision); }
        public void InvalidateCandidates() { _bridge.InvalidateCandidates(); }
        public bool PublishTerminal(string episodeId, long sequence, double gameSeconds, int? winningSide,
            string reason, bool terminated, bool truncated)
        { return _bridge.PublishTerminal(episodeId, sequence, gameSeconds, winningSide, reason, terminated, truncated); }
        public void Dispose() { _bridge.Dispose(); }
    }

    internal sealed class TrainingTraceSinkAdapter : ITrainingPolicyTraceSink
    {
        private readonly TrainingEpisodeRecorder _recorder;
        internal TrainingTraceSinkAdapter(TrainingEpisodeRecorder recorder) { _recorder = recorder; }
        public bool Record(double now, TrainingComponentOutput output) { return _recorder.RecordComponentOutput(now, output); }
        public void MarkIncomplete() { _recorder.MarkPolicyTraceIncomplete(); }
    }

    internal sealed class TrainingPolicyPhase
    {
        internal int AgentId;
        internal bool IsPlayer;
        internal bool IsAi;
        internal bool? IsMounted;
        internal Agent.ActionCodeType Action;
        internal Agent.ActionStage Stage;
    }

    // All methods are called while the mission owns its lifecycle lock. The
    // AI input callback holds only the individual component's scalar lock.
    internal sealed class TrainingPolicySession : IDisposable
    {
        private readonly ITrainingPolicyMailbox _mailbox;
        private readonly ITrainingPolicyTraceSink _sink;
        private readonly Dictionary<int, TrainingPolicyController> _controllers = new Dictionary<int, TrainingPolicyController>();
        private string _episodeId;
        private long _sequence;
        private bool _active, _published, _canApply, _disabled, _disposed;
        private string _lastError;

        internal TrainingPolicySession(TrainingEpisodeRecorder recorder, TrainingPolicyBridge bridge)
            : this(new TrainingTraceSinkAdapter(recorder), new TrainingMailboxAdapter(bridge)) { }

        internal TrainingPolicySession(ITrainingPolicyTraceSink sink, ITrainingPolicyMailbox mailbox)
        { _sink = sink ?? throw new ArgumentNullException("sink"); _mailbox = mailbox ?? throw new ArgumentNullException("mailbox"); }

        internal string LastError { get { return _lastError; } }
        internal int ControllerCount { get { return _controllers.Count; } }
        internal static bool IsEnabled(string value) { return string.Equals(value, "1", StringComparison.Ordinal); }

        internal static TrainingPolicyActor[] ConvertObservations(IList<TrainingActorSnapshot> snapshots)
        {
            var actors = new TrainingPolicyActor[snapshots.Count];
            for (int i = 0; i < snapshots.Count; i++)
            {
                TrainingActorSnapshot a = snapshots[i];
                actors[i] = new TrainingPolicyActor { AgentId = a.AgentId, Side = a.Side, IsPlayer = a.IsPlayer, IsMounted = a.IsMounted,
                    X = a.X, Y = a.Y, Z = a.Z, LookX = a.LookX, LookY = a.LookY,
                    VelocityX = a.VelocityX, VelocityY = a.VelocityY, Health = a.Health, HealthLimit = a.HealthLimit,
                    ActionType = a.ActionType, ActionStage = a.ActionStage, ActionDirection = a.ActionDirection,
                    ActionProgress = a.ActionProgress, WeaponClass = a.WeaponClass };
            }
            return actors;
        }

        internal bool BeginRound(string episodeId)
        {
            Guid parsed;
            if (_disposed || _active || !Guid.TryParseExact(episodeId, "N", out parsed) || parsed.ToString("N") != episodeId) return false;
            _episodeId = episodeId;
            _sequence = -1;
            _active = true;
            _published = false;
            _canApply = false;
            _disabled = false;
            _controllers.Clear();
            return true;
        }

        // Check both the declared human identity and the actual controller.
        // A failed attachment is never published a candidate.
        internal bool Register(Agent agent, bool isPlayer)
        {
            TrainingPolicyController controller = null;
            try
            {
                if (!_active || _disabled || agent == null || isPlayer || agent.Controller != AgentControllerType.AI || agent.HasMount
                    || agent.Index < 0 || _controllers.ContainsKey(agent.Index)) return false;
                controller = new TrainingPolicyController(agent, agent.Index);
                agent.AddComponent(controller);
                // AddComponent alone does not enable the native AI-input
                // callback. Never turn it off: other components may need it.
                if (!agent.GetHasOnAiInputSetCallback()) agent.SetHasOnAiInputSetCallback(true);
                if (!agent.GetHasOnAiInputSetCallback()) throw new InvalidOperationException("AI input callback unavailable");
                _controllers.Add(agent.Index, controller);
                return true;
            }
            catch (Exception error)
            {
                if (controller != null) controller.Clear();
                _lastError = "policy component attachment: " + error.GetType().Name;
                return false;
            }
        }

        internal bool PublishObservation(double now, TrainingPolicyActor[] actors)
        {
            if (!_active || _disabled || _disposed || _sequence == long.MaxValue) return false;
            try
            {
                bool unmounted = actors != null && actors.Length > 0;
                if (unmounted)
                    foreach (TrainingPolicyActor actor in actors)
                        if (actor == null || actor.IsMounted != false) { unmounted = false; break; }
                if (!unmounted) SuspendForMountedActor();
                if (_disabled) return false;
                bool accepted = _mailbox.Publish(_episodeId, ++_sequence, now, actors);
                _canApply = accepted && unmounted;
                if (accepted) _published = true;
                else ClearControllers();
                return accepted;
            }
            catch (Exception error) { FailClosed(error); return false; }
        }

        internal void Tick(double now, TrainingPolicyPhase[] phases)
        {
            if (!_active || _disposed) return;
            try
            {
                var lookup = new Dictionary<int, TrainingPolicyPhase>();
                bool unmounted = phases != null && phases.Length > 0;
                if (phases != null)
                    foreach (TrainingPolicyPhase phase in phases)
                    {
                        if (phase == null || phase.IsMounted != false) unmounted = false;
                        if (phase != null && !lookup.ContainsKey(phase.AgentId)) lookup.Add(phase.AgentId, phase);
                    }
                if (!unmounted) { SuspendForMountedActor(); Drain(now); return; }
                foreach (var pair in _controllers)
                {
                    TrainingPolicyController controller = pair.Value;
                    TrainingPolicyPhase phase;
                    if (_disabled || !lookup.TryGetValue(pair.Key, out phase) || phase.IsPlayer || !phase.IsAi
                        || !SafePhase(phase.Action, phase.Stage))
                    { controller.Clear(); continue; }
                    bool releasing = phase.Action == Agent.ActionCodeType.ReleaseMelee || phase.Stage == Agent.ActionStage.AttackRelease;
                    bool allowNew = !releasing;
                    controller.ObserveAttackPhase(allowNew, releasing);
                    TrainingPolicyDecision decision;
                    if (_canApply && _mailbox.TryGetDecision(_episodeId, pair.Key, out decision)
                        && decision != null && decision.EpisodeId == _episodeId && decision.AgentId == pair.Key)
                        controller.Publish(decision, allowNew, releasing, false);
                    else controller.WithdrawCandidate();
                }
                Drain(now);
            }
            catch (Exception error) { FailClosed(error); DrainSafely(now); }
        }

        internal void RemovedOrDeleted(int agentId)
        {
            TrainingPolicyController controller;
            if (_controllers.TryGetValue(agentId, out controller)) controller.Clear();
        }

        // Invalidate all old observations as well as latched inputs: a mount
        // event can arrive between 50 ms snapshots, and a quick dismount must
        // not revive an old on-foot candidate.
        internal void SuspendForMountedActor()
        {
            _canApply = false;
            try { ClearControllers(); _mailbox.InvalidateCandidates(); }
            catch (Exception error) { FailClosed(error); }
        }

        internal bool EndRound(double now, int? winner, string reason, bool interrupted)
        {
            if (!_active || _disposed) return false;
            bool terminal = !_published;
            try
            {
                ClearControllers();
                Drain(now);
                if (_published && _sequence < long.MaxValue)
                    terminal = _mailbox.PublishTerminal(_episodeId, ++_sequence, now,
                        interrupted ? null : winner, reason, !interrupted, interrupted);
                if (!terminal) _lastError = "policy terminal not accepted";
            }
            catch (Exception error) { terminal = false; FailClosed(error); DrainSafely(now); }
            finally { _active = false; _controllers.Clear(); _episodeId = null; }
            return terminal;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { ClearControllers(); if (_active) _sink.MarkIncomplete(); }
            catch (Exception) { }
            _controllers.Clear();
            try { _mailbox.Dispose(); } catch (Exception) { }
        }

        private void Drain(double now)
        {
            foreach (var controller in _controllers.Values)
                foreach (TrainingComponentOutput output in controller.DrainOutputs())
                {
                    bool recorded;
                    try { recorded = _sink.Record(now, output); }
                    catch (Exception) { recorded = false; }
                    if (!recorded)
                    {
                        _sink.MarkIncomplete();
                        _disabled = true;
                        _lastError = "policy trace could not be recorded";
                    }
                }
            if (_disabled) ClearControllers();
        }

        private void DrainSafely(double now)
        {
            try { Drain(now); }
            catch (Exception) { try { _sink.MarkIncomplete(); } catch (Exception) { } }
        }

        private void ClearControllers()
        { foreach (var controller in _controllers.Values) controller.Clear(); }

        private void FailClosed(Exception error)
        {
            _disabled = true;
            _canApply = false;
            _lastError = "policy disabled: " + error.GetType().Name;
            try { ClearControllers(); } catch (Exception) { }
        }

        private static bool SafePhase(Agent.ActionCodeType action, Agent.ActionStage stage)
        {
            return stage != Agent.ActionStage.Defend && stage != Agent.ActionStage.DefendParry
                && (action == Agent.ActionCodeType.Idle || action == Agent.ActionCodeType.Guard || action == Agent.ActionCodeType.Other
                    || action == Agent.ActionCodeType.ReadyMelee || action == Agent.ActionCodeType.ReleaseMelee);
        }
    }
}
