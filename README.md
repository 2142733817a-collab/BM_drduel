Code for the Mount & Blade II: Bannerlord 'DR Duel' server which is a copy of the Mount & Blade Warband Duel system.  
The code was not formatted / refactored etc. since it was created out of curiosity and trying around what is possible while staying Native compatible. It was not supposed to be released at all.

Some small parts (messages / reflections) might be copied by krex his Admin tool or from takeo (cRPG).

## FT3 / FT5 / FT7 scene objects

In a scene running this server's Duel mode, place three weapon-hittable objects with collision and give each object exactly one of these tags:

| Tag | Personal duel mode |
| --- | --- |
| `adimi_ft3_anvil` | First to 3 wins |
| `adimi_ft5_anvil` | First to 5 wins |
| `adimi_ft7_anvil` | First to 7 wins |

Hit an object to select its mode. Hit the same mode's object again to return to FT1; hit another mode's object to switch directly. An object with multiple FT tags is disabled. Use the same hittable entity arrangement as the existing FT7 anvils; entity names do not select the mode. Existing FT7 tags and the script name remain supported.

Both players must select the same win limit to request and accept a duel. The limit is fixed when the duel is accepted and cannot be changed during the series. FT3 means first to 3 wins, not best of 3. The existing `!ft 1` through `!ft 10` commands remain available and follow the same matching rule. FT3, FT5 and FT7 series all announce the final result.

These tags require the server code; Native alone does not implement them. The example scenes are unchanged, so place and label the three objects in your own map before testing in game.

## 传送门标签 1–20

现有服务器代码按 `teleport_door_` 前缀查找路线标签，再按完整标签匹配落点，没有将编号限制为 1–4。因此 `teleport_door_1` 至 `teleport_door_20` 均可沿用现有逻辑；1–4 只是示例地图使用的编号，并非代码上限。

以第 20 条路线为例：

| 对象 | 必须设置的标签 |
| --- | --- |
| 传送门 | `teleport_door`、`teleport_door_20` |
| 独立落点箭头 | `teleport_target`、`teleport_door_20` |

新增编号可使用 `teleport_door_5`、`teleport_door_6`、`teleport_door_7`、`teleport_door_8`、`teleport_door_9`、`teleport_door_10`、`teleport_door_11`、`teleport_door_12`、`teleport_door_13`、`teleport_door_14`、`teleport_door_15`、`teleport_door_16`、`teleport_door_17`、`teleport_door_18`、`teleport_door_19`、`teleport_door_20`。

复制现有可交互门及独立落点后，替换各自原有的编号标签，每个实体只保留一个路线标签。每条路线保留一个有效落点；同号门传送到同号箭头，实体名称不参与配对。落点箭头须保持独立，不能同时带 `teleport_door` 标签。双向通道需要两条路线，例如门 19 指向箭头 19、门 20 指向箭头 20，将两个箭头分别放到对面的出口处。

`is_section`、`section_a` / `section_b` 与传送编号无关，应根据地图出生区域配置。新增编号的匹配规则已经过源码检查，实际交互、落点朝向和传送效果仍需在服务器中验证。

## 按兵种设置出生点

在场景里复制原有出生点箭头，保留 `spawnpoint` 标签，再给每个点添加以下一个兵种标签：

| 标签 | 选兵类别 |
| --- | --- |
| `spawn_troop_1` | 步兵、重步兵 |
| `spawn_troop_2` | 弓箭手、弩手 |
| `spawn_troop_3` | 骑兵、骑射、轻骑兵、重骑兵 |

同一类别可以放多个点，服务器使用原生出生点评分选择位置。一个点同时有多个兵种标签时，不作为专属点使用；某类别没有有效专属点时，保留原来的分区逻辑：有死亡记录时取原有最近分区出生点，普通入场回到 A 区。仍遵循 `exclude_mounted` / `exclude_footmen`。分类依据是选中的角色类别，不依赖当前手中武器。

正常入场、普通死亡及系列结束后的复活走分类出生点。FT 系列进行中的死亡仍优先使用原有决斗原位复活逻辑。没有改动地图；需要在自己的场景里放置并保存这些标签。

