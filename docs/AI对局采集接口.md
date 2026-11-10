# AI 对局采集接口

本文供独立开发回放、数据分析和后续训练程序的人对接使用，依据本项目当前 C# 源码整理。它描述的是服务器 Mod 的实际数据接口，不代表已经约定了神经网络模型的输入、输出或部署协议。请以运行服务器所使用的 Mod 版本源码为准；本文不意味着服务器已经部署或完成实战验证。

## 数据位置和文件

服务器进程的环境变量 `ADIMI_AI_TRAINING_PATH` 指定**数据目录**，不是 JSONL 文件名。未设置或为空白时，默认目录是 `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Logs", "AiTraining"))`。相对目录会按服务器进程的工作目录解析，部署时建议使用绝对路径。设置环境变量后，需让运行 Mod 的服务器进程实际继承该值。

| 文件 | 用途 |
| --- | --- |
| `episodes.jsonl` | 按回合追加的 UTF-8 JSON Lines 日志，同一文件可混用普通 JSON 行和压缩信封行。 |
| `reviews.json` | 同目录下的管理员排除清单；缺失视为空清单，不改变原始日志。 |
| `reviews.applied.sha256` | Mod 成功重建并发布与某份复核文件对应的策略后写出的回执；内容和限制见下文。 |
| `episodes-incomplete-<GUID>.partial` | 启动或追加时恢复的末尾未完整换行数据可被备份到这里；它不是已提交回合。 |

例如，Windows PowerShell 中可在启动服务器的同一进程环境设置：

```powershell
$env:ADIMI_AI_TRAINING_PATH = 'D:\BannerlordData\AiTraining'
# 再从继承此环境的进程启动服务器；已有服务器进程不会自动获得新值。
```

日志有异步写入队列；回合结束不代表数据已成功落盘。入队、序列化或写入失败、队列满、异常终止都可能造成记录未保存。启动时写入工作线程先扫描并恢复已有日志，再消费待写回合；这段时间队列仍最多容纳 8 条待处理记录，满时新回合会被丢弃。写入器成功追加并刷新到磁盘后，才在进程内部更新已保存计数和提交位置。读取工具应复制备份，或只读取已经以 LF 结束的完整行，**不要修改正在写入的日志**。但外部读者看到 LF 只说明行在读取时完整，并不能证明写入器的 `fsync` 已成功：对外持久提交偏移及文件身份协议目前尚未接入。外部同步或训练应自行处理未确认行的回滚、重复、文件截断和轮转，不能将换行单独当作持久提交证明。

## `episodes.jsonl` 的行格式

- 每行是一局，使用 UTF-8，行尾是 LF（`0x0A`）；读取器兼容一行末尾的单个 CR 加 LF，文件开头可接受 UTF-8 BOM。未结束的最后一行不是已提交记录。独立的 CR 不作为分行符。
- 旧行直接保存回合 JSON 对象。当前写入器先序列化同一回合，再尝试单局 gzip；压缩信封不比原文更小或超过字节上限时，退回保存普通 JSON 行。两种行可能交错。
- 物理行和解压后的原始 JSON 各不得超过 **16 MiB（16,777,216 字节）**；单个字符串的 Base64 字符数也受上限约束。读取时应在解压前、解压过程中和解压后分别限制资源，不能先无限解压再检查。
- 回合读取要求对象内 `Id` 能解析为 GUID，`Valid`、`SoloAttributedWin` 是明确的 JSON 布尔值；空行忽略。格式错误的行不作为回合。日志 GUID 唯一身份上限为 1,000,000：运行中达到上限后新身份记录会被丢弃；已有日志超过上限时 Mod 的初始化/重建不能正常继续，不能假定其余数据已被处理。

压缩行结构如下，尖括号为说明性占位符，并非可解码样本：

```json
{"Format":"adimi-gzip-json-v1","RawBytes":1234,"Sha256":"<原始 UTF-8 字节的 64 位小写十六进制 SHA-256>","Payload":"<gzip 字节的标准 Base64>"}
```

