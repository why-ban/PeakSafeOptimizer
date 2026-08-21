# PeakSafeOptimizer 0.1.0

PEAK 的 BepInEx 5 插件，用于修复已确认的空引用/越界异常，并针对**多人玩家数量**与**地面物品数量**带来的每帧重复计算做保守削减。

这不是通用画质/帧率包。它不修改渲染、不修改网络发包频率、不创建任何全局调度对象，也不接管游戏的物品状态机。

本仓库不会自动复制 DLL、部署到游戏目录、创建发行包或执行 Git 操作。

## 受审计的游戏构建

| 项目 | 值 |
|---|---|
| Assembly-CSharp MVID | `3f24c13b-87b6-4c9f-9c21-3c49b6ad17e1` |
| Assembly-CSharp SHA-256 | `b5c1b8b73f82cca847942b0be758dd554fe347d1a7c89c69e292fafb115bfffd` |
| 插件版本 | `0.1.0` |

SHA-256 是审计记录值，不参与运行时判断。MVID 由 `BuildFingerprint.Capture()` 在启动时读取游戏 Assembly-CSharp 的 `ModuleVersionId` 得到。

## 兼容性模型（MVID 不再是全局硬门控）

当前版本的关键变化：**MVID 不是全局硬门控，而是逐补丁的兼容条件**。结构级守卫可以在未知构建上继续自验；完整替换、网络语义改写和 Transpiler 在尚未提供方法级行为指纹前只在审计 MVID 上安装。

- 启动时读取游戏 Assembly-CSharp 的 MVID 并与其 SHA-256 一起输出一行指纹日志。
- MVID 与审计值 `3f24c13b-87b6-4c9f-9c21-3c49b6ad17e1` 一致 → 兼容模式为 **`certified`**。
- MVID 不一致（游戏更新或不同构建）→ 兼容模式为 **`structural-validation`**。
- 两种模式下每个补丁仍独立报告；`Structural` 补丁执行自身验证，`Fingerprint`/`StrictIl` 补丁在未知 MVID 上以 `SkippedUncertifiedBuild` 安全跳过。局部游戏更新只会使相关补丁各自 fail-closed，不再让整个模组失效。

每个补丁声明一个 `CompatibilityLevel`（严格度递增）：

| 等级 | 含义 | 本项目的典型形态 |
|---|---|---|
| `Structural` | 结构自检通过即可运行，仅依赖 `Initialize` 校验的成员形状 | 简单 FieldRef / null guard（仅 `RunBasedValues`） |
| `Fingerprint` | 当前为结构自检 + 审计 MVID；补齐方法级行为指纹后才可放开未知构建 | 大多数完整替换/守卫 Prefix |
| `StrictIl` | 当前为审计 MVID + 唯一 IL 匹配、标签/异常块安全与生成后校验 | Transpiler 与网络面（Photon 所有权/RPC 语义）改写 |

补丁失败时单独跳过并分类（不连坐）：

- `SkippedStructureMismatch`：`Initialize` 结构校验（成员缺失/变形）拒绝当前构建。
- `SkippedIlMismatch`：IL 匹配或 IL 生成拒绝当前构建。
- `SkippedUncertifiedBuild`：补丁尚未具备方法级行为指纹，当前 MVID 未认证。
- `FailedInitialize` / `FailedPatch`：真正的初始化或安装故障。
- 其余见 `SkippedDisabled`（配置关闭）、`SkippedNoDefinition`（保留键未注册）等。

每个补丁支持 `reset`（热卸载成功后清理静态/缓存状态），卸载顺序为「全部 Unpatch 成功 → Reset → 删记录」，避免留下悬空访问器或半卸载状态。

## 实测状态摘要

以下来自真实多人局日志（`PEAK/BepInEx/LogOutput.log`），非推测：