此功能只分配出生位置，原服去头盔、去坐骑和禁止远程武器的规则继续保留。骑兵类即使被本服去掉坐骑，仍按选中的骑兵类别进入 `spawn_troop_3`。

## 原版客户端的距离决斗提示

服务器默认每 0.5 秒检查一次距离：超过 55 米隐藏头顶决斗提示，靠近到 50 米以内恢复。50–55 米之间保持上次状态，减少边界反复切换。每次每名观察者最多处理 8 个变化，优先恢复靠近的目标；距离未变化到需要切换时不重复发消息。

玩家不需要安装客户端 Mod。服务器只向对应观察者发送原版消息，使用临时个人对局状态的覆盖与结束来保留目标隐藏缓存，不新增虚假进行中对局行，不改变真实 Team、生命值或位置。每条消息使用独立可靠事件；不能保证原版客户端把整组消息在同一帧显示完毕。

正在准备、进行 FT 系列或有待处理挑战的观察者暂停更新；真实对局中的目标不会因靠近而恢复提示。真实系列结束后重新计算其提示状态；重连和断线会清理对应观察关系。发送异常时停止新的距离更新，并在观察者空闲后尝试恢复原版提示，失败会记入服务器日志并等待下次清理。

当前实现保守接受 `area_flag_1` 到 `area_flag_5`（使用编号 0–4）；没有这些有效场地编号时功能自动停用。这不是协议上限：原版该字段为 4 位，可编码 0–15。这里的场地编号与传送门编号 1–20 是不同用途。

设置服务器进程环境变量 `ADIMI_DUEL_DISTANCE_MARKERS=0` 可在下次载入场景时关闭此功能。当前代码验证不包含游戏运行；消息分帧时是否闪出个人面板、多人场景下开销及完整联机效果仍需实际客户端验收。

## 攻击触发的 AI 训练

每组最少放两个独立对象，各自添加一个角色标签和相同的分组标签。例如第 1 组：

| 对象 | 角色标签 | 分组标签 | 是否必需 |
| --- | --- | --- | --- |
| 可被武器击中的触发物 | `adimi_ai_trigger` | `adimi_ai_group_1` | 必需 |
| AI 出生位置标记 | `adimi_ai_spawn` | `adimi_ai_group_1` | 必需 |
| 预埋在地下的障碍物 | `adimi_ai_barrier` | `adimi_ai_group_1` | 可选 |
| 挂原版 `VolumeBox` 的区域对象 | `adimi_ai_area` | `adimi_ai_group_1` | 可选 |

**没有障碍物就只生成 AI；没有区域对象就不检测、驱离其他玩家，也不执行越界判负。** 两个可选对象互不依赖，四种组合都支持。第 2 组统一改为 `adimi_ai_group_2`，之后使用规范的正整数编号。同组每种角色最多一个；不可在一个对象上放多个角色或多个分组标签。缺少必需角色、重复角色或已标记的可选组件配置错误会在日志中说明并停用该组，其余组不受影响。

触发物需要可被武器打击的实体碰撞。服务器自动挂载命中观察器，地图内不需要挂本 Mod 的自定义脚本，也不需要玩家安装客户端 DLL。不要同时保留 FT 铁砧、传送门标签；推荐普通非破坏性物品，避免子实体脚本抢占命中。AI 标记使用独立箭头/空实体，不能带普通玩家出生用的 `spawnpoint` 标签；标记附近需要可行走的 AI 导航网格和足够站位。

敲击一次预约本组并提示 3 秒后生成 1 个 AI；同一玩家继续有效敲击时增加数量，并从最后一次有效敲击重新倒计时 3 秒。0.25 秒内重复命中按一次处理，每次最多 10 个 AI。AI 使用首次敲击时玩家的当前装备快照和相同角色，包含后来从武器架取得的武器与当前坐骑；不是完整复制所有玩家特长效果。AI 拥有独立装备，只与触发者敌对、锁定该玩家，不与同组 AI、路人或其他组玩家敌对。