| 信封字段 | 含义与校验 |
| --- | --- |
| `Format` | 必须精确等于 `adimi-gzip-json-v1`。当前 Mod 将任何包含此字段的顶层对象当信封，未知格式拒绝，不按旧回合解释。 |
| `RawBytes` | 解压后原始 UTF-8 字节长度，整数范围 1 至 16 MiB，必须与实际长度一致。 |
| `Sha256` | 原始未压缩字节的 SHA-256；恰好 64 个小写十六进制字符，解压后必须匹配。 |
| `Payload` | 标准、规范的 Base64 字符串，解码为单个完整 gzip 成员；须检查 gzip 头、校验和、尾部长度，拒绝损坏、额外尾随数据、拼接成员和嵌套信封。 |

先解析外层 JSON；若有 `Format`，验证并解压 `Payload`、核对字节长度和摘要，再解析内部 JSON。内部对象不允许再包含 `Format`。无 `Format` 的普通回合则直接解析。坏行应单独报告，不能当成有效数据。

Mod 读取时按 GUID 去重：**第一条结构合格的记录占用该 GUID，即使其 `Valid=false`；之后相同 GUID 不再重复计数或训练**。已保存有效回合数是去重后 `Valid=true` 的数量；单人可归因胜数还要求 `SoloAttributedWin=true`，且 `MatchType` 是 `ai` 或旧记录缺少 `MatchType`。`reviews.json` 不会改变这些原始存档统计。有效回合并不必然是训练样本。

## 何时产生一局

- `MatchType="ai"`：AI 训练场开始挑战及其后续回合时创建采集。当前包括普通、长矛、射手、骑射手、骑兵训练场和不同难度，也包括多人对 AI 的回合；不是只有困难难度才采集。玩家为 `Side=0`，AI 为 `Side=1`。
- `MatchType="pvp"`：玩家单挑，发起方为 `Side=0`，受邀方为 `Side=1`。
- `MatchType="team_pvp"`：玩家团队战，A 队为 `Side=0`，B 队为 `Side=1`，不限制成两名角色。

具体能否采集取决于相应对局行为是否已在当前服务器启用、回合是否成功开始，以及采集/写入过程是否成功。不是服务器上所有任意战斗都会自动成为训练回合。PvP 和团队 PvP 的角色均为 `IsPlayer=true`，应使用 `Side` 区分双方，不能用 `IsPlayer` 给两队分色。

## 回合字段

类型为当前 C# DTO 类型；旧记录可能缺少后来加入的字段，读取时不能无条件假设字段完整。

| 字段 | 类型和含义 |
| --- | --- |
| `SchemaVersion` | `int`，当前新采集为 `2`。旧记录没有此字段时 Mod 将版本视为未知的 `0`，不能假装它符合版本 2。 |
| `Id` | `string`，回合 GUID；新记录为 32 位十六进制 `N` 格式，无连字符。用于去重与复核。 |
| `MatchType` | `string`，`ai`、`pvp`、`team_pvp`。旧记录缺少时按 AI 兼容；未知值不应猜测。 |
| `ArenaId` | `int`，本地图/场景中的场地标识，不应当作跨地图、跨版本稳定的全局 ID。 |
| `ArenaKind` | `int`，AI 训练场：`0` 普通、`1` 长矛、`2` 射手、`3` 骑射手、`4` 骑兵。PvP 当前未赋值，默认 `0` 并不表示它是普通 AI 训练场。 |
| `Difficulty` | `int`，AI 训练场：`1` 简单、`2` 一般、`3` 困难。PvP 当前默认 `0`，不表示简单难度。 |
| `WinningSide` | `int`，`0` 或 `1` 为获胜方，`-1` 为未知。不能仅凭数值判断回合有效。 |
| `StartedUtc` | `string`，回合开始时 UTC 时间，当前使用 .NET `DateTime.UtcNow.ToString("O")`。 |
| `DurationSeconds` | `double`，相对回合开始的秒数；AI 对局胜负被确认时取失败方最后一人的确认离场时间，否则取结束时已过去的时间。PvP 在此基础上还会取所有事件时间的最大值，避免末尾事件晚于记录时长。 |
| `Outcome` | `string`。AI 对局为 `player_win`、`ai_win`、`aborted`；PvP 为 `side_a_win`、`side_b_win`、`aborted`。 |
| `InitialPlayers`、`InitialBots` | `int`，开始时参与玩家和 AI 数量；PvP 中 `InitialBots=0`。 |
| `Valid` | `bool`，采集器判定该回合可作为有效存档；其规则见下文，不等于必然被当前训练器采用。 |
| `SoloAttributedWin` | `bool`，采集器仅在有效 AI 回合中恰好一名玩家对一名 AI、玩家获胜且 AI 的击倒可归因于该玩家时设为真；PvP 不计入。后续复制截断可令 `Valid=false` 而保留此值，统计时必须同时检查 `Valid`。 |
| `Truncated` | `bool`，采集或复制过程中超过限制等导致回合不完整；一旦截断，`Valid` 被置为 `false`。 |
| `Frames` | `array`，时间顺序的角色快照，字段见下。 |
| `Events` | `array`，命中、离场、骑乘和射击等事件，字段见下。 |

