using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace AdimiDuel_Training
{
    // Isolated offline rounds; each AI side uses Native until a policy is connected.
    internal sealed class TrainingMissionLogic : MissionLogic
    {
        private const string CharacterId = "vlandian_voulgier";
        private const string WeaponId = "vlandia_2hsword_1_t5";
        private const double RoundLimit = 600d;
        private readonly Random _random = new Random();
        private readonly object _eventGate = new object();
        private readonly bool _playerControlled;
        private readonly TrainingBattlePowerLogic _battlePower;
        private readonly TrainingCasualtyHandler _casualties;
        private Team _sideA, _sideB;
        private Agent _a, _b;
        private int _aIndex = -1, _bIndex = -1;
        private Agent _retiringA, _retiringB;
        private Vec3 _positionA, _positionB;
        private Vec3 _cameraTarget;
        private bool _ready, _stopped, _roundActive, _removedA, _removedB, _deletedA, _deletedB, _invalidRemoval;
        private bool _playerRemovalObserved, _unexpectedPlayerDeletion, _playerDetachInProgress, _unexpectedPlayerDeletionReported;
        private Exception _playerDetachError;
        private double _roundStart, _nextRound, _nextSnapshot;
        private double _retirementDeadline;
        private long _reportedDropped, _reportedErrors;
        private long _policyMountEpoch;
        private double _nextFailureNotice;
        private double _nextFrameNotice;
        private TrainingEpisodeRecorder _recorder;
        private TrainingPolicyBridge _policyBridge;
        private TrainingPolicySession _policy;
        private string _reportedPolicyError;
        private long _reportedPolicyErrors;

        internal TrainingMissionLogic(bool playerControlled, TrainingBattlePowerLogic battlePower, TrainingCasualtyHandler casualties)
        {
            _playerControlled = playerControlled;
            _battlePower = battlePower ?? throw new ArgumentNullException(nameof(battlePower));
            _casualties = casualties ?? throw new ArgumentNullException(nameof(casualties));
        }

        internal bool CameraReady { get { lock (_eventGate) return _ready && !_stopped; } }

        internal Vec3 CameraTarget
        {
            get
            {
                lock (_eventGate) return _cameraTarget;
            }
        }

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            // Several original scene objects assume at least two teams exist
            // by AfterMissionStart; create them before their callbacks.
            _sideA = Mission.Teams.Add(BattleSideEnum.Attacker, 0xff9a352f, 0xffdedede, null);
            _sideB = Mission.Teams.Add(BattleSideEnum.Defender, 0xff3255a0, 0xffdedede, null);
            _sideA.SetIsEnemyOf(_sideB, true);
            _sideB.SetIsEnemyOf(_sideA, true);
            if (_playerControlled) Mission.PlayerTeam = _sideA;
        }

        public override void AfterStart()
        {
            base.AfterStart();
            try
            {
                Mission.SetMissionMode(MissionMode.Battle, true);
                Mission.MissionTeamAIType = Mission.MissionTeamAITypeEnum.FieldBattle;
                _sideA.AddTeamAI(new TeamAIGeneral(Mission, _sideA));
                _sideB.AddTeamAI(new TeamAIGeneral(Mission, _sideB));
                var anchors = Mission.Scene.FindEntitiesWithTag("adimi_ai_spawn")
                    .Where(e => e.HasTag("adimi_ai_group_1")).ToArray();
                if (anchors.Length != 1) throw new InvalidOperationException("训练地图没有唯一的第 1 组出生点。");
                Vec3 anchor = anchors[0].GlobalPosition;
                _positionA = ProjectGround(anchor);
                _positionB = ProjectGround(anchor + new Vec3(0, -6f, 0));
                float distance;
                Vec3 hit;
                if (Mission.Scene.RayCastForClosestEntityOrTerrain(_positionA + Vec3.Up, _positionB + Vec3.Up, out distance, out hit))
                    throw new InvalidOperationException("训练出生点之间存在障碍，请调整训练场设置。");
                _recorder = new TrainingEpisodeRecorder(System.IO.Path.Combine(Utilities.GetBasePath(), "Modules", "AdimiDuel_Training"));
                InitializeOptionalPolicy();
                _nextRound = Mission.CurrentTime + 2d;
                lock (_eventGate) { _cameraTarget = (_positionA + _positionB) * 0.5f; _ready = true; }
                SubModule.Notify(_playerControlled
                    ? "亲自挑战：你将使用双手剑与 AI 对打；无可用训练器时使用原版 AI，单机数值未与线上对齐。"
                    : "单机训练场：无可用训练器时使用原版 AI，战斗数值尚未与线上对齐；ESC 返回。");
            }
            catch (Exception error) { StopWithError("训练场初始化失败", error); }
        }

        private Vec3 ProjectGround(Vec3 position)
        {
            if (!Finite(position) || !Mission.IsPositionInsideHardBoundaries(position.AsVec2))
                throw new InvalidOperationException("训练出生点在场景边界之外。");
            var world = new WorldPosition(Mission.Scene, position);
            Vec3 ground = world.GetGroundVec3();
            if (!Finite(ground) || Math.Abs(position.z - ground.z) > 2f || world.GetNavMesh() == UIntPtr.Zero)
                throw new InvalidOperationException("训练出生点不在有效导航地面。");
            return ground;
        }

        private static bool Finite(Vec3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x)
            && !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);

        private static bool Alive(Agent agent) => agent != null && agent.IsActive() && agent.Health > 0f;

        private void InitializeOptionalPolicy()
        {
            try
            {
                if (!TrainingPolicySession.IsEnabled(Environment.GetEnvironmentVariable("ADIMI_LOCAL_POLICY_BRIDGE"))) return;
                _policyBridge = new TrainingPolicyBridge(null, true);
                _policy = new TrainingPolicySession(_recorder, _policyBridge);
                SubModule.Log("Optional local policy mailbox: " + _policyBridge.SessionDirectory);
            }
            catch (Exception error)
            {
                try { if (_policyBridge != null) _policyBridge.Dispose(); } catch (Exception) { }
                _policyBridge = null;
                _policy = null;
                SubModule.Log("Local policy unavailable; using native AI: " + error.Message);
            }
        }

        public override void OnPreMissionTick(float dt)
        {
            base.OnPreMissionTick(dt);
            if (!_ready || _stopped || Mission.MissionEnded) return;
            try
            {
                double now = Mission.CurrentTime;
                bool playerDeleted, detachInProgress;
                Exception detachError;
                lock (_eventGate)
                {
                    playerDeleted = _unexpectedPlayerDeletion;
                    detachInProgress = _playerDetachInProgress;
                    detachError = _playerDetachError;
                }
                if (playerDeleted)
                {
                    if (detachInProgress) return;
                    _unexpectedPlayerDeletionReported = true;
                    StopWithError("训练已停止", new InvalidOperationException("玩家角色被异常删除，请退出训练场后重新进入。", detachError));
                    return;
                }
                ReportRecorderFailures(now);
                ReportPolicyFailures();
                if (!_roundActive)
                {
                    if (now >= _nextRound)
                    {
                        bool stillRetiring;
                        lock (_eventGate)
                        {
                            stillRetiring = IsStillActive(_retiringA) || IsStillActive(_retiringB);
                            if (!stillRetiring) _retiringA = _retiringB = null;
                        }
                        if (stillRetiring)
                        {
                            if (now >= _retirementDeadline)
                                throw new InvalidOperationException("上一局角色尚未退出，已停止重复生成。");
                            return;
                        }
                        StartRound(now);
                    }
                    return;
                }
                if (_casualties.FlushPending()) ExpireTeamQueries();
                if (now >= _nextSnapshot)
                {
                    _nextSnapshot = now + (now - _roundStart < 120d ? .05d : .2d);
                    var actors = new List<TrainingActorSnapshot>();
                    Vec3 aPosition, bPosition;
                    long policyMountEpoch;
                    lock (_eventGate)
                    {
                        bool aAlive = !_deletedA && Alive(_a), bAlive = !_deletedB && Alive(_b);
                        if (aAlive) { var a = Snapshot(_a); a.Side = 0; a.IsPlayer = _playerControlled || _a.Controller == AgentControllerType.Player; actors.Add(a); }
                        if (bAlive) { var b = Snapshot(_b); b.Side = 1; b.IsPlayer = _b.Controller == AgentControllerType.Player; actors.Add(b); }
                        aPosition = aAlive ? _a.Position : _positionA;
                        bPosition = bAlive ? _b.Position : _positionB;
                        _cameraTarget = (aPosition + bPosition) * .5f;
                        policyMountEpoch = _policyMountEpoch;
                    }
                    if (actors.Count > 0 && !_recorder.RecordFrame(now, actors.ToArray()) && now >= _nextFrameNotice)
                    {
                        _nextFrameNotice = now + 10d;
                        SubModule.Notify("本局训练快照不完整，可能无法作为有效样本。");
                    }
                    if (actors.Count > 0) PublishPolicyObservation(now, actors, policyMountEpoch);
                }
                TickPolicy(now);
                TrainingRoundOutcome outcome;
                lock (_eventGate)
                    outcome = TrainingRoundPolicy.Resolve(_removedA, _removedB, _invalidRemoval,
                        !_deletedA && Alive(_a), !_deletedB && Alive(_b), now - _roundStart, RoundLimit);
                if (outcome.Complete) EndRound(now, outcome.WinningSide, outcome.Reason, outcome.Interrupted);
            }
            catch (Exception error) { StopWithError("训练对局出现错误", error); }
        }

        private static bool IsStillActive(Agent agent) => agent != null && agent.IsActive();

        private void ExpireTeamQueries()
        {
            if (_sideA != null) _sideA.QuerySystem.Expire();
            if (_sideB != null) _sideB.QuerySystem.Expire();
        }

        private void StartRound(double now)
        {
            BasicCharacterObject character = MBObjectManager.Instance.GetObject<BasicCharacterObject>(CharacterId);
            ItemObject sword = MBObjectManager.Instance.GetObject<ItemObject>(WeaponId);
            if (character == null || character.FirstBattleEquipment == null || character.BodyPropertyRange == null
                || sword == null || sword.PrimaryWeapon == null || sword.PrimaryWeapon.WeaponClass != WeaponClass.TwoHandedSword)
                throw new InvalidOperationException("原版双手剑兵种或武器资源不可用。");
            Equipment equipment = character.FirstBattleEquipment.Clone();
            for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                equipment[i] = EquipmentElement.Invalid;
            equipment[EquipmentIndex.Weapon0] = new EquipmentElement(sword);
            equipment[EquipmentIndex.Horse] = EquipmentElement.Invalid;
            equipment[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
            lock (_eventGate) { _removedA = _removedB = _deletedA = _deletedB = _invalidRemoval = _playerRemovalObserved = false; }
            if (_playerControlled && Mission.MainAgent != null)
                throw new InvalidOperationException("上一个玩家角色尚未清理，已停止重复生成。");
            try
            {
                _casualties.BeginRound();
                _battlePower.BeginRound(_sideA, character, _sideB, character);
                TrainingTeamQueryReset.Reset(_sideA, (float)now);
                TrainingTeamQueryReset.Reset(_sideB, (float)now);
                _a = Spawn(character, equipment, _sideA, _positionA, _positionB, false, _playerControlled);
                _aIndex = _a.Index;
                _casualties.Track(_a, _sideA.GetFormation(character.DefaultFormationClass), _battlePower.GetTotalTeamPower(_sideA));
                _b = Spawn(character, equipment, _sideB, _positionB, _positionA, true, false);
                _bIndex = _b.Index;
                _casualties.Track(_b, _sideB.GetFormation(character.DefaultFormationClass), _battlePower.GetTotalTeamPower(_sideB));
                foreach (Team team in new[] { _sideA, _sideB })
                {
                    team.ClearTacticOptions();
                    team.AddTacticOption(new TacticCharge(team));
                    foreach (Formation formation in team.FormationsIncludingSpecialAndEmpty) formation.SetControlledByAI(true);
                    team.QuerySystem.Expire();
                    team.ResetTactic();
                }
                _roundStart = now;
                _nextSnapshot = now;
                lock (_eventGate)
                {
                    _roundActive = true;
                    TrainingParticipant first = Participant(_a, 0, _playerControlled), second = Participant(_b, 1, false);
                    bool started = _playerControlled
                        ? _recorder.BeginLocalPlayerRound(now, SubModule.SceneId, "1.4.8", "0.1.0", first, second)
                        : _recorder.BeginRound(now, SubModule.SceneId, "1.4.8", "0.1.0", first, second);
                    if (!started) throw new InvalidOperationException("训练记录无法开始，已停止自动对战。");
                    StartPolicyRound();
                }
            }
            catch
            {
                lock (_eventGate) _roundActive = false;
                CloseRoundResources();
                throw;
            }
        }

        private Agent Spawn(BasicCharacterObject character, Equipment equipment, Team team, Vec3 position, Vec3 enemyPosition, bool female, bool player)
        {
            Vec2 direction = (enemyPosition - position).AsVec2.Normalized();
            int seed = _random.Next(2001);
            BodyProperties body = BodyProperties.GetRandomBodyProperties(character.Race, female,
                character.GetBodyPropertiesMin(), character.GetBodyPropertiesMax(), (int)equipment.HairCoverType, seed,
                character.BodyPropertyRange.HairTags, character.BodyPropertyRange.BeardTags, character.BodyPropertyRange.TattooTags);
            var build = new AgentBuildData(character).Equipment(equipment.Clone()).FixedEquipment(true).NoHorses(true)
                .InitialPosition(in position).InitialDirection(in direction).Controller(player ? AgentControllerType.Player : AgentControllerType.AI)
                .TroopOrigin(new BasicBattleAgentOrigin(character)).BodyProperties(body).IsFemale(female).EquipmentSeed(seed)
                .ClothingColor1(team.Color).ClothingColor2(team.Color2).CanSpawnOutsideOfMissionBoundary(false)
                .Team(team);
            if (!player) build.Formation(team.GetFormation(character.DefaultFormationClass));
            Agent agent = Mission.SpawnAgent(build);
            if (agent == null) throw new InvalidOperationException("引擎未生成训练角色。");
            try
            {
                if (player) Mission.MainAgent = agent;
                agent.WieldInitialWeapons();
                if (!player) agent.SetWatchState(Agent.WatchState.Alarmed);
                return agent;
            }
            catch
            {
                // The caller has not received this local agent yet. Release it
                // here if any post-spawn setup fails, including MainAgent binding.
                try { ReleaseAgent(agent); }
                catch (Exception cleanup) { SubModule.Log("Failed to release a partially spawned training agent: " + cleanup.Message); }
                throw;
            }
        }

        private static TrainingParticipant Participant(Agent agent, int side, bool player)
        {
            MissionWeapon weapon = agent.WieldedWeapon;
            return new TrainingParticipant { AgentId = agent.Index, Side = side,
                CharacterId = agent.Character == null ? "" : agent.Character.StringId,
                WeaponId = weapon.IsEmpty || weapon.Item == null ? "" : weapon.Item.StringId,
                WeaponClass = weapon.IsEmpty || weapon.CurrentUsageItem == null ? "" : weapon.CurrentUsageItem.WeaponClass.ToString(),
                ControlSource = player ? "human" : "native", ModelId = null, ModelSha256 = null };
        }

        private static TrainingActorSnapshot Snapshot(Agent agent)
        {
            Vec3 p = agent.Position, look = agent.LookDirection, velocity = agent.GetRealGlobalVelocity();
            Vec2 input = agent.MovementInputVector;
            MissionWeapon weapon = agent.WieldedWeapon;
            WeaponComponentData usage = weapon.IsEmpty ? null : weapon.CurrentUsageItem;
            return new TrainingActorSnapshot
            {
                AgentId = agent.Index, X = p.x, Y = p.y, Z = p.z, LookX = look.x, LookY = look.y, LookZ = look.z,
                VelocityX = velocity.x, VelocityY = velocity.y, MoveInputX = input.x, MoveInputY = input.y,
                Health = agent.Health, HealthLimit = agent.HealthLimit, WeaponLength = usage == null ? 0f : usage.GetRealWeaponLength(),
                ActionType = (int)agent.GetCurrentActionType(1), ActionStage = (int)agent.GetCurrentActionStage(1),
                ActionDirection = (int)agent.GetCurrentActionDirection(1), ActionProgress = agent.GetCurrentActionProgress(1),
                MovementFlags = (uint)agent.MovementFlags, EventFlags = (uint)agent.EventControlFlags,
                WeaponId = weapon.IsEmpty || weapon.Item == null ? "" : weapon.Item.StringId,
                WeaponClass = usage == null ? "" : usage.WeaponClass.ToString(), IsMounted = agent.HasMount,
            };
        }

        private void StartPolicyRound()
        {
            if (_policy == null) return;
            try
            {
                if (!_policy.BeginRound(_recorder.ActiveEpisodeId))
                { DisableOptionalPolicy(new InvalidOperationException("local policy episode could not start")); return; }
                if (!_playerControlled && !_policy.Register(_a, false)) SubModule.Log("Local policy side A unavailable; using native AI.");
                if (!_policy.Register(_b, false)) SubModule.Log("Local policy side B unavailable; using native AI.");
            }
            catch (Exception error) { DisableOptionalPolicy(error); }
        }

        private void PublishPolicyObservation(double now, List<TrainingActorSnapshot> snapshots, long policyMountEpoch)
        {
            lock (_eventGate)
            {
                // A mount event may arrive after sampling but before this
                // publication. Never re-enable a pre-mount observation.
                if (!_roundActive || _policy == null || policyMountEpoch != _policyMountEpoch) return;
                try
                {
                    _policy.PublishObservation(now, TrainingPolicySession.ConvertObservations(snapshots));
                }
                catch (Exception error) { DisableOptionalPolicy(error); }
            }
        }

        private void TickPolicy(double now)
        {
            lock (_eventGate)
            {
                if (!_roundActive || _policy == null) return;
                try
                {
                    var phases = new List<TrainingPolicyPhase>(2);
                    if (!_deletedA && !_removedA && Alive(_a)) phases.Add(PolicyPhase(_a, _playerControlled));
                    if (!_deletedB && !_removedB && Alive(_b)) phases.Add(PolicyPhase(_b, false));
                    _policy.Tick(now, phases.ToArray());
                }
                catch (Exception error) { DisableOptionalPolicy(error); }
            }
        }

        private static TrainingPolicyPhase PolicyPhase(Agent agent, bool player)
        {
            return new TrainingPolicyPhase { AgentId = agent.Index, IsPlayer = player || agent.Controller == AgentControllerType.Player,
                IsAi = agent.Controller == AgentControllerType.AI, IsMounted = agent.HasMount,
                Action = agent.GetCurrentActionType(1), Stage = agent.GetCurrentActionStage(1) };
        }

        public override void OnAgentMount(Agent agent)
        {
            if (agent == null) return;
            try
            {
                lock (_eventGate)
                {
                    if (!_roundActive || _policy == null || (!ReferenceEquals(agent, _a) && !ReferenceEquals(agent, _b))) return;
                    _policyMountEpoch++;
                    _policy.SuspendForMountedActor();
                }
            }
            catch (Exception error) { lock (_eventGate) DisableOptionalPolicy(error); }
        }

        private void DisableOptionalPolicy(Exception error)
        {
            try { if (_policy != null) _policy.Dispose(); } catch (Exception) { }
            _policy = null;
            _policyBridge = null;
            SubModule.Log("Local policy disabled; using native AI: " + error.Message);
        }

        private void ClearPolicyAgent(int agentId)
        {
            try { if (_policy != null) _policy.RemovedOrDeleted(agentId); }
            catch (Exception error) { DisableOptionalPolicy(error); }
        }

        public override void OnScoreHit(Agent victim, Agent attacker, WeaponComponentData attackerWeapon, bool isBlocked,
            bool isSiegeEngineHit, in Blow blow, in AttackCollisionData collisionData, float damagedHp, float hitDistance, float shotDifficulty)
        {
            if (victim == null) return;
            try
            {
                lock (_eventGate)
                {
                    if (!_roundActive || _recorder == null) return;
                    bool targetIsOurs = ReferenceEquals(victim, _a) || ReferenceEquals(victim, _b);
                    bool attackerIsOurs = ReferenceEquals(attacker, _a) || ReferenceEquals(attacker, _b);
                    if (!targetIsOurs && !attackerIsOurs) return;
                    if (!targetIsOurs || !attackerIsOurs || ReferenceEquals(victim, attacker))
                    {
                        _invalidRemoval = true;
                        return;
                    }
                    _recorder.RecordHit(Mission.CurrentTime, attacker.Index, victim.Index, isBlocked, damagedHp, collisionData.IsMissile,
                        attackerWeapon == null ? "" : attackerWeapon.WeaponClass.ToString(), collisionData.IsAlternativeAttack,
                        (int)collisionData.AttackDirection, collisionData.AttackProgress, collisionData.StrikeType, collisionData.ThrustTipHit);
                }
            }
            catch (Exception error) { SubModule.Log("Training hit recording skipped: " + error.Message); }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            long callbackTimestamp = Stopwatch.GetTimestamp();
            if (affectedAgent == null) return;
            try
            {
                lock (_eventGate)
                {
                    if (!_roundActive || _recorder == null) return;
                    bool isA = ReferenceEquals(affectedAgent, _a), isB = ReferenceEquals(affectedAgent, _b);
                    if (!isA && !isB) return;
                    if (isA && _playerControlled) _playerRemovalObserved = true;
                    ClearPolicyAgent(isA ? _aIndex : _bIndex);
                    bool died = agentState == AgentState.Killed || agentState == AgentState.Unconscious;
                    bool alreadyRemoved = isA ? _removedA : _removedB;
                    if (alreadyRemoved)
                    {
                        // Nonlethal cleanup after an already reported death is
                        // not a second removal or a new training interaction.
                        if (died && !ReferenceEquals(affectorAgent, isA ? _b : _a)) _invalidRemoval = true;
                        return;
                    }
                    if (isA) _removedA = died;
                    if (isB) _removedB = died;
                    if (!died || !ReferenceEquals(affectorAgent, isA ? _b : _a) || (isA && _removedB) || (isB && _removedA)) _invalidRemoval = true;
                    int attackerIndex = ReferenceEquals(affectorAgent, _a) ? _aIndex : ReferenceEquals(affectorAgent, _b) ? _bIndex : -1;
                    _recorder.RecordRemoval(Mission.CurrentTime, attackerIndex, isA ? _aIndex : _bIndex, died, (int)agentState, callbackTimestamp);
                }
            }
            catch (Exception error) { SubModule.Log("Training removal recording skipped: " + error.Message); }
        }

        public override void OnAgentDeleted(Agent affectedAgent)
        {
            if (affectedAgent == null) return;
            bool detachPlayer = false;
            lock (_eventGate)
            {
                if (ReferenceEquals(affectedAgent, _retiringA)) _retiringA = null;
                if (ReferenceEquals(affectedAgent, _retiringB)) _retiringB = null;
                bool isA = ReferenceEquals(affectedAgent, _a), isB = ReferenceEquals(affectedAgent, _b);
                if (isA) { _deletedA = true; ClearPolicyAgent(_aIndex); }
                if (isB) { _deletedB = true; ClearPolicyAgent(_bIndex); }
                if (!_roundActive) return;
                if ((isA && !_removedA) || (isB && !_removedB))
                    _invalidRemoval = true;
                if (_playerControlled && isA && !_playerRemovalObserved && !_unexpectedPlayerDeletion)
                {
                    _unexpectedPlayerDeletion = true;
                    _playerDetachInProgress = true;
                    detachPlayer = true;
                }
            }
            if (!detachPlayer) return;
            try
            {
                // The setter notifies native views synchronously. Do not invoke
                // it while holding _eventGate, and never detach another agent.
                if (ReferenceEquals(Mission.MainAgent, affectedAgent)) Mission.MainAgent = null;
            }
            catch (Exception error)
            {
                lock (_eventGate) _playerDetachError = error;
            }
            finally
            {
                lock (_eventGate) _playerDetachInProgress = false;
            }
        }

        private void EndRound(double now, int? winner, string reason, bool interrupted)
        {
            bool close = false;
            try
            {
            lock (_eventGate)
            {
                if (!_roundActive) return;
                if (_policy != null)
                {
                    try { _policy.EndRound(now, winner, reason, interrupted); }
                    catch (Exception error) { DisableOptionalPolicy(error); }
                }
                _roundActive = false;
                close = true;
                _recorder.EndRound(now, winner, reason, interrupted);
            }
            }
            finally
            {
                if (close)
                {
                    CloseRoundResources();
                    _nextRound = now + 3d;
                    _retirementDeadline = now + 15d;
                }
            }
        }

        private void CloseRoundResources()
        {
            _casualties.EndRound();
            _battlePower.EndRound();
            lock (_eventGate)
            {
                // OnAgentDeleted clears/marks references under this same lock
                // before native OnDelete can invalidate the agent pointer.
                _retiringA = _deletedA ? null : _a;
                _retiringB = _deletedB ? null : _b;
                try { ReleaseAgent(_retiringA, _unexpectedPlayerDeletion); }
                finally
                {
                    try { ReleaseAgent(_retiringB, _unexpectedPlayerDeletion); }
                    finally { _a = _b = null; _aIndex = _bIndex = -1; }
                }
            }
            ExpireTeamQueries();
        }

        private void ReleaseAgent(Agent agent, bool preserveCurrentMainAgent = false)
        {
            if (agent == null) return;
            if (ReferenceEquals(Mission.MainAgent, agent))
            {
                if (preserveCurrentMainAgent) return;
                Mission.MainAgent = null;
            }
            if (Alive(agent))
            {
                if (agent.Controller == AgentControllerType.Player) agent.Controller = AgentControllerType.None;
                agent.FadeOut(true, true);
            }
        }

        private void StopWithError(string reason, Exception error)
        {
            _stopped = true;
            if (_roundActive && _recorder != null) { try { EndRound(Mission.CurrentTime, null, "error", true); } catch (Exception) { } }
            SubModule.Log(reason + ": " + error);
            SubModule.Notify(reason + "：" + error.Message);
        }

        private void ReportRecorderFailures(double now)
        {
            if (_recorder == null) return;
            long dropped = _recorder.Dropped, errors = _recorder.Errors;
            if ((dropped == _reportedDropped && errors == _reportedErrors) || now < _nextFailureNotice) return;
            _reportedDropped = dropped; _reportedErrors = errors;
            _nextFailureNotice = now + 10d;
            SubModule.Log("Self-play writer status: dropped=" + dropped + ", errors=" + errors + ", " + _recorder.LastError);
            SubModule.Notify("训练数据保存出现异常，部分回合可能未保存；请检查日志与存储空间。");
        }

        private void ReportPolicyFailures()
        {
            if (_policy == null || _policyBridge == null) return;
            string localError = _policy.LastError;
            long errors = _policyBridge.Errors;
            if (localError != _reportedPolicyError || errors != _reportedPolicyErrors)
            {
                _reportedPolicyError = localError;
                _reportedPolicyErrors = errors;
                if (!string.IsNullOrEmpty(localError) || errors != 0)
                    SubModule.Log("Local policy status; AI falls back to native when unavailable: "
                        + localError + "; mailbox errors=" + errors + "; " + _policyBridge.LastError);
            }
        }

        protected override void OnEndMission()
        {
            try
            {
                _stopped = true;
                lock (_eventGate)
                {
                    if (_unexpectedPlayerDeletion && !_unexpectedPlayerDeletionReported)
                    {
                        _unexpectedPlayerDeletionReported = true;
                        SubModule.Log("Player agent deleted unexpectedly; training stopped: " + _playerDetachError);
                    }
                }
                if (_roundActive && _recorder != null) EndRound(Mission.CurrentTime, null, "exited", true);
                else { _casualties.EndRound(); _battlePower.EndRound(); }
            }
            catch (Exception error) { SubModule.Log("Training cleanup failed: " + error.Message); }
            finally
            {
                try { if (_policy != null) _policy.Dispose(); } catch (Exception) { }
                try { if (_recorder != null) _recorder.Dispose(); } catch (Exception) { }
                _policy = null;
                _policyBridge = null;
            }
            base.OnEndMission();
        }
    }
}