**AI 对局规则：1 对 1 沿用玩家的 FT 设置（铁砧 FT3/FT5/FT7 或 `!ft 1–10`）；1 对 2 个及以上 AI 强制 FT1，一局定胜负。** 规则在首次生成 AI 时固定，系列中修改个人 FT 不影响正在进行的系列，也不会为了多 AI 对局将玩家个人 FT 改成 1。FT3 是先赢 3 局，不是三局两胜。多 AI 局必须击败全部 AI 才算玩家获胜，击败其中一个不会结束；玩家先阵亡则整局失败，同时死亡也按玩家失败结算。

1 对 1 的 FT 系列每局单独计分，未达到目标胜场时**只复活败者，保留胜者实体和当前位置**：玩家阵亡时，存活 AI 及其坐骑保留，玩家通过原版流程在本组 `adimi_ai_spawn` 标记处复活；AI 阵亡时，在该标记处生成替代 AI，存活玩家留在当前位置，不拉回首次敲击点。点位被活体占用时选择标记附近有效导航位置，不移动幸存者。双方准备好后，按原服恢复函数补生命、盾牌、箭和弩矢，再友好等待 3 秒后继续交战。等待期间保留区域占用及升起的障碍物，存活 AI 暂停锁定旧玩家，开局时改为锁定复活后的新玩家角色。

玩家复活仍使用本次训练的角色和装备快照，位置优先于普通按兵种/分区出生点；原来的 AI 出生标记直接复用，不增加必需组件。30 秒内未完成复活则取消系列。同时死亡按玩家本局失败计分，两方均死亡时在玩家回来后补出 AI，再共同等待 3 秒。只有整场胜负已定或挑战取消时才清理本组 AI、释放区域、下降障碍物；正常死尸仍使用 10 秒消失配置。

障碍物如有设置，需在场景中预先挂好原版 `SynchedMissionObject`（不是城门/可破坏物的派生脚本），设置为可移动的动态实体并保留有效碰撞。地图保存的位置就是地下起始位置。首次敲击后沿**世界 Z 轴向上 3 米，耗时 1.5 秒**；整场对局/FT 系列结束（包含越界判负），或挑战者断线、换队等导致取消后，从当前位置用 1.5 秒回到保存的起点，复位期间不能再次预约。不要给障碍物挂服务端自定义同步脚本或手工修改对象 ID。

区域如有设置，使用原版 `volume_box` 完整对象，保留 `VolumeBox` 脚本，调整范围覆盖挑战区域、触发者和 AI 标记。预约开始到挑战结束期间，其他存活的人类玩家进入区域会被移至所有 AI 区域之外的普通 `spawnpoint`；机器人、坐骑不作为闯入玩家。地图必须保留区域外有效出生点。若区域内已有其他正在 PvP/AI 挑战的玩家，会拒绝或取消本组挑战，不会强行移动另一场对局的参与者。

**AI 挑战开始后，存活的触发者离开本组检测盒子，直接判整场挑战失败，清理本组 AI 和 AI 坐骑并降下障碍物，不继续下一局。** 服务器沿用每 0.25 秒的区域检查；FT 局间倒计时及复活后的准备时间也受此规则约束，越界优先于同次检查中的 AI 死亡结算。判负不伪造双方已赢局数，也不修改玩家个人 FT 设置；死亡角色或等待复活的旧尸体不参与越界判定。首次生成 AI 前的 3 秒预约期离开区域只取消预约。没有区域对象时不执行这些区域检查。

同一玩家不能同时预约多组或参加 PvP。每组复用自己的玩家/AI 私有队伍，结束后恢复玩家及原坐骑的队伍；失败的清理步骤保留引用并重试。AI 及其坐骑在取消时清理；正常死亡的尸体使用本决斗场景的 **10 秒消失设置**。这不会要求修改 Native 模块或安装客户端 Mod。

当前仅完成编译、离线行为和接线检查，真实命中、导航、AI 战斗、动态碰撞、原版客户端提示及联网同步需实机验收。场景和游戏安装目录未自动修改。

## 构建

使用当前 Bannerlord Dedicated Server 的 DLL，设置 `MB_SERVER_PATH` 后构建整个 `AdimiDuel_Server.sln`。默认构建仅输出本地文件，不再自动覆盖专服目录；只有显式指定 `DeployToBannerlordServer=True` 才复制模块文件。`BannerlordValidationBuild=True` 模式禁止部署，并要求将输出和中间文件指向专服之外的绝对路径。