- 本插件补丁全程 `applied`、`failed=0`，无 `PeakSafeOptimizer` 异常堆栈。
- 日志中的大量异常均来自**游戏原版代码或其它模组**，本插件逐项定位后新增守卫：
  - `BugleSFX.Update` 每帧 `IndexOutOfRangeException`（654 次）——已由 `Fixes.BugleClipGuard` 消除到 0，并记录到 3 次 `currentClip-out-of-range` 修复。
  - `RopeClimbingAPI.GetSegmentFromPercent → CharacterRopeHandling.Climbing` 每帧 `ArgumentOutOfRangeException`（184 次）——已由 `Fixes.RopeSegmentGuard` 消除。
  - `GUIManager.get_canEmote → UpdateEmoteWheel → LateUpdate` 每帧 `NullReferenceException`（2315 次）——已由 `Fixes.EmoteWheelGuard` 对 localCharacter/data/refs/stats 不完整窗口进行防御。当前反编译原版 LateUpdate 已有外层 localCharacter 守卫，因此仍需实机日志确认历史异常究竟来自成员级空窗还是第三方直接调用。
  - `CharacterItems.GetItemPosRight → CharacterAnimations.ConfigureIK → CharacterRagdoll.FixedUpdate` 每帧 `NullReferenceException`（135 次）——已由 `Fixes.IKItemGuard` 消除。
- 13 次 `RPC method 'RPC_SetSongByName(String)' not found` 属于**其他玩家安装的自定义军号曲目模组**，本机不存在该 RPC 方法，本插件无法修复。
- 排查本插件问题请按 `PeakSafeOptimizer` 关键字过滤，避免与其它模组（如 `PeakStatsEx`、`PEAK_Visuals`）产生的异常混淆。

仍未完成：多人压力基准、长时稳定性、性能基准。本文不提供任何帧率数字。

## 已注册补丁（35 项，全部默认开启）

当前是私人集中测试配置：35 个有实现的补丁与 `Diagnostics.DetailedLogging` 全部默认开启。15 个无实现或已撤下的保留键仍默认关闭，不会重新启用已确认危险的旧实现。下表「默认」列均为补丁开关默认值。

### 稳定性修复（`Patches\Stability`，6 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.RunBasedValues` | `true` | Structural | Prefix：`SerializableRunBasedValues.SerializeRunBasedValues(BinarySerializer)` | 为 null 的两个运行数据字典分配空字典，避免序列化空引用。纯守卫、不吞原方法异常。 |
| `Fixes.Parachute` | `true` | Fingerprint | Prefix（完整替换）：`CharacterData.UpdateHasParachute()` | 对不完整 character/player/slot/backpack 状态保守清空降落伞缓存，保留 Scoutmaster 与「手持→槽位→背包」优先级。 |
| `Fixes.BugleClipGuard` | `true` | Fingerprint | Prefix（只改状态）：`BugleSFX.Update()` | 修越界的 `currentClip`（负数归零、超界按 `% Length` 映射、数组空则标记跳过）。根因是原版 `RPC_StartToot` 不校验索引 + 跨客户端曲目模组不一致。副作用是本机曲目可能与发送方不同。 |
| `Fixes.RopeSegmentGuard` | `true` | Fingerprint | Prefix：`CharacterRopeHandling.Climbing()` | 当所持绳索的 `GetRopeSegments()` 为 null/空，或 `climbingAPI` 为 null 时跳过该帧攀爬。守卫每帧入口而非底层 API，避免把异常换成 `NullReferenceException`。 |
| `Fixes.IKItemGuard` | `true` | Fingerprint | Prefix：`CharacterAnimations.ConfigureIK()` | 当 `currentItem` 非空但 prefab 缺 `Hand_R`/`Hand_L` 子节点时跳过该帧手部 IK 定位。只在手部节点缺失时跳过，ReachIK 分支不受影响。 |
| `Fixes.EmoteWheelGuard` | `true` | Fingerprint | Prefix：`GUIManager.UpdateEmoteWheel()` | 当 `Character.localCharacter`、data、refs 或 stats 在观战/重生/退出/第三方调用窗口中不完整时跳过表情轮更新。只读不写。 |

### 等价改写（`Patches\Equivalent`，2 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.Campfire` | `true` | Fingerprint | Prefix：`Campfire.EveryoneInRange(float)` | 单次扫描同时维护 `_charactersInRadius` 副作用；补丁自身状态不可用时回退原方法。 |
| `Fixes.BreakableBridge` | `true` | StrictIl | Transpiler：`BreakableBridge.FixedUpdate()` | 复用每座桥已有列表做字典键快照，消除每物理帧 `Dictionary.Keys.ToList()` 分配。**当前唯一保留的 Transpiler**。 |