## 帧和角色字段

`Frames` 中每项的 `Seconds` 是从回合开始计的秒数，类型为 `double`；`Actors` 是此刻记录到的角色快照数组。首帧要求包含初始参与者，之后已离场者可不再出现。`Actors` 并不包含独立的坐骑角色快照，坐骑信息附在骑手上。

AI 对局的确认离场与回合收尾可能相隔一个或多个更新；日志中个别帧或事件时间可能晚于 `DurationSeconds`。当前训练器只使用结束时刻之前的有效帧和事件。消费者应保留原始时间，按自己的用途明确处理，而不要把这些记录默认为精确复现。

以下均是 `Actors` 内字段，类型 `float` 的数值来自游戏引擎；Mod 没有额外进行单位换算。`X/Y/Z` 是引擎世界坐标，通常以米理解，但接口消费者应以对应引擎版本约定为准；速度是引擎速度向量，不应将角色本地输入向量当作世界坐标速度。身体和坐骑速度只存 X、Y 两个分量。

| 字段 | 类型和含义 |
| --- | --- |
| `Id` | `int`，当前 Agent 的索引；只应在该局内关联，不是账号、也不是跨回合稳定 ID。 |
| `IsPlayer` | `bool`，是否玩家角色；PvP 的两队都是 `true`。 |
| `Side` | `int`，阵营 `0` / `1`；`-1` 表示未知。旧 AI 记录缺少阵营时，可仅对 AI 对局按 `IsPlayer` 回退推断。 |
| `PlayerToken` | `string`，临时随机令牌，玩家可能有值，AI 通常为空；关联范围和隐私限制见下。 |
| `CharacterId` | `string`，引擎角色定义的 `StringId`，不是玩家账户名。 |
| `WeaponId` | `string`，当前手持武器物品的 `StringId`；无武器或无法取得时为空。 |
| `WeaponClass` | `string`，当前武器使用方式的引擎武器类别名，例如 `TwoHandedSword`；缺失时为空。 |
| `WeaponLength` | `float`，`GetRealWeaponLength()` 返回的实际武器长度，使用引擎世界距离单位；不是 XML 的整数长度，Mod 不再换算。 |
| `X`、`Y`、`Z` | `float`，角色世界位置；俯视通常使用 X/Y，Z 为高度。 |
| `LookX`、`LookY`、`LookZ` | `float`，角色的引擎朝向向量。 |
| `VelocityX`、`VelocityY` | `float`，`GetRealGlobalVelocity()` 的水平分量，通常按世界距离单位/秒解释。 |
| `MoveInputX`、`MoveInputY` | `float`，角色 `MovementInputVector`；不是逐键按下/松开日志，也不保证与世界速度同坐标系。 |
| `Health`、`HealthLimit` | `float`，当前生命与生命上限，使用游戏生命值。 |
| `ActionType`、`ActionStage`、`ActionDirection` | `int`，分别为引擎 `GetCurrentActionType(1)`、`GetCurrentActionStage(1)`、`GetCurrentActionDirection(1)` 的原始枚举值。应按对应游戏版本解释，不要把其他动作类型的数值统统解释为攻击。 |
| `ActionProgress` | `float`，`GetCurrentActionProgress(1)` 原始进度值；缺少快照期间的变化。 |
| `MovementFlags`、`EventFlags` | `uint`，引擎移动与事件控制位标志的数值；不是完整输入事件流。 |
| `IsMounted` | `bool`，该帧是否骑乘。 |
| `MountId` | `int`，坐骑 Agent 索引；未骑乘时为 `-1`，只在局内用于关联。 |
| `MountX`、`MountY`、`MountZ` | `float`，坐骑世界位置。 |
| `MountLookX`、`MountLookY`、`MountLookZ` | `float`，坐骑朝向向量。 |
| `MountVelocityX`、`MountVelocityY` | `float`，坐骑世界速度的水平分量。 |
| `MountInputX`、`MountInputY` | `float`，坐骑的 `MovementInputVector`，不是逐键事件。 |
| `MountHealth`、`MountHealthLimit` | `float`，坐骑生命与上限。 |

