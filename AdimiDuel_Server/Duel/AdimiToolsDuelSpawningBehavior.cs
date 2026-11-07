using AdimiToolsShared;
using NetworkMessages.FromServer;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.MissionRepresentatives;
using TaleWorlds.PlayerServices;
using static AdimiDuel_Server.Duel.AdimiToolsMissionMultiplayerDuel;

namespace AdimiDuel_Server.Duel;

internal class AdimiToolsDuelSpawningBehavior : DuelSpawningBehavior
{
    private sealed record CapturedMount(Agent Horse, Agent FormerRider, double Deadline)
    {
        public bool Released { get; set; }
    }

    private sealed record InPlaceDuelRespawn(Team Team, MatrixFrame Frame, EquipmentElement Horse, EquipmentElement Harness,
        CapturedMount CapturedMount);

    private readonly Dictionary<PlayerId, AgentBuildData> _buildAgentData;
    private readonly Dictionary<MissionPeer, InPlaceDuelRespawn> _inPlaceDuelRespawns = new();

    public AdimiToolsDuelSpawningBehavior()
    {
        AdimiToolsConsoleLog.Log("Initalized AdimiToolsDuelSpawningBehavior");
        _buildAgentData = new Dictionary<PlayerId, AgentBuildData>();
    }

    public override void OnTick(float dt)
    {
        if (IsSpawningEnabled && SpawnCheckTimer.Check(Mission.Current.CurrentTime))
        {
            SpawnAgents();
        }

        foreach (NetworkCommunicator networkPeer in GameNetwork.NetworkPeers)
        {
            if (!networkPeer.IsSynchronized)
            {
                continue;
            }

            MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
            AdimiToolsTeamBattleBehavior? teamBattle = Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>();
            if (missionPeer != null && teamBattle?.ShouldDelayRespawn(missionPeer) == true)
            {
                continue;
            }

            AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
            bool hasPendingAiRespawn = missionPeer != null && aiArena?.HasPendingAiRespawn(missionPeer) == true;
            bool hasPendingTeamRespawn = missionPeer != null && teamBattle?.HasPendingRoundRespawn(missionPeer) == true;
            InPlaceDuelRespawn? inPlaceDuel = missionPeer == null ? null : GetInPlaceDuelRespawn(missionPeer);
            if (missionPeer == null || missionPeer.ControlledAgent != null || !missionPeer.HasSpawnedAgentVisuals
                || (!hasPendingAiRespawn && !hasPendingTeamRespawn && inPlaceDuel == null && CanUpdateSpawnEquipment(missionPeer)))
            {
                continue;
            }

            if (aiArena?.ShouldDelayRespawn(missionPeer) == true)
            {
                continue;
            }

            if (inPlaceDuel != null && !TryReleaseCapturedMount(inPlaceDuel.CapturedMount))
            {
                continue;
            }

            if (hasPendingAiRespawn || hasPendingTeamRespawn || inPlaceDuel != null)
            {
                // Series round replacements and completed AI loss recovery
                // use the original visuals/slot route with server-owned data.
                missionPeer.EquipmentUpdatingExpired = true;
            }

            MultiplayerClassDivisions.MPHeroClass mPHeroClassForPeer = MultiplayerClassDivisions.GetMPHeroClassForPeer(missionPeer);
            MPPerkObject.MPOnSpawnPerkHandler onSpawnPerkHandler = MPPerkObject.GetOnSpawnPerkHandler(missionPeer);
            GameNetwork.BeginBroadcastModuleEvent();
            GameNetwork.WriteMessage(new SyncPerksForCurrentlySelectedTroop(networkPeer, missionPeer.Perks[missionPeer.SelectedTroopIndex]));
            GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.ExcludeOtherTeamPlayers, networkPeer);

            _buildAgentData.TryGetValue(networkPeer.VirtualPlayer.Id, out AgentBuildData? agentBuildData);

            BasicCharacterObject basicCharacterObject = mPHeroClassForPeer.HeroCharacter;

            PlayerDuelData.PlayerDuelList.TryGetValue(missionPeer.Peer.Id, out PlayerDuelData? playerDuelData);
            if (agentBuildData == null)
            {
                Banner? banner = AdimiToolsDuelAppearance.CreateBanner(missionPeer.Culture);
                agentBuildData = new AgentBuildData(basicCharacterObject).VisualsIndex(0).Team(missionPeer.Team).TroopOrigin(new BasicBattleAgentOrigin(basicCharacterObject))
                    .Formation(missionPeer.ControlledFormation)
                    .IsFemale(missionPeer.Peer.IsFemale)
                    .Banner(banner);
                agentBuildData.MissionPeer(missionPeer);

                Equipment equipment = basicCharacterObject.Equipment.Clone();
                IEnumerable<(EquipmentIndex, EquipmentElement)>? enumerable2 = onSpawnPerkHandler?.GetAlternativeEquipments(isPlayer: true);
                if (enumerable2 != null)
                {
                    foreach (var item in enumerable2)
                    {
                        equipment[item.Item1] = item.Item2;
                    }
                }

                agentBuildData.Equipment(equipment);
                GameMode.AddCosmeticItemsToEquipment(equipment, GameMode.GetUsedCosmeticsFromPeer(missionPeer, basicCharacterObject));

                if (missionPeer.ControlledFormation != null && missionPeer.ControlledFormation.Banner == null)
                {
                    missionPeer.ControlledFormation.Banner = banner;
                }
            }
            else if (GameMode is AdimiToolsMissionMultiplayerDuel)
            {
                if (playerDuelData != null)
                {
                    // Remove potentially dropped shields etc.
                    for (int i = (int)EquipmentIndex.WeaponItemBeginSlot; i < (int)EquipmentIndex.NumPrimaryWeaponSlots; i++)
                    {
                        agentBuildData.AgentData.AgentOverridenEquipment[i] = EquipmentElement.Invalid;
                    }

                    // Equip weapons which the player used at duel start.
                    for (int i = (int)EquipmentIndex.WeaponItemBeginSlot; i < Math.Min(playerDuelData.Equipment.Count, (int)EquipmentIndex.NumPrimaryWeaponSlots); i++)
                    {
                        // AdimiToolsConsoleLog.Log($"Item to equip: {playerDuelData.Equipment[i]?.Name.ToString() ?? "invalid name"} | Slot: {i}");
                        agentBuildData.AgentData.AgentOverridenEquipment[i] = new EquipmentElement(playerDuelData.Equipment[i]);
                    }
                }
                else
                {
                    if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig) && duelConfig.RespawnEquip != null)
                    {
                        // Remove potentially dropped shields etc.
                        for (int i = (int)EquipmentIndex.WeaponItemBeginSlot; i < (int)EquipmentIndex.NumPrimaryWeaponSlots; i++)
                        {
                            agentBuildData.AgentData.AgentOverridenEquipment[i] = EquipmentElement.Invalid;
                        }

                        // Equip weapons which the player used at duel start.
                        for (int i = (int)EquipmentIndex.WeaponItemBeginSlot; i < Math.Min(duelConfig.RespawnEquip.Count, (int)EquipmentIndex.NumPrimaryWeaponSlots); i++)
                        {
                            // AdimiToolsConsoleLog.Log($"Item to equip: {duelConfig.RespawnEquip[i]?.Name.ToString() ?? "invalid name"} | Slot: {i}");
                            agentBuildData.AgentData.AgentOverridenEquipment[i] = new EquipmentElement(duelConfig.RespawnEquip[i]);
                        }

                        duelConfig.RespawnEquip = null;
                    }
                }
            }

            agentBuildData.BodyProperties(GetBodyProperties(missionPeer, missionPeer.Culture));
            agentBuildData.Age((int)agentBuildData.AgentBodyProperties.Age);
            agentBuildData.Team(missionPeer.Team); // Set team again, otherwise might break in duel / teamswitch etc.
            // Keep native class/perk gear for ordinary spawns. A series recovery
            // owns its saved banner and equipment after the country default.
            AdimiToolsDuelAppearance.ApplyToBuild(agentBuildData, missionPeer.Culture);
            aiArena?.ConfigureRoundRespawn(missionPeer, agentBuildData);
            teamBattle?.ConfigureRoundRespawn(missionPeer, agentBuildData);
            if (inPlaceDuel != null)
            {
                ApplyInPlaceDuelMount(agentBuildData, inPlaceDuel);
            }

            bool hasMount = !agentBuildData.AgentOverridenSpawnEquipment[EquipmentIndex.Horse].IsEmpty;

            MatrixFrame? getSpawnFrame;
            MatrixFrame? aiRoundRespawn = aiArena?.GetRoundRespawnFrame(missionPeer);
            MatrixFrame? teamRoundRespawn = teamBattle?.GetRoundRespawnFrame(missionPeer);
            bool regularCompletionSpawn = aiArena?.UsesRegularCompletionSpawn(missionPeer) == true;
            if (inPlaceDuel != null)
            {
                getSpawnFrame = inPlaceDuel.Frame;
            }
            else if (teamRoundRespawn.HasValue)
            {
                getSpawnFrame = teamRoundRespawn.Value;
            }
            else if (hasPendingTeamRespawn)
            {
                // Never fall back to an ordinary spawn outside a locked arena.
                continue;
            }
            else if (aiRoundRespawn.HasValue)
            {
                getSpawnFrame = aiRoundRespawn.Value;
            }
            else if (regularCompletionSpawn)
            {
                // Completed losses use ordinary role-specific spawnpoints. The
                // configured snapshot, not a new menu selection, owns the class.
                AdimiToolsDuelSpawnClass spawnClass = GetSpawnClass(agentBuildData.AgentCharacter);
                AdimiToolsDuelSpawnFrame spawnFrameBehavior = (AdimiToolsDuelSpawnFrame)SpawnComponent.SpawnFrameBehavior;
                getSpawnFrame = spawnFrameBehavior.GetTroopSpawnFrame(spawnClass, missionPeer.Team, hasMount)
                    ?? spawnFrameBehavior.GetRandomSectionSpawn("A", hasMount);
            }
            else if (playerDuelData != null && playerDuelData.RespawnFrame != null) // Spawn here during a duel
            {
                getSpawnFrame = (MatrixFrame)playerDuelData.RespawnFrame!;
            }
            else if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig) && duelConfig.DeathFrame != null) // Use this after duels
            {
                AdimiToolsDuelSpawnClass spawnClass = GetSpawnClass(mPHeroClassForPeer.HeroCharacter);
                AdimiToolsDuelSpawnFrame spawnFrameBehavior = (AdimiToolsDuelSpawnFrame)SpawnComponent.SpawnFrameBehavior;
                getSpawnFrame = spawnFrameBehavior.GetTroopSpawnFrame(spawnClass, missionPeer.Team, hasMount) ?? spawnFrameBehavior.GetBestRespawn(duelConfig.DeathFrame.Value.origin, hasMount);
            }
            else
            {
                AdimiToolsDuelSpawnClass spawnClass = GetSpawnClass(mPHeroClassForPeer.HeroCharacter);
                AdimiToolsDuelSpawnFrame spawnFrameBehavior = (AdimiToolsDuelSpawnFrame)SpawnComponent.SpawnFrameBehavior;
                getSpawnFrame = spawnFrameBehavior.GetTroopSpawnFrame(spawnClass, missionPeer.Team, hasMount) ?? spawnFrameBehavior.GetRandomSectionSpawn("A", hasMount);
            }

            MatrixFrame spawnFrame = (MatrixFrame)getSpawnFrame;

            if (spawnFrame.IsIdentity && !aiRoundRespawn.HasValue && !teamRoundRespawn.HasValue && inPlaceDuel == null)
            {
                AdimiToolsConsoleLog.Log($"Trying to spawn case spawnFrame.IsIdentity {missionPeer.Name}");
                continue;
            }

            agentBuildData.InitialPosition(in spawnFrame.origin);
            Vec2 value = spawnFrame.rotation.f.AsVec2.Normalized();
            agentBuildData.InitialDirection(in value);

            Agent agent = Mission.SpawnAgent(agentBuildData, spawnFromAgentVisuals: true);
            if (inPlaceDuel != null)
            {
                _inPlaceDuelRespawns.Remove(missionPeer);
            }

            agent.AddComponent(new MPPerksAgentComponent(agent));
            agent.MountAgent?.UpdateAgentProperties();
            agent.HealthLimit += onSpawnPerkHandler?.GetHitpoints(true) ?? 0f;
            agent.Health = agent.HealthLimit;

            agent.WieldInitialWeapons();
            missionPeer.SpawnCountThisRound++;
            // Callbacks are somehow not working - no idea why.
            // this.OnPeerSpawnedFromVisuals?.Invoke(missionPeer);
            // this.OnAllAgentsFromPeerSpawnedFromVisuals?.Invoke(missionPeer);
            if (GameNetwork.IsServerOrRecorder)
            {
                GameNetwork.BeginBroadcastModuleEvent();
                GameNetwork.WriteMessage(new RemoveAgentVisualsForPeer(missionPeer.GetNetworkPeer()));
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.None);
            }

            missionPeer.HasSpawnedAgentVisuals = false;
            MPPerkObject.GetPerkHandler(missionPeer)?.OnEvent(MPPerkCondition.PerkEventFlags.SpawnEnd);
            aiArena?.CompleteRoundRespawn(missionPeer, agent);
            teamBattle?.CompleteRoundRespawn(missionPeer, agent);
        }

        // base.OnTick(dt);
    }

    internal void RequestClassSelection(MissionPeer peer)
    {
        // A deliberate country change starts a fresh stock loadout preview.
        // SpawnAgents still waits for the old agent to disappear and for the
        // client's initial-perk reply before sending CreateAgentVisuals, which
        // is the original client's automatic loadout-opening event.
        _buildAgentData.Remove(peer.GetNetworkPeer().VirtualPlayer.Id);
        peer.HasSpawnedAgentVisuals = false;
        peer.EquipmentUpdatingExpired = false;
        peer.SpawnTimer.Reset(Mission.CurrentTime, 0f);
    }

    internal bool HasInPlaceDuelRespawn(MissionPeer peer) => GetInPlaceDuelRespawn(peer) != null;

    internal void CancelInPlaceDuelRespawn(MissionPeer peer) => _inPlaceDuelRespawns.Remove(peer);

    internal void QueueCapturedMountReset(Agent winner, MissionPeer loser, Agent horse, MatrixFrame deathFrame)
    {
        MissionPeer? winnerPeer = winner.MissionPeer;
        if (winnerPeer == null || winnerPeer.Team == null || loser.Team == null
            || winnerPeer.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || loser.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true }
            || _inPlaceDuelRespawns.ContainsKey(winnerPeer) || _inPlaceDuelRespawns.ContainsKey(loser))
        {
            return;
        }

        Equipment horseEquipment = horse.SpawnEquipment;
        EquipmentElement horseItem = horseEquipment[EquipmentIndex.Horse];
        MatrixFrame winnerFrame = winner.Frame;
        winnerFrame.origin = horse.Position;
        if (horseItem.IsEmpty || !winnerFrame.origin.IsValid || !deathFrame.origin.IsValid)
        {
            return;
        }

        var captured = new CapturedMount(horse, winner, Mission.CurrentTime + 3d);
        _inPlaceDuelRespawns.Add(winnerPeer, new InPlaceDuelRespawn(winnerPeer.Team, winnerFrame,
            EquipmentElement.Invalid, EquipmentElement.Invalid, captured));
        _inPlaceDuelRespawns.Add(loser, new InPlaceDuelRespawn(loser.Team, deathFrame, horseItem,
            horseEquipment[EquipmentIndex.HorseHarness], captured));
        try
        {
            // Retire the human through the normal removal path. Only the
            // verified unoccupied horse is faded after the rider is detached.
            winner.FadeOut(hideInstantly: true, hideMount: false);
        }
        catch (Exception exception)
        {
            _inPlaceDuelRespawns.Remove(winnerPeer);
            _inPlaceDuelRespawns.Remove(loser);
            AdimiToolsConsoleLog.Log($"Captured duel mount reset failed: {exception.Message}");
        }
    }

    internal void ClearInPlaceDuelRespawns() => _inPlaceDuelRespawns.Clear();

    protected override void SpawnAgents()
    {
        foreach (NetworkCommunicator networkPeer in GameNetwork.NetworkPeers)
        {
            if (!networkPeer.IsSynchronized)
            {
                continue;
            }

            MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
            InPlaceDuelRespawn? inPlaceDuel = missionPeer == null ? null : GetInPlaceDuelRespawn(missionPeer);
            AdimiToolsTeamBattleBehavior? teamBattle = Mission.GetMissionBehavior<AdimiToolsTeamBattleBehavior>();
            if (missionPeer != null && teamBattle?.ShouldDelayRespawn(missionPeer) == true)
            {
                continue;
            }

            if (missionPeer == null || !(missionPeer.Representative is DuelMissionRepresentative) || !networkPeer.IsSynchronized || missionPeer.ControlledAgent != null || missionPeer.HasSpawnedAgentVisuals || missionPeer.Team == null || missionPeer.Team == Mission.SpectatorTeam || !missionPeer.TeamInitialPerkInfoReady || missionPeer.Culture == null || (inPlaceDuel == null && !missionPeer.SpawnTimer.Check(Mission.Current.CurrentTime)))
            {
                continue;
            }

            if (Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>()?.ShouldDelayCoopRespawn(missionPeer) == true)
            {
                continue;
            }

            if (inPlaceDuel != null && !TryReleaseCapturedMount(inPlaceDuel.CapturedMount))
            {
                continue;
            }

            MultiplayerClassDivisions.MPHeroClass mPHeroClassForPeer = MultiplayerClassDivisions.GetMPHeroClassForPeer(missionPeer);
            if (mPHeroClassForPeer == null)
            {
                if (missionPeer.SelectedTroopIndex != 0)
                {
                    missionPeer.SelectedTroopIndex = 0;
                    GameNetwork.BeginBroadcastModuleEvent();
                    GameNetwork.WriteMessage(new UpdateSelectedTroopIndex(networkPeer, 0));
                    GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.ExcludeOtherTeamPlayers, networkPeer);
                }

                continue;
            }

            BasicCharacterObject heroCharacter = mPHeroClassForPeer.HeroCharacter;
            Equipment equipment = heroCharacter.Equipment.Clone();
            IEnumerable<(EquipmentIndex, EquipmentElement)>? enumerable = MPPerkObject.GetOnSpawnPerkHandler(missionPeer)?.GetAlternativeEquipments(true);
            if (enumerable != null)
            {
                foreach (var item in enumerable)
                {
                    equipment[item.Item1] = item.Item2;
                }
            }

            AgentBuildData agentBuildData = new AgentBuildData(heroCharacter).MissionPeer(missionPeer).Equipment(equipment).Team(missionPeer.Team)
                .TroopOrigin(new BasicBattleAgentOrigin(heroCharacter))
                .IsFemale(missionPeer.Peer.IsFemale)
                .BodyProperties(GetBodyProperties(missionPeer, missionPeer.Culture))
                .VisualsIndex(0);
            AdimiToolsDuelAppearance.ApplyToBuild(agentBuildData, missionPeer.Culture);
            AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
            bool hasPendingAiRespawn = aiArena?.HasPendingAiRespawn(missionPeer) == true;
            bool hasPendingTeamRespawn = teamBattle?.HasPendingRoundRespawn(missionPeer) == true;
            if (hasPendingAiRespawn)
            {
                // The original client must receive the same saved equipment
                // in its visuals packet as the server's later real spawn.
                aiArena!.ConfigureRoundRespawn(missionPeer, agentBuildData);
            }

            if (hasPendingTeamRespawn)
            {
                teamBattle!.ConfigureRoundRespawn(missionPeer, agentBuildData);
            }

            if (inPlaceDuel != null)
            {
                ApplyInPlaceDuelMount(agentBuildData, inPlaceDuel);
            }

            if (!_buildAgentData.TryAdd(networkPeer.VirtualPlayer.Id, agentBuildData))
            {
                _buildAgentData[networkPeer.VirtualPlayer.Id] = agentBuildData;
            }

            if (GameMode.ShouldSpawnVisualsForServer(networkPeer) && agentBuildData.AgentVisualsIndex == 0)
            {
                missionPeer.HasSpawnedAgentVisuals = true;
                missionPeer.EquipmentUpdatingExpired = false;
            }

            HandleDuelAgentVisualSpawning(networkPeer, agentBuildData, useCosmetics: !hasPendingAiRespawn && !hasPendingTeamRespawn);
        }
    }

    private static void ApplyInPlaceDuelMount(AgentBuildData build, InPlaceDuelRespawn reset)
    {
        Equipment equipment = build.AgentOverridenSpawnEquipment;
        equipment[EquipmentIndex.Horse] = reset.Horse;
        equipment[EquipmentIndex.HorseHarness] = reset.Harness;
        build.NoHorses(reset.Horse.IsEmpty);
    }

    private InPlaceDuelRespawn? GetInPlaceDuelRespawn(MissionPeer peer)
    {
        if (!_inPlaceDuelRespawns.TryGetValue(peer, out InPlaceDuelRespawn? reset))
        {
            return null;
        }

        if (peer.Team != reset.Team || peer.GetNetworkPeer() is not { IsConnectionActive: true, IsSynchronized: true })
        {
            _inPlaceDuelRespawns.Remove(peer);
            return null;
        }

        return reset;
    }

    private bool TryReleaseCapturedMount(CapturedMount captured)
    {
        if (captured.Released)
        {
            return true;
        }

        try
        {
            if (!AdimiToolsMountSafety.IsLiving(Mission, captured.Horse))
            {
                captured.Released = true;
            }
            else if (AdimiToolsMountSafety.TryGetCurrentRider(Mission, captured.Horse, out Agent? rider)
                && !ReferenceEquals(rider, captured.FormerRider))
            {
                if (rider == null)
                {
                    if (captured.Horse.RiderAgent != null
                        || (Mission.AllAgents.Contains(captured.FormerRider) && captured.FormerRider.State != AgentState.Deleted
                            && ReferenceEquals(captured.FormerRider.MountAgent, captured.Horse)))
                    {
                        return Mission.CurrentTime >= captured.Deadline;
                    }

                    captured.Horse.FadeOut(hideInstantly: true, hideMount: false);
                }

                captured.Released = true;
            }
        }
        catch (Exception exception)
        {
            AdimiToolsConsoleLog.Log($"Captured duel mount cleanup failed: {exception.Message}");
        }

        // An unresolved native riding link must not lock either player out.
        return captured.Released || Mission.CurrentTime >= captured.Deadline;
    }

    private AdimiToolsDuelSpawnClass GetSpawnClass(BasicCharacterObject character)
    {
        return character.DefaultFormationClass switch
        {
            FormationClass.Infantry => AdimiToolsDuelSpawnClass.Infantry,
            FormationClass.HeavyInfantry => AdimiToolsDuelSpawnClass.Infantry,
            FormationClass.Ranged => AdimiToolsDuelSpawnClass.Ranged,
            FormationClass.Cavalry => AdimiToolsDuelSpawnClass.Cavalry,
            FormationClass.HorseArcher => AdimiToolsDuelSpawnClass.Cavalry,
            FormationClass.LightCavalry => AdimiToolsDuelSpawnClass.Cavalry,
            FormationClass.HeavyCavalry => AdimiToolsDuelSpawnClass.Cavalry,
            _ => AdimiToolsDuelSpawnClass.Unknown,
        };
    }

    private void HandleDuelAgentVisualSpawning(NetworkCommunicator spawningNetworkPeer, AgentBuildData spawningAgentBuildData, int troopCountInFormation = 0, bool useCosmetics = true)
    {
        MissionPeer component = spawningNetworkPeer.GetComponent<MissionPeer>();
        GameNetwork.BeginBroadcastModuleEvent();
        GameNetwork.WriteMessage(new SyncPerksForCurrentlySelectedTroop(spawningNetworkPeer, component.Perks[component.SelectedTroopIndex]));
        GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.ExcludeOtherTeamPlayers, spawningNetworkPeer);
        component.HasSpawnedAgentVisuals = true;
        component.EquipmentUpdatingExpired = false;
        if (useCosmetics)
        {
            GameMode.AddCosmeticItemsToEquipment(spawningAgentBuildData.AgentOverridenSpawnEquipment, GameMode.GetUsedCosmeticsFromPeer(component, spawningAgentBuildData.AgentCharacter));
        }

        if (!GameMode.IsGameModeHidingAllAgentVisuals)
        {
            GameNetwork.BeginBroadcastModuleEvent();
            GameNetwork.WriteMessage(new CreateAgentVisuals(spawningNetworkPeer, spawningAgentBuildData, component.SelectedTroopIndex, troopCountInFormation));
            GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.ExcludeOtherTeamPlayers, spawningNetworkPeer);
        }
        else if (!spawningNetworkPeer.IsServerPeer)
        {
            GameNetwork.BeginModuleEventAsServer(spawningNetworkPeer);
            GameNetwork.WriteMessage(new CreateAgentVisuals(spawningNetworkPeer, spawningAgentBuildData, component.SelectedTroopIndex, troopCountInFormation));
            GameNetwork.EndModuleEventAsServer();
        }
    }
}