### 规模化（`Patches\Scale`，7 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.HeatEmissionScan` | `true` | StrictIl | Prefix（完整替换）：`CharacterHeatEmission.Update()` | 距离判定平方化，等价替换原方法。网络面（读 `photonView.IsMine`），故升级为 StrictIl。 |
| `Fixes.RagdollPhysicsMats` | `true` | Fingerprint | Prefix：`CharacterRagdoll.SetPhysicsMats()` | 输入签名未变时跳过原方法。签名含摩擦类型、部件/碰撞体计数、材质引用与**每个 Bodypart 全部 collider 的材质 ID**，另有每 300 帧/5 秒强制放行自愈。 |
| `Fixes.ItemCollisionMode` | `true` | Fingerprint | Prefix：`Item.UpdateCollisionDetectionMode()` | 目标值仍由 `itemState` 决定，仅当 Rigidbody 已是目标值时跳过赋值。 |
| `Fixes.GenericOptimizerRange` | `true` | Fingerprint | Prefix：`GenericOptimizer.AnyCharacterWithinRange(float)` | 平方距离等价。审计版 `GenericOptimizer` 默认无穷范围时仅增加薄封装；场景配置有限范围时才有收益。 |
| `Fixes.CampfireProtectionMerge` | `true` | Fingerprint | Prefix：`Campfire.ApplyCampfireProtection()` | 把同帧两次全量扫描合并为一次，单次扫描同时得出 `everyoneInRange` 与 `_charactersInRadius`，再复用于 OrbFogHandler 写入与 buff 分支。 |
| `Experimental.ZombieScanRange` | `true` | Fingerprint | Prefix：`MushroomZombie.ReadyToDisable()` | 主机侧每帧调用。平方距离 + 两标记都真后提前结束循环；阈值非正回退原方法。 |
| `Experimental.ZombieSpawnScanRange` | `true` | Fingerprint | Prefix：`MushroomZombieSpawner.ReadyToSpawn()` | 主机侧每帧调用。单阈值平方化，命中即返回；阈值负时回退。 |

### 规模化实验（`Patches\ScaleExperimental`，6 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Experimental.WeightRefreshCoalescing` | `true` | StrictIl | Prefix：`CharacterItems.RefreshAllCharacterCarryWeightRPC()` | 同帧**内容签名**合并：仅当折叠签名未变时跳过同帧重复全员负重刷新。目标即 RPC 入口，网络面升级 StrictIl。 |
| `Experimental.SleepingZombieScan` | `true` | Fingerprint | Prefix：`MushroomZombie.DoSleeping()` | 距离平方化并把 `Vector3.Angle` 延后到距离判定通过后。私有方法用 `AccessTools.MethodDelegate` 绑定。 |
| `Experimental.ItemScaleRedundantWrite` | `true` | StrictIl | Prefix：`ItemScaleSyncer.Update()` | 缩放输入、目标 transform 身份、当前 localScale 全部未变时跳过原方法。owner 缩放变化（可能发 RPC）必定放行，网络面升级 StrictIl。 |
| `Experimental.RemoteItemInterpolationThreshold` | `true` | StrictIl | Prefix：`ItemPhysicsSyncer.FixedUpdate()` | 远端 Ground 物品已精确到达插值目标时跳过两次无效 Rigidbody 移动；不触碰发包/kinematic。 |
| `Experimental.EyeLookComponentCache` | `true` | Fingerprint | Prefix（完整替换）：`PlayerEyeLook.Update()` | 逐行复刻原方法，仅把循环内每帧 `GetComponent<AnimatedMouth>` 改为按 Character 缓存。 |
| `Experimental.ExplosionScale` | `true` | Fingerprint | Prefix：`ExplosionEffect.GetPoints()` | 爆炸球数量自适应削减。原版 13 球；默认近距单发 7 球，邻近集群达到 2 / 5 发时继续降为 5 / 2 球，50–120 米降为 2 球，120 米外仅保留中心球。只减不增、不为负。 |

