# 本地单机训练：采集与模型交换接口

本协议对应单机模块中的 `TrainingMissionLogic.cs`、`TrainingPolicySession.cs`、`TrainingEpisodeRecorder.cs`、`TrainingPolicyBridge.cs` 和 `TrainingPolicyController.cs`。它是局限本机的对接约定；独立离线训练器和实验性推理程序位于仓库外的 `E:\AI训练网页\AiOfflineTrainer`，模型需另行训练生成。离线测试不等于游戏、地图、动画或模型已实机验证。客户端使用 net472 / C# 7.3 和游戏自带的 Newtonsoft.Json。

## 对局文件

采集器构造为 `new TrainingEpisodeRecorder(moduleDirectory)`。后台解析环境变量 `ADIMI_LOCAL_TRAINING_PATH`，未指定时读取模块根目录 `TrainingSettings.xml` 中的 `TrainingSettings/DataDirectory/@value`。相对值基于模块目录。配置文件不存在时回退 `%LOCALAPPDATA%\AdimiDuelTraining`；配置存在但格式无效、不可读或路径失败时报告 `Errors`/`LastError`，不会悄悄换目录。`ResolvedRoot` 在后台解析完成后可读；失败时为 null。机器当前配置由单机模块交付文件决定，不需把它硬编码到采集代码。

- 原版 AI 对 AI：`BeginRound(...)`，写 `<root>/selfplay/<session>/episodes.jsonl`，`MatchType` 为 `selfplay`。
- 人类玩家对 AI：`BeginLocalPlayerRound(...)`，要求恰好一方 `ControlSource=human`，写 `<root>/local_player_ai/<session>/episodes.jsonl`，`MatchType` 为 `local_player_ai`。双方原版 AI 应标 `native`；有实际组件输出的原版 AI 可转为 `mixed`。不能把玩家局伪装成自对战。

每行是一局完整 JSON，或者单行 `adimi-gzip-json-v1` gzip/Base64 信封（含原始字节长度和 SHA-256）；不要把它当作逐 transition 的多行 JSON。实际包含时序帧、命中/移除事件、终局和完整性信息，不提供逐步 reward。帧是观察，`ExecutedAction` 始终为 null。回合队列有界，异步压缩落盘；断电、队列满、写入失败都可能丢失数据，必须观察 `Pending`、`Dropped`、`Errors` 和 `LastError`。

## 可选本机邮箱

单机任务默认不创建邮箱；只有游戏进程启动时环境变量 `ADIMI_LOCAL_POLICY_BRIDGE` 的值**恰好为 `1`**才启用。它不会修改系统级环境变量。任务代码使用 `new TrainingPolicyBridge(null, true)`，后台创建 `%LOCALAPPDATA%\AdimiDuelTraining\PolicyBridge/<session_id>/`，不使用网络；具体会话路径记入模块日志。底层桥接器也允许指定本机 `localRoot`，但不支持 UNC 目录。邮箱不可用、无训练器、回复无效或过期时保留原版 AI，不能让对战停止。邮箱是覆盖式最新状态交换，不是完整轨迹或可靠消息队列；完整训练数据以采集器为准。

任务创建 AI 时才附加组件，玩家不附加；附加后检查原生 AI 输入回调开关，未启用时将其设为 true 并复核。若设置失败，组件保持原版输入且不接收策略；回退和清理不会将回调开关设为 false，以免影响其他组件。每次快照保留 A/B 阵营和人类标识。每 tick 读取角色动作阶段并交给本机 session，同时接收候选、处理失联回退和回写组件输出；快照通常每 50 ms 一次，长局会降频。当前没有面向用户的移动开关，任务调用固定关闭实验移动。

`Publish(episodeId, sequence, gameSeconds, actors)` 接收有界快照；`episodeId` 必须取当前采集器 `ActiveEpisodeId`（32 位小写 GUID），序号在回合内递增，时间不得倒退。角色最多 32 个，ID 唯一，队伍为 0/1，位置等为有限数，生命在 0 与上限之间。每个角色的 `is_mounted` 是必需的 JSON 布尔值，来自该角色当次游戏快照的 `Agent.HasMount`，独立于阵营和玩家身份；发送端拒绝缺失状态，不能默认推断为未骑乘。字段来自采样时的引擎观测，不代表已经实机验证。旧版 DLL 的观察可能不含此字段，推理端遇到缺失、null 或非布尔值时应拒绝该观察或安全回退，而不是按 `false` 处理。所有字段都是游戏原始观测；`input_coordinate_system="unknown"`，不得将输入向量解释为已验证的世界坐标。

后台原子更新 `observation.json`，示意：

```json
{"protocol":"adimi-local-policy-v1","kind":"observation","session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","episode_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sequence":1,"game_seconds":3.0,"input_coordinate_system":"unknown","actors":[{"agent_id":1,"side":0,"is_player":false,"is_mounted":false,"x":0,"y":1,"z":0,"look_x":1,"look_y":0,"velocity_x":0,"velocity_y":0,"health":100,"health_limit":100,"action_type":0,"action_stage":0,"action_direction":0,"action_progress":0,"weapon_class":"TwoHandedSword"}],"winning_side":null,"reason":null,"terminated":false,"truncated":false}
```

外部训练器读取后，可将每个 AI 的回复先写同目录临时文件并刷新，再原子替换 `actions/<agent_id>.json`。编码为 UTF-8，无多余字段或重复键，最大 64 KiB。以下所有字段必须存在：

```json
{"protocol":"adimi-local-policy-v1","session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","episode_id":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","sequence":1,"agent_id":1,"model_id":"model_1","model_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","attack":"right","move_x":null,"move_y":null}
```

