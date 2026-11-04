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
    private readonly Dictionary<PlayerId, AgentBuildData> _buildAgentData;

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
            AdimiToolsAiArenaBehavior? aiArena = Mission.GetMissionBehavior<AdimiToolsAiArenaBehavior>();
            bool hasPendingAiRespawn = missionPeer != null && aiArena?.HasPendingAiRespawn(missionPeer) == true;
            if (missionPeer == null || missionPeer.ControlledAgent != null || !missionPeer.HasSpawnedAgentVisuals
                || (!hasPendingAiRespawn && CanUpdateSpawnEquipment(missionPeer)))
            {
                continue;
            }

            if (aiArena?.ShouldDelayRespawn(missionPeer) == true)
            {
                continue;
            }

            if (hasPendingAiRespawn)
            {
                // AI series rounds and completed-series death-frame recovery
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
            // Keep native class/perk gear for ordinary spawns. An AI recovery
            // owns its saved banner and equipment after the country default.
            AdimiToolsDuelAppearance.ApplyToBuild(agentBuildData, missionPeer.Culture);
            aiArena?.ConfigureRoundRespawn(missionPeer, agentBuildData);
            bool hasMount = !agentBuildData.AgentOverridenSpawnEquipment[EquipmentIndex.Horse].IsEmpty;

            MatrixFrame? getSpawnFrame;
            MatrixFrame? aiRoundRespawn = aiArena?.GetRoundRespawnFrame(missionPeer);
            bool regularCompletionSpawn = aiArena?.UsesRegularCompletionSpawn(missionPeer) == true;
            if (aiRoundRespawn.HasValue)
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

            if (spawnFrame.IsIdentity && !aiRoundRespawn.HasValue)
            {
                AdimiToolsConsoleLog.Log($"Trying to spawn case spawnFrame.IsIdentity {missionPeer.Name}");
                continue;
            }

            agentBuildData.InitialPosition(in spawnFrame.origin);
            Vec2 value = spawnFrame.rotation.f.AsVec2.Normalized();
            agentBuildData.InitialDirection(in value);

            Agent agent = Mission.SpawnAgent(agentBuildData, spawnFromAgentVisuals: true);
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
        }

        // base.OnTick(dt);
    }

    protected override void SpawnAgents()
    {
        foreach (NetworkCommunicator networkPeer in GameNetwork.NetworkPeers)
        {
            if (!networkPeer.IsSynchronized)
            {
                continue;
            }

            MissionPeer missionPeer = networkPeer.GetComponent<MissionPeer>();
            if (!(missionPeer.Representative is DuelMissionRepresentative) || !networkPeer.IsSynchronized || missionPeer.ControlledAgent != null || missionPeer.HasSpawnedAgentVisuals || missionPeer.Team == null || missionPeer.Team == Mission.SpectatorTeam || !missionPeer.TeamInitialPerkInfoReady || missionPeer.Culture == null || !missionPeer.SpawnTimer.Check(Mission.Current.CurrentTime))
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
            if (hasPendingAiRespawn)
            {
                // The original client must receive the same saved equipment
                // in its visuals packet as the server's later real spawn.
                aiArena!.ConfigureRoundRespawn(missionPeer, agentBuildData);
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

            HandleDuelAgentVisualSpawning(networkPeer, agentBuildData, useCosmetics: !hasPendingAiRespawn);
        }
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