未骑乘时 `MountId=-1`，坐骑数值通常为零。`ActionDirection` 在当前训练器判定为近战准备攻击后，使用引擎 `Agent.UsageDirection` 的 `0=上`、`1=下`、`2=左`、`3=右`；事件 `AttackDirection` 来自碰撞数据，是另一个来源，不能自行断言两者完全等价。

### 临时玩家令牌

当前实现为 `MissionPeer` 生成随机字节，存储时为 64 个小写十六进制字符；不同角色的 AI `PlayerToken` 通常为空。AI 训练场与 PvP 采集行为有各自的令牌字典：即使同一玩家，跨这两种行为也不保证相同。场景或任务清理、重连、更换 `MissionPeer`、服务器重启均可能改变令牌。它只可在相应令牌生存范围内帮助分组，不能作为账号、认证凭据、真实身份或跨重启稳定 ID。它仍是可能关联行为的数据，对外发布应按需要脱敏。

## 事件字段

`Events` 每项是同一结构；`Seconds` 是相对回合开始的 `double` 秒数。事件种类依赖引擎回调，未适用于该种类的字段可能为 `0`、`false`、空字符串或 `null`，不能把这些默认值当成已观察事实。事件数量有限、快照也可能漏掉动作；日志不含完整逐帧视频或完整输入。

| 字段 | 类型和含义 |
| --- | --- |
| `Kind` | `string`，当前包括 `hit`（角色受击）、`removal`（角色离场）、`mount_hit`（坐骑受击）、`mount_removal`（坐骑离场）、`mount`（上马）、`dismount`（下马）、`shot`（发射）。 |
| `Seconds` | `double`，该事件相对回合起点的秒数。 |
| `AttackerId`、`VictimId` | `int`，引擎 Agent 索引；按事件可能指角色或坐骑，未知/不适用时可为 `-1`。上马和下马的 `VictimId` 是骑手；射击的 `VictimId=-1`。 |
| `IsBlocked` | `bool`，命中回调报告是否被挡。 |
| `Damage` | `float`，命中回调报告的伤害数值，单位为游戏伤害/生命值。 |
| `RemovalState` | `int`，角色或坐骑离场时引擎 `AgentState` 原始枚举值。 |
| `IsMissile` | `bool`，命中是否为投射物。 |
| `ThrustTipHit` | `bool`，碰撞信息中的枪尖/刺击尖端命中标志。 |
| `AttackerWeaponClass` | `string`，命中时攻击武器的引擎类别。 |
| `IsAlternativeAttack` | `bool?`，是否为替代攻击；`null` 表示未取得，不能等同于 `false`。旧记录可能没有此字段。 |
| `AttackDirection`、`StrikeType` | `int`，来自引擎碰撞数据的攻击方向和打击类型枚举值。 |
| `AttackProgress` | `float`，碰撞数据报告的攻击进度。 |
| `WeaponSlot` | `int`，`shot` 回调报告的武器槽位枚举值。 |
| `ShotX`、`ShotY`、`ShotZ` | `float`，`shot` 回调报告的发射世界位置。 |
| `ShotVelocityX`、`ShotVelocityY`、`ShotVelocityZ` | `float`，`shot` 回调报告的发射速度向量，未额外换算。 |