`session_id`、`episode_id`、`sequence` 和 `agent_id` 必须与已发布观察匹配；模型 ID 最多 128 个 ASCII 字母、数字、点、下划线或连字符，哈希为模型文件的 64 位小写 SHA-256。`attack` 只接受 `native/up/down/left/right`，不支持踢腿；`move_x` 和 `move_y` 必须同时为 null 或均在 [-1,1]。程序只校验哈希格式，不代表已载入或核验模型文件。玩家和已死亡角色不接收建议。

`TryGetDecision(episodeId, agentId, out decision)` 只返回候选，无法证明动作已执行。默认候选有效期为从最初发布观察时起 250 ms（构造参数允许 20–2000 ms），不能靠重复读同一文件续期；保留最近 8 个已写观察、允许序号落后最多 6。错误、旧会话、旧局、非法 JSON、超限和超时不能形成新动作；异常与无回复按原版处理。轮询会合并中间状态，训练器不能假定每个 `Publish` 都写到磁盘。

针对目前只训练未骑乘步兵的模型，收到上马事件或逐帧发现骑乘后会清除旧候选；重新满足未骑乘条件时需等待新观察。输入回调与上马事件的引擎调度仍需实机验证，不能保证所有并发时序都没有窗口。

## 终局和有界保持

`PublishTerminal(episodeId, sequence, gameSeconds, winningSide, reason, terminated, truncated)` 对当前回合发布独立 `terminals/<episode_id>.json`。正常结果 `terminated=true, truncated=false`，获胜方为 0/1 或平局 null；超时、中断为 `terminated=false, truncated=true, winningSide=null`。终局必须有递增序号、非递减时间及非空原因；它不要求角色列表，发布后候选立即失效，旧 episode 不可重新启动。终局队列最多 8；满或磁盘错误会报告失败，关闭时后台尽力排空，不保证崩溃后写出。

`TrainingPolicyController` 是可选 `AgentComponent`，只接收内存候选；不在输入回调做文件 I/O 或访问引擎对象。任务每 tick 即使没有候选，也调用 `ObserveAttackPhase(allowNewAttack, releasing)`；收到有效候选调用 `Publish(decision, allowNewAttack, releasing, experimentalMovement=false)`，无新回复调用 `WithdrawCandidate()`。只接受 `Idle/Guard/Other/ReadyMelee/ReleaseMelee` 动作类型且排除 `Defend/DefendParry` 阶段；`ReleaseMelee` 或 `AttackRelease` 阶段不得开始新接管。组件只在原版已发起普通攻击时尝试选择方向，不修改移动，不新增踢腿，也不修改防御或其他事件控制位。底层测试接口有实验移动参数，单机任务始终传 false，坐标语义尚未验证。

原建议必须在原截止内才能首次接管。已经确实改写过的单次攻击，即使建议过期或暂时失联，也可以保持原方向，直到无攻击、release-to-ready、防御、踢腿、显式 `Clear()`，或自首次接管起的严格 1.5 秒上限。它不是新的建议或 TTL 续期；超上限恢复原版且不会再次接管同一次攻击。完整队列抑制修改，以防无来源的干预。动作阶段是在 mission tick 采样后传入组件的，并非在每次输入回调中实时查询；游戏线程调度、动作阶段切换与输入回调之间的先后，以及 1.5 秒上限对动作的影响尚未实机验证。若无法可靠辨别阶段，应停用接管。

## 回合结束及回写顺序

1. 在 `OnAgentRemoved` 回调尽早获取 `Stopwatch.GetTimestamp()`，调用 `RecordRemoval(now, attacker, victim, killed, state, timestamp)`。旧五参数重载会在自身入口取时间，精度略受调用延迟影响。死亡回调仅是可观察边界，不声称它精确等于引擎内部死亡时刻。
2. 为死亡角色 `Clear()` 组件，阻止后续干预；在主线程 `DrainOutputs()`，对每条调用 `RecordComponentOutput(now, trace)`。时间应使用当前游戏时间，不能倒退。采集器仅接受同局同角色、回调时间**严格早于**其死亡回调的迟到输出；相同或更晚、错误回合、错误身份或缺失来源将使回合无效。
3. 结束时先对所有组件 `Clear()`，再排空并回写剩余 trace。若丢失或回写失败，在 `EndRound` 前调用 `MarkPolicyTraceIncomplete()`。随后用同一 `ActiveEpisodeId` 发布邮箱终局并按实际结果 `EndRound(...)`，不可把超时或中断标成胜利。

组件输出保留模型声明 ID/哈希、观察序号、原始和组件修改后的控制值、回调单调时间、时钟频率、原建议截止、首次接管时刻及 `ContinuedAttack`。它表示组件当时输出，不代表后续组件或游戏最终执行。事件列表的 `Seconds` 是回写时的游戏时间；迟到回写可能排在死亡事件之后，需使用同进程的回调单调时间判断这两种事件的先后。不要跨进程或跨重启直接比较 Stopwatch 数值。

## 验证范围

各测试项目位于 `E:\汉化mod\Codex工具\Tests\SinglePlayerTrainingRecorder`、`SinglePlayerPolicyBridge`、`SinglePlayerPolicyController`、`SinglePlayerPolicyIntegration` 和 `LocalTrainingMission`。它们使用合成输入、文件与引擎桩；其中完整任务桩直接验证玩家局和观战局送出的双方阵营及身份。模块另外按实际本机客户端程序集编译；这些都不是游戏实机、FPS、真实训练器联动或训练完成证明。
