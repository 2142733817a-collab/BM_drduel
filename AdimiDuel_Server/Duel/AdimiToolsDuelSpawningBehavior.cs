using AdimiToolsHelper;
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
            bool isAiRoundRespawn = missionPeer != null && aiArena?.GetRoundRespawnFrame(missionPeer).HasValue == true;
            if (missionPeer == null || missionPeer.ControlledAgent != null || !missionPeer.HasSpawnedAgentVisuals
                || (!isAiRoundRespawn && CanUpdateSpawnEquipment(missionPeer)))
            {
                continue;
            }

            if (aiArena?.ShouldDelayRespawn(missionPeer) == true)
            {
                continue;
            }

            if (isAiRoundRespawn)
            {
                // Only an owned, unfinished AI series bypasses the normal
                // loadout confirmation wait, after its server countdown.
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
                uint color = (!GameMode.IsGameModeUsingOpposingTeams || missionPeer.Team == Mission.AttackerTeam) ? missionPeer.Culture.Color : missionPeer.Culture.ClothAlternativeColor;
                uint color2 = (!GameMode.IsGameModeUsingOpposingTeams || missionPeer.Team == Mission.AttackerTeam) ? missionPeer.Culture.Color2 : missionPeer.Culture.ClothAlternativeColor2;
                uint color3 = (!GameMode.IsGameModeUsingOpposingTeams || missionPeer.Team == Mission.AttackerTeam) ? missionPeer.Culture.BackgroundColor1 : missionPeer.Culture.BackgroundColor2;
                uint color4 = (!GameMode.IsGameModeUsingOpposingTeams || missionPeer.Team == Mission.AttackerTeam) ? missionPeer.Culture.ForegroundColor1 : missionPeer.Culture.ForegroundColor2;
                Banner banner = new(missionPeer.Peer.BannerCode, color3, color4);
                agentBuildData = new AgentBuildData(basicCharacterObject).VisualsIndex(0).Team(missionPeer.Team).TroopOrigin(new BasicBattleAgentOrigin(basicCharacterObject))
                    .Formation(missionPeer.ControlledFormation)
                    .IsFemale(missionPeer.Peer.IsFemale)
                    .ClothingColor1(color)
                    .ClothingColor2(color2)
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
            } // Rerandomize colors on respawn
            else if (GameMode is AdimiToolsMissionMultiplayerDuel duelGamemode)
            {
                FactionColor fc = missionPeer.GetNetworkPeer().IsAdmin ? AdminColor : duelGamemode.PlayerColors.GetRandomElement();
                uint color1 = BannerManager.GetColor(fc.PrimaryColor);
                uint color2 = BannerManager.GetColor(fc.IconColor);

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
                    // Only re-randomize colors when spawning after a duel.
                    agentBuildData.ClothingColor1(color1)
                        .ClothingColor2(color2)
                        .Banner(new Banner(missionPeer.GetNetworkPeer().VirtualPlayer.BannerCode, color1, color2));

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
            RemoveUnallowedItems(ref agentBuildData); // Remove unallowed items etc. - also used to remove cosmetic only items (i.e clan warrior who doesnt ahve a helmet by defualt)
            aiArena?.ConfigureRoundRespawn(missionPeer, agentBuildData);

            MatrixFrame? getSpawnFrame;
            MatrixFrame? aiRoundRespawn = aiArena?.GetRoundRespawnFrame(missionPeer);
            if (playerDuelData != null && playerDuelData.RespawnFrame != null) // Spawn here during a duel
            {
                getSpawnFrame = (MatrixFrame)playerDuelData.RespawnFrame!;
            }
            else if (aiRoundRespawn.HasValue)
            {
                getSpawnFrame = aiRoundRespawn.Value;
            }
            else if (PlayersDuelConfig.TryGetValue(missionPeer.Peer.Id, out DuelConfig? duelConfig) && duelConfig.DeathFrame != null) // Use this after duels
            {
                AdimiToolsDuelSpawnClass spawnClass = GetSpawnClass(mPHeroClassForPeer);
                AdimiToolsDuelSpawnFrame spawnFrameBehavior = (AdimiToolsDuelSpawnFrame)SpawnComponent.SpawnFrameBehavior;
                getSpawnFrame = spawnFrameBehavior.GetTroopSpawnFrame(spawnClass, missionPeer.Team, false) ?? spawnFrameBehavior.GetBestRespawn(duelConfig.DeathFrame.Value.origin);
            }
            else
            {
                AdimiToolsDuelSpawnClass spawnClass = GetSpawnClass(mPHeroClassForPeer);
                AdimiToolsDuelSpawnFrame spawnFrameBehavior = (AdimiToolsDuelSpawnFrame)SpawnComponent.SpawnFrameBehavior;
                getSpawnFrame = spawnFrameBehavior.GetTroopSpawnFrame(spawnClass, missionPeer.Team, false) ?? spawnFrameBehavior.GetRandomSectionSpawn("A");
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

            uint color1 = missionPeer.Culture.Color;
            uint color2 = missionPeer.Culture.Color2;
            if (GameMode is AdimiToolsMissionMultiplayerDuel duelGamemode)
            {
                FactionColor fc = duelGamemode.PlayerColors.GetRandomElement();
                color1 = BannerManager.GetColor(fc.PrimaryColor);
                color2 = BannerManager.GetColor(fc.IconColor);
            }

            AgentBuildData agentBuildData = new AgentBuildData(heroCharacter).MissionPeer(missionPeer).Equipment(equipment).Team(missionPeer.Team)
                .TroopOrigin(new BasicBattleAgentOrigin(heroCharacter))
                .IsFemale(missionPeer.Peer.IsFemale)
                .BodyProperties(GetBodyProperties(missionPeer, missionPeer.Culture))
                .VisualsIndex(0)
                .ClothingColor1(color1)
                .ClothingColor2(color2)
                .Banner(new Banner(missionPeer.GetNetworkPeer().VirtualPlayer.BannerCode, color1, color2));

            // RemoveUnallowedItems(ref agentBuildData); // Now called in HandleDuelAgentVisualSpawning

            if (!_buildAgentData.TryAdd(networkPeer.VirtualPlayer.Id, agentBuildData))
            {
                _buildAgentData[networkPeer.VirtualPlayer.Id] = agentBuildData;
            }

            if (GameMode.ShouldSpawnVisualsForServer(networkPeer) && agentBuildData.AgentVisualsIndex == 0)
            {
                missionPeer.HasSpawnedAgentVisuals = true;
                missionPeer.EquipmentUpdatingExpired = false;
            }

            HandleDuelAgentVisualSpawning(networkPeer, agentBuildData);
        }
    }

    private void RemoveUnallowedItems(ref AgentBuildData agentBuildData)
    {
        agentBuildData.AgentData.AgentOverridenEquipment[EquipmentIndex.Head] = EquipmentElement.Invalid; // Remove head armor
        agentBuildData.AgentData.AgentOverridenEquipment[EquipmentIndex.Horse] = EquipmentElement.Invalid; // Remove horse

        for (EquipmentIndex i = EquipmentIndex.Weapon0; i < EquipmentIndex.NumPrimaryWeaponSlots; i++) // Remove all kind of ranged weapons
        {
            EquipmentElement? equipmentElement = agentBuildData.AgentData.AgentOverridenEquipment[i];
            if (equipmentElement != null && equipmentElement?.Item != null && !EquipmentElement.Invalid.IsEqualTo(equipmentElement.Value))
            {
                foreach (WeaponComponentData weaponComponentData in equipmentElement.Value.Item.Weapons)
                {
                    if (weaponComponentData.IsAmmo || weaponComponentData.IsBow || weaponComponentData.IsCrossBow || weaponComponentData.IsConsumable)
                    {
                        agentBuildData.AgentData.AgentOverridenEquipment[i] = EquipmentElement.Invalid;
                        continue;
                    }
                }
            }
        }
    }

    private AdimiToolsDuelSpawnClass GetSpawnClass(MultiplayerClassDivisions.MPHeroClass heroClass)
    {
        return heroClass.HeroCharacter.DefaultFormationClass switch
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

        RemoveUnallowedItems(ref spawningAgentBuildData);

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