没有记录每支投射物的完整路径，也没有提供可靠的发射事件与后续命中事件一一配对 ID。依靠快照和发射向量绘制的俯视回放只能是示意，不能当作实际游戏画面、逐键操作或精确弹道。

## 采样、有效性和上限

采样随初始参与人数和时长调整。令 `n=InitialPlayers+InitialBots`，时间 `t` 为本局开始后的秒数；基本间隔为 `n<=4` 时 `0.05` 秒、`5<=n<=8` 时 `0.10` 秒、`n>8` 时 `0.20` 秒。目标间隔：

```text
t < 120 秒： max(基本间隔, n / 80) 秒
t >= 120 秒：max(0.20, n * 0.08) 秒
```

例如两人局前 120 秒目标为 0.05 秒、之后 0.20 秒；四人局前 120 秒 0.05 秒、之后 0.32 秒。采样由游戏更新驱动，满足时间后才采集，下一次从当前时刻继续计算，不补采错过的帧；实际间隔可能更长。

| 限制 | 当前值和影响 |
| --- | --- |
| 最长时长 | 600 秒；超过时截断。 |
| 帧数 | 最多 8,192 帧。 |
| 角色快照数 | 所有帧中 `Actors` 数量之和最多 16,384；不是最多 16,384 个不同玩家。多人局可能较早达到此上限。 |
| 事件数 | 最多 4,096 个事件。 |
| 单行及原文 | 各最多 16 MiB；若最终序列化/编码无法满足限制，整局可能无法保存，不保证一定留下截断记录。 |
| 写入队列 | 默认容量 8，满载或异常会导致局被丢弃。 |

有效回合通常要求已明确获胜方、未被中断或标记异常、未截断、败方的离场均已确认、胜方至少一人仍在场，且至少有两帧。采集还检查角色、坐骑关系、事件归属和数值等一致性；掉线、中断、异常数值或无法确认离场均可能无效。采集上限或复制时字段超限可触发截断；无效或截断局仍可能保存以供排查，但不计有效训练样本。上限是保护边界，不代表每局一定采满。

复制时还限制字符串长度：`MatchType` 最多 32、`StartedUtc` 和 `Outcome` 最多 64、`CharacterId` 和 `WeaponId` 最多 128、`WeaponClass`、`Kind` 和 `AttackerWeaponClass` 最多 64；长度按 .NET 字符串的 UTF-16 代码单元计。超长或意外的 `null` 可能被裁剪/替换并标记截断。

## 人工排除和重建

`reviews.json` 为 UTF-8 JSON，对象版本为 `1`，`excluded` 是最多 25,000 个无连字符 GUID，整个文件不超过 1 MiB。建议使用当前 UI 写出的 32 位小写形式；Mod 按 GUID 解析去重。

```json
{"version":1,"excluded":["0123456789abcdef0123456789abcdef"]}
```

它仅决定训练重建时跳过哪些回合，不删除或更改 `episodes.jsonl`。外部管理工具应写入同目录临时文件、刷新并原子替换，且对并发更改做互斥，避免覆盖别人的改动或产生半文件。Mod 本身读取这个文件；网页管理的认证、网络访问和写入机制不属于此数据格式的保证。

写入和训练重建分别由两个后台工作线程执行；模型线程只扫描写入线程确认刷新到磁盘的**已提交日志前缀**。启动时先等待写入线程完成历史日志初始化；随后重建，并在新增已提交回合时按偏移追赶。模型扫描不会要求写入线程等扫描结束，但两者仍共用存储设备，不能保证零资源争用。复核文件的字节内容变化后，先撤销此前学习策略，再跳过排除项并重建。默认约每 5 秒检查一次清单，但扫描、排队和负载会增加延迟，**保存排除清单不代表策略已即时生效**。清单格式非法、超限或重建失败时会停止使用学习到的方向策略并重试；其他 AI 强化按自身逻辑运行，原始日志不因此删除。缺失清单按无排除处理。