### 其余等价规模化（`Patches\Scale`，8 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用 |
|---|---:|---|---|---|
| `Fixes.ItemDatabaseNameLookup` | `true` | Fingerprint | Prefix：`ItemDatabase.TryGetItem(string, out Item)` | `TryGetItem` 线性遍历改字典查表，消除全员重量刷新链路里随玩家数放大的字符串分配。 |
| `Fixes.IsLookedAtScan` | `true` | Fingerprint | Prefix（完整替换）：`IsLookedAt.Update()` | 把 `Vector3.Angle` 延迟到距离判定通过后，消除每远端角色每帧的三角函数。 |
| `Fixes.ItemAudioManagerHash` | `true` | Fingerprint | Prefix（完整替换）：`ItemAudioManager.Update()` | 字符串 Animator 参数名改 int hash + 值未变跳过写入。 |
| `Fixes.BarAfflictionLayout` | `true` | Fingerprint | Prefix：`BarAffliction.UpdateAffliction(StaminaBar)` | 收敛后跳过 `RectTransform.sizeDelta` 写入，避免 dirty Canvas 布局。 |
| `Fixes.AnimatedMouthMaterial` | `true` | Fingerprint | Prefix（完整替换）：`AnimatedMouth.ProcessMicData(float[])` | 仅在 `_UseTalkSprites`/`amplitudeIndex` 变化时写材质。 |
| `Fixes.CollisionCharacterLookup` | `true` | Fingerprint | Prefix（命中时完整替换）：`CharacterMovement.StandOnPlayer(Collision)` | 对已登记的 Bodypart collider 使用游戏自带字典 O(1) 查找 Character；字典未命中时回退原方法，保留其它子碰撞体行为。 |
| `Fixes.AnimatorValuesHash` | `true` | Fingerprint | Prefix（完整替换）：`AnimatorValues.Update()` | Animator 字符串参数改为一次性 hash，并跳过值未变化的写入；接地后的 `sprintAnimationDisabled` 清理保持原顺序。 |
| `Fixes.PlayerNameUiWrites` | `true` | Fingerprint | Prefix（完整替换）：`UIPlayerNames.UpdateName(int, Vector3, bool, int)` | 名字牌位置仍每帧更新，仅在 sprite、active 与 alpha 目标实际变化时写 UI 状态。 |

### 远端与物理实验（`Patches\ScaleExperimental`，6 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Experimental.RemoteRagdollLodPhysics` | `true` | Fingerprint | Prefix：`CharacterRagdoll.FixedUpdate()` | 仅对 60 米外、已预热且无交互的远端玩家跳过动画图、IK 与姿态采样；50 米内恢复。近距聚集场景不触发。 |
| `Experimental.RemoteRagdollLodAnimate` | `true` | Fingerprint | Prefix：`Bodypart.Animate(float, float)` | 与同一远距判定共享状态，跳过关节动画驱动；Gravity、Drag、ToggleUseGravity、ApplyForces 保持运行。 |
| `Experimental.RemoteRagdollLodMovementForce` | `true` | Fingerprint | Prefix：`Bodypart.AddMovementForce(Vector3)` | 远距被动 ragdoll 不再加入本地动画移动力；网络位置与碰撞路径不变。 |
| `Experimental.RemoteRagdollLodAnimator` | `true` | Fingerprint | Prefix：`CharacterAnimations.Update()` | 远距被动 ragdoll 跳过大部分 Animator 参数写入，但保留投掷/表情计时、表情退出、水状态复位和辅助位置更新；本地、bot、观战、抓取、攀爬、死亡、装备窗口等均回退原版。 |
| `Experimental.JointProjection` | `true` | Fingerprint | Postfix：`RigCreator.SpawnJoint(...)` | 将运行时新建 joint 的 projection 从 PositionAndRotation 改为 PositionOnly。审计构建中该方法没有正常运行时调用者，预期通常不触发且可能无收益。 |
| `Experimental.DetailBodypartThrottle` | `true` | Fingerprint | Prefix：`Bodypart.Animate(float, float)` | 非本地、非交互玩家的 59 个手指/jiggle/脚趾/嘴颌部件默认每 3 个物理 tick 驱动一次，并按部件错峰。近距聚集时仍生效，是当前直接削减该场景托管物理工作的主要补丁。 |

爆炸相关说明：`Dynamite.RPC_Explode` 每次实例化 `explosionPrefab`（含 `ExplosionEffect`），原版 `GetPoints` 生成 `1 + 4 + 2*4 = 13` 个爆炸球，每个球再实例化 orb 并做 Animator/MeshRenderer/MaterialPropertyBlock 处理，多人同时投掷时放大。`ExplosionScale` 使用 16 项预分配环形缓冲记录邻近爆炸，整个判定零分配；它只削减球数。球内 `new MaterialPropertyBlock()` 的复用**未实现**——该分配位于 `Start()` 内嵌套的 `IExplode` 局部函数（编译器生成方法）里，属于多模组环境下已证明不可靠的目标形态，故不做。全局 `Shader.WarmupAllShaders()` 实现已因主线程冻结事故彻底删除。

## 本轮修复的问题（变更说明）

- `WeightRefreshCoalescing` 改为**内容签名合并**：不再因「玩家数不变」就丢弃同帧必要的重量刷新（Photon 单帧可派发多条 RPC，签名折叠 roster 槽位/背包/卡住物品等全部输入）。
- `BreakableBridge` 快照 null 时恢复原版语义（新建列表写回）而非抛异常。
- `RagdollPhysicsMats` 改 pending 签名确认：原方法真正返回后才确认签名，避免原方法抛异常时状态错记。
- 扫描循环内 null candidate 改为静默 `continue`，移除高频诊断开销。
- `CampfireProtectionMerge` 改为每次读 `s_CampfireBuff` 当前值（不再缓存值）。
- `RagdollPhysicsMats` 签名纳入每个 Bodypart 全部 collider 材质 ID（不再是只取首个）。
- `ExplosionScale` 对负 prefab count 做保护。
- `EyeLook` 依赖验证前移、`eyeRenderers == null` 保持原 NRE 行为。
- 所有 35 个补丁补齐 `Reset` 并由安装器统一管理（卸载后清理静态/缓存状态）。

## 保留配置键但未注册的项

以下 15 个键没有 `PatchDefinition`。默认均为 `false`，启动时显示 `disabled by configuration`；若改为 `true`，则显示 `no definition registered`，不安装任何补丁：

| 配置键 | 默认 | 未注册原因 |
|---|---:|---|
| `Fixes.HeatEmission` | `false` | 目标由 `HeatEmissionScan` 覆盖。 |
| `Fixes.MapHandler` | `false` | 旧索引/硬编码局部可能破坏结算与切机场。 |
| `Fixes.RopeRendering` | `false` | 依赖 14 条含编译器 selector 缓存字段的连续 IL，运行时匹配 0，无法证明唯一。 |
| `Experimental.ShakyRock` | `false` | NonAlloc 替换与 foreach/异常区/`ComputePenetration` 语义耦合。 |
| `Experimental.PlayerEyeLook` | `false` | stale target / 已销毁对象风险，目标由 `EyeLookComponentCache` 覆盖。 |
| `Experimental.WeightRefresh` | `false` | 缺少可靠帧末 RPC 合并调度点，目标由 `WeightRefreshCoalescing` 覆盖。 |
| `Experimental.RavenPerch` | `false` | 玩家扫描与 FlyAway/生成/视角决策交织。 |
| `Experimental.EruptionSpawner` | `false` | Transpiler 生成无效 IL（HarmonyException: IL Compile Error）。 |
| `Experimental.BeeSwarm` | `false` | acquisition 扫描与 owner/TryDeAggro/蜂巢即时响应耦合。 |
| `Experimental.ZombieManager` | `false` | 清理优先级与 `maxActiveZombies` 顺序不应被降频改变。 |
| `Experimental.SleepingZombie` | `false` | 唤醒扫描与可见性/视线/状态转换耦合，目标由 `SleepingZombieScan` 覆盖。 |
| `Experimental.Tornado` | `false` | 多客户端 raycast 缓存可能分叉。 |
| `Experimental.GloomVoiceConfuser` | `false` | 复杂 Update 同时处理主机转换/分配清理/偷取表现/elapsed timer。 |
| `Experimental.MovingLava` | `false` | 未证明 scene view/owner 为空/主机迁移下的唯一权威规则。 |
| `Experimental.ExplosionShaderWarmup` | `false` | `Shader.WarmupAllShaders()` 会全局阻塞预热全部 shader 变体，已实测导致生成炸药时主线程冻结，实现已删除。 |

没有 `EnableAllExperimental` 或任何批量开关；每项必须单独决定。

## 配置与诊断

首次运行生成 `BepInEx/config/com.peak.safeoptimizer.cfg`，分 `Fixes`、`Experimental`、`Diagnostics` 三节。