当前回执 `reviews.applied.sha256` 的约定如下：

1. 内容是**精确 `reviews.json` 原始文件字节**的 SHA-256，64 个小写十六进制字符，后跟一个 LF；不是排除 ID 的规范化哈希。哪怕仅空格、换行或排序发生变化，摘要也不同。
2. 模型线程检查复核文件未变、追上当时已提交前缀并成功发布对应策略后，先写同目录临时文件、刷新，再替换回执。若当前清单不存在，则按空清单工作但不生成回执；少于 20 个合格样本时发布的策略可以为空，所以回执不代表必然已有学习效果。
3. 启动、复核变化、存储不可用或重建失败时，Mod 会尝试删除旧回执；回执删除或写入失败会记录警告并稍后重试，不阻止对局记录。因异常退出或删除失败，旧回执可能残留。
4. 外部程序只能在读取并比较当前 `reviews.json` 原始字节哈希与回执一致时，将其作为该内容**曾完成重建并发布的提示**；应处理两次读取之间文件变化、残留回执和进程不再运行的情况。它不证明当前服务器健康、不保证所有 AI 都已实际采用策略，也不是日志的持久提交偏移、文件身份或外部同步协议。

## 当前自动学习实际范围

这里的学习器是 Mod 内的有界统计策略，不是神经网络模型，也没有确定可供新训练程序交付的神经网络模型文件协议。它严格筛选采集数据，归纳四个近战准备攻击方向，并按战斗表现等计算权重；达到 20 个合格回合后构建策略，之后每新增 5 个合格回合更新一次。重启后从已提交日志与复核清单重建，复核或重建失败时停止使用学习到的方向策略。

目前输入可以包含符合严格条件的困难普通 AI 训练场玩家胜局，也可以包含符合条件的 `pvp`、`team_pvp` 获胜方数据；多人 AI 局也可能合格，单人 AI 局还要求 `SoloAttributedWin=true`。训练器进一步校验双手剑、是否骑乘、命中、角色生命周期、出刀方向及数据一致性等；PvP 还要求双方在至少一个共同时间窗口内有有效交战。`Valid=true`、玩家获胜或 `SoloAttributedWin=true` 都不足以单独证明某局已参与训练。PvP 活动、受伤、伤害与移动等可能影响样本权重，但不等于学习了移动动作。

当前策略只作用于符合条件、持双手剑的**困难普通 AI 步兵**：对原版 AI 已发起且允许介入的攻击，有 25% 的机会用学习得到的分布选择上下左右方向；其余情形继续按原版输入。学习概率有均匀先验约束，每个方向范围为 12.5%–62.5%。当前没有完成由数据驱动的走位、骑兵控制或射击预判，也没有约定外部神经网络模型的协议；离线模型不能仅凭本日志推断为已经接入游戏。

## 代码依据

- [数据结构、持久化、复核和重建](../AdimiDuel_Server/Duel/AdimiToolsAiLearningStore.cs)
- [单局压缩信封编解码](../AdimiDuel_Server/Duel/AdimiToolsAiLearningCodec.cs)
- [AI 对局采集](../AdimiDuel_Server/Duel/AdimiToolsAiLearningCapture.cs) 与 [AI 场地行为](../AdimiDuel_Server/Duel/AdimiToolsAiArenaBehavior.cs)
- [PvP 对局采集](../AdimiDuel_Server/Duel/AdimiToolsPvpLearningCapture.cs) 与 [PvP 采集行为](../AdimiDuel_Server/Duel/AdimiToolsPvpLearningBehavior.cs)
- [训练器和方向统计](../AdimiDuel_Server/Duel/AdimiToolsAiLearningModel.cs)、[AI 攻击方向应用](../AdimiDuel_Server/Duel/AdimiToolsAiArenaLearnedAttack.cs)、[场地枚举](../AdimiDuel_Server/Duel/AdimiToolsAiArenaPolicy.cs)