| 配置键 | 默认 | 含义 |
|---|---:|---|
| `Diagnostics.DetailedLogging` | `true` | 每个修复原因首次命中输出一次，之后按原因最多每 30 秒聚合一次。任何情况都不逐帧输出。 |
| `Experimental.ExplosionPointCount` | `3` | `ExplosionScale` 的基础 `explosionPointCount` 上限（原版 4）；集群与距离判定可继续降低。 |
| `Experimental.ExplosionSubPointCount` | `1` | `ExplosionScale` 的基础 `subExplosionPointCount` 上限（原版 2）；集群与距离判定可继续降低。 |
| `Experimental.ExplosionClusterWindowSeconds` | `1.2` | 爆炸集群检测时间窗。 |
| `Experimental.ExplosionClusterRadius` | `3.5` | 爆炸集群检测半径。 |
| `Experimental.ExplosionNearDistance` | `50` | 从该距离开始进入远距 2 球上限。 |
| `Experimental.ExplosionFarDistance` | `120` | 从该距离开始仅保留中心球。 |
| `Experimental.RemoteRagdollLodEnterDistance` | `60` | 远端角色进入被动 ragdoll 层级的距离。 |
| `Experimental.RemoteRagdollLodExitDistance` | `50` | 远端角色恢复完整行为的距离。 |
| `Experimental.DetailBodypartThrottleInterval` | `3` | 远端细节部件每 N 个物理 tick 执行一次 Animate；1 为不降频，运行时最大钳制到 10。 |

诊断实现要点：详细诊断只对修复/回退原因计数，正常优化路径不记账；同一原因首次后最多每 30 秒输出一条聚合日志，不会逐帧刷屏。

配置文件由 BepInEx 持久化，插件不会在更新、启动或退出时删除 `com.peak.safeoptimizer.cfg`。默认值只影响首次生成或缺少对应键的配置；已有 cfg 中的显式值会继续保留。

### 热重载

- 布尔补丁开关（`Fixes.*` 与 `Experimental.*` 中**已注册**的项）支持运行时热重载：在 BepInEx ConfigurationManager 里改值后立即 install/uninstall，无需重启。
- 诊断开关和数值型配置（爆炸、远距 LOD、细节部件间隔）由运行时代码读取 `.Value`，修改后即时生效，无需 Harmony install/uninstall。
- 热切换在日志输出 `HotReload:` 一行，例如 `HotReload: <名称> enabled→installed` / `disabled→uninstalled`，失败或跳过则带 `Skipped*`/`Failed*` 与原因。每项切换独立隔离，失败只影响该项。
- 未注册键改为 `true` 时输出 `HotReload: <名称> enabled→SkippedNoDefinition: no definition registered`，不安装任何补丁。
- **风险提示**：唯一 Transpiler（`Fixes.BreakableBridge`）热切换风险最高——卸载/重装需要 Harmony 反向应用并重新编译 IL。若切换后出现异常或 `verified uninstall failed`，应重启游戏确认，不要在运行中反复切换。

## 构建与安装

需要 .NET SDK、PEAK 与 BepInEx 5。项目以只读方式引用游戏依赖，所有引用 `Private=false`，构建不复制 BepInEx/Harmony/Unity/Photon/Zorro/Assembly-CSharp。

```powershell
dotnet build -c Release
```

输出目录 `bin/Release/netstandard2.1/`，预期仅含本项目 DLL、PDB、deps。安装：手动复制 `PeakSafeOptimizer.dll` 到 `PEAK/BepInEx/plugins/`。

## 当前限制

- 完成的是构建、结构验证与严格 IL 自检；**多人压力基准、长时稳定性与性能基准仍未完成**，不给帧率数值承诺。
- `DetailBodypartThrottle`、`CollisionCharacterLookup`、`AnimatorValuesHash`、`PlayerNameUiWrites` 尚未完成游戏内验证；若目标构建结构不符会各自 fail-closed 单独跳过，不影响其余补丁。
- 长序列 IL 模式在多模组预处理环境不可靠；后续新增补丁优先窄 Prefix + 精确结构验证。
- `BugleClipGuard` 只保护 `BugleSFX.Update` 的 clip 索引路径；`RPC_SetSongByName` 不在修复范围。
- 热重载对 Prefix 补丁较安全，但唯一 Transpiler（`BreakableBridge`）存在 IL 反向应用的残余风险，切换后如有异常请重启确认。
- 遇到崩溃、存档/网络/场景切换/物品/物理/渲染异常，立即关闭最近启用的单项并保留完整日志。测试步骤见 [TESTING.md](TESTING.md)。
