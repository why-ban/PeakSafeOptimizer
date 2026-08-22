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
- 其余见 `SkippedDisabled`（配置关闭）等。

每个补丁支持 `reset`（热卸载成功后清理静态/缓存状态），卸载顺序为「全部 Unpatch 成功 → Reset → 删记录」，避免留下悬空访问器或半卸载状态。

## 实测状态摘要

以下来自真实多人局日志（`PEAK/BepInEx/LogOutput.log`），非推测：

- 本插件补丁全程 `applied`、`failed=0`，无 `PeakSafeOptimizer` 异常堆栈。
- 日志中的大量异常均来自**游戏原版代码或其它模组**，本插件逐项定位后新增守卫：
  - `RopeClimbingAPI.GetSegmentFromPercent → CharacterRopeHandling.Climbing` 每帧 `ArgumentOutOfRangeException`（184 次）——已由 `Fixes.RopeSegmentGuard` 消除。
  - `GUIManager.get_canEmote → UpdateEmoteWheel → LateUpdate` 每帧 `NullReferenceException`（2315 次）——已由 `Fixes.EmoteWheelGuard` 对 localCharacter/data/refs/stats 不完整窗口进行防御。当前反编译原版 LateUpdate 已有外层 localCharacter 守卫，因此仍需实机日志确认历史异常究竟来自成员级空窗还是第三方直接调用。
  - `CharacterItems.GetItemPosRight → CharacterAnimations.ConfigureIK → CharacterRagdoll.FixedUpdate` 每帧 `NullReferenceException`（135 次）——已由 `Fixes.IKItemGuard` 消除。
- 由其它模组造成的异常与卡顿不在本插件职责范围内。例如 13 次 `RPC method 'RPC_SetSongByName(String)' not found` 来自其他玩家安装的自定义军号曲目模组；`BugleSFX.Update` 的越界索引同样源于此，曾经有过守卫（`BugleClipGuard`），已按此原则移除。
- 排查本插件问题请按 `PeakSafeOptimizer` 关键字过滤，避免与其它模组（如 `PeakStatsEx`、`PEAK_Visuals`）产生的异常混淆。

仍未完成：多人压力基准、长时稳定性、性能基准。本文不提供任何帧率数字。

## 已注册补丁（41 个补丁 / 37 个开关）

配置文件里的每一个开关都对应真实注册的补丁，不存在只显示不生效的键。下表「默认」列均为开关默认值。

**默认值策略**：只有**行为等价**（结果与原版逐位相同）或**修复原版故障**的补丁默认开启。任何用观感、手感或动画精度换帧数的项一律默认关闭——那个取舍属于安装者，不属于模组。

默认关闭的项：`Experimental.RemoteRagdollLod`（一个开关 = 4 个补丁，远距姿态凝固）、`Experimental.DetailBodypartThrottle`、`Experimental.RemoteClusterAnimationThrottle`（远端动画 25Hz 步进）、`Graphics.CapBoneWeights`（关节形变变糙）、`Experimental.CollisionCallbackReuse`（进程级引擎标志）。

**必须协同的补丁合并为一个开关**，避免半开状态看起来受支持却不是：`Experimental.RemoteRagdollLod` 管 4 个补丁（它们共用同一份距离层级判定，只开其中几个会让远端角色一边跳过动画图求值、一边仍被驱动向动画目标，这不是任何一档被论证过的状态）；`Fixes.SnowballContactGuard` 管 2 个补丁（一个写快照、一个读快照，单开任一方都无意义）。因此 41 个补丁对应 37 个开关。

### 稳定性修复（`Patches\Stability`，5 项，全部默认开启）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.RunBasedValues` | `true` | Structural | Prefix：`SerializableRunBasedValues.SerializeRunBasedValues(BinarySerializer)` | 为 null 的两个运行数据字典分配空字典，避免序列化空引用。纯守卫、不吞原方法异常。 |
| `Fixes.Parachute` | `true` | Fingerprint | Prefix（完整替换）：`CharacterData.UpdateHasParachute()` | 对不完整 character/player/slot/backpack 状态保守清空降落伞缓存，保留 Scoutmaster 与「手持→槽位→背包」优先级。 |
| `Fixes.RopeSegmentGuard` | `true` | Fingerprint | Prefix：`CharacterRopeHandling.Climbing()` | 当所持绳索的 `GetRopeSegments()` 为 null/空，或 `climbingAPI` 为 null 时跳过该帧攀爬。守卫每帧入口而非底层 API，避免把异常换成 `NullReferenceException`。 |
| `Fixes.IKItemGuard` | `true` | Fingerprint | Prefix：`CharacterAnimations.ConfigureIK()` | 当 `currentItem` 非空但 prefab 缺 `Hand_R`/`Hand_L` 子节点时跳过该帧手部 IK 定位。只在手部节点缺失时跳过，ReachIK 分支不受影响。 |
| `Fixes.EmoteWheelGuard` | `true` | Fingerprint | Prefix：`GUIManager.UpdateEmoteWheel()` | 当 `Character.localCharacter`、data、refs 或 stats 在观战/重生/退出/第三方调用窗口中不完整时跳过表情轮更新。只读不写。 |

### 等价改写（`Patches\Equivalent`，1 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.Campfire` | `true` | Fingerprint | Prefix：`Campfire.EveryoneInRange(float)` | 单次扫描同时维护 `_charactersInRadius` 副作用；补丁自身状态不可用时回退原方法。 |

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

### 聚集与方山专项（本轮新增 4 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用 |
|---|---:|---|---|---|
| `Fixes.PocketBehaviorGuard` | `true` | Fingerprint | Prefix：`CharacterItems.UpdatePocketBehaviors()` | 加入/注册窗口 `character.player` 未链接时跳过该帧，消除实测 14 次逐帧空引用；数据就绪后逐行走原方法。 |
| `Fixes.BodypartDragIdentity` | `true` | Fingerprint | Prefix：`Bodypart.Drag(float, bool)` | 有效 drag 精确等于 `1f` 时跳过两次速度乘法（IEEE 恒等）。倒地/被风滚草撞倒的角色 `currentRagdollControll == 0`，命中率最高。 |
| `Fixes.LightVolumeSampleCache` | `true` | Fingerprint | Prefix+Postfix：`LightVolume.SamplePositionAlpha(Vector3, bool)` | 按截断后的光照体素缓存 `Texture3D.GetPixel` 结果；每角色每帧的语音遮蔽采样、环境混响与风区采样共享同一缓存。贴图实例或 `updateCount` 变化即整体失效。 |
| `Experimental.TumbleWeedTargetScan` | `true` | Fingerprint | Prefix：`TumbleWeed.FixedUpdate()` | 风滚草的目标扫描默认每 5 个物理 tick 解一次并复用，`AddForce` 仍每 tick 施加且方向按缓存目标当前 Center 重算；距离比较改平方。沙漠/方山专项。 |

### 远端与物理实验（`Patches\ScaleExperimental`，5 项，**全部默认关闭**）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Experimental.RemoteRagdollLod`（本组共用一个开关） | `false` | Fingerprint | Prefix：`CharacterRagdoll.FixedUpdate()` | 仅对 60 米外、已预热且无交互的远端玩家跳过动画图、IK 与姿态采样；50 米内恢复。近距聚集场景不触发。 |
| 同上开关 | `false` | Fingerprint | Prefix：`Bodypart.Animate(float, float)` | 与同一远距判定共享状态，跳过关节动画驱动；Gravity、Drag、ToggleUseGravity、ApplyForces 保持运行。 |
| 同上开关 | `false` | Fingerprint | Prefix：`Bodypart.AddMovementForce(float)` | 远距被动 ragdoll 不再加入本地动画移动力；网络位置与碰撞路径不变。 |
| 同上开关 | `false` | Fingerprint | Prefix：`CharacterAnimations.Update()` | 远距被动 ragdoll 跳过大部分 Animator 参数写入，但保留投掷/表情计时、表情退出、水状态复位和辅助位置更新；本地、bot、观战、抓取、攀爬、死亡、装备窗口等均回退原版。 |
| `Experimental.DetailBodypartThrottle` | `false` | Fingerprint | Prefix：`Bodypart.Animate(float, float)` | 非本地、非交互玩家的 59 个手指/jiggle/脚趾/嘴颌部件默认每 3 个物理 tick 驱动一次，并按部件错峰。近距聚集时仍生效，是当前直接削减该场景托管物理工作的主要补丁。 |

爆炸相关说明：`Dynamite.RPC_Explode` 每次实例化 `explosionPrefab`（含 `ExplosionEffect`），原版 `GetPoints` 生成 `1 + 4 + 2*4 = 13` 个爆炸球，每个球再实例化 orb 并做 Animator/MeshRenderer/MaterialPropertyBlock 处理，多人同时投掷时放大。`ExplosionScale` 使用 16 项预分配环形缓冲记录邻近爆炸，整个判定零分配；它只削减球数。球内 `new MaterialPropertyBlock()` 的复用**未实现**——该分配位于 `Start()` 内嵌套的 `IExplode` 局部函数（编译器生成方法）里，属于多模组环境下已证明不可靠的目标形态，故不做。全局 `Shader.WarmupAllShaders()` 实现已因主线程冻结事故彻底删除。

### 聚集专项（5 项 + 2 条非 Harmony 车道）

这一组是针对「离队友远就不卡、聚集起来才十几帧」这条实测事实设计的：每一项的收益都必须随屏幕上的玩家数增长，纯距离门控的老车道（`RemoteRagdollLod*`）在聚集时完全不触发，因此不算在内。

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与代价 |
|---|---:|---|---|---|
| `Experimental.RemoteClusterAnimationThrottle` | `false` | Fingerprint | Prefix（执行 tick 完整替换）：`CharacterRagdoll.FixedUpdate()` | 远端、满控、无交互玩家的动画图默认每 2 个物理 tick 求值一次，并按实例错峰。求值时传入**累计 dt**，所以动画总时长不变，只是时间分辨率降到 25Hz。跳过的 tick 仍执行 `SetPhysicsMats`；`SaveAnimationData`/`SaveAdditionalTransformPositions` 故意不执行（它们读的是动画姿态，在没求值的 tick 上读到的是物理姿态，照读会让角色瘫软）。渲染骨骼由 PhysX 直接驱动，不会冻结。代价：远端动画以 25Hz 步进，近距可感知。 |
| `Fixes.BodypartMovementForceIdentity` | `true` | Fingerprint | Prefix：`Bodypart.AddMovementForce(float)` | 力恰为零向量时跳过（`forcesToAdd += Vector3.zero` 逐位恒等）。站着不动的人 `worldMovementInput_Lerp` 收敛到零、`GetMovementForce` 对不可移动状态直接返回 0，所以聚集聊天时全员每部件每 tick 命中。 |
| `Fixes.CollisionContactsNoAlloc` | `true` | Fingerprint | Prefix（完整替换）：`CharacterMovement.OnCollision(Collision, bool, Bodypart)` | 原版对 `Collision.contacts` 取值 7 次，本版通过 `GetContact(0)` 只读一次。引擎源码逐位等价（`Collision.cs:104-117` 与 `ContactPair.cs:110-137` 翻转逻辑一致）。零接触点这一分支交回原版（原版会抛 `IndexOutOfRangeException`，不替它改语义）。 |
| `Fixes.SnowballContactGuard`（本组共用一个开关） | `true` | Fingerprint | Prefix（完整替换）：`Snowball.OnCollisionStay(Collision)` | 全仓唯一跨帧持有 `Collision` 的地方（`Snowball.cs:105/227`）。改为在回调内快照 collider / gameObject / `GetContact(0).normal`，这是开启回调复用的前提。 |
| 同上开关 | `true` | Fingerprint | Prefix（完整替换）：`Snowball.ProcessLastCollision()` | 消费上面的快照（内联原版 `Scale` 与 `ClassifyCurrentContact`），分支、比较与字段写入顺序逐行复刻。无快照时（补丁半装）交回原版。 |

两条非 Harmony 车道：

这两条不走 Harmony，因为它们要改的不是某个方法的行为，而是**引擎/管线的全局状态**：一个是 `Physics` 的静态标志，一个是 URP 资产与 `QualitySettings` 的字段。没有可打的目标方法，直接写就是最省的做法。它们由 `Plugin.Update` 每帧驱动，`OnDestroy` 里在 Harmony 卸载**之前**先还原（引擎全局状态与补丁卸载是否成功无关）。回滚都是「只还原自己写过、且当前值仍是自己写的那个值」，玩家或游戏中途改过的值不会被覆盖。

| 配置键 | 默认 | 作用与回滚 |
|---|---:|---|
| `Experimental.CollisionCallbackReuse` | `false` | 置 `Physics.reuseCollisionCallbacks = true`。默认 false 时引擎对**每次上报的碰撞**都先分配一个 `Collision` 对象加一个 `ContactPoint[]`（`Collision.cs:87-94` 构造函数里就填好，回调看不看 contacts 都付钱），而每个接触对的两侧各派发一次（`Physics.cs:1440-1454`）——聚集的布娃娃正是这个量最大的场景。仅当上面两个 Snowball 守卫都安装成功才会开启，任一未装立即写回原值；退出时也写回。 |
| `Graphics.CapBoneWeights` | `false` | 把 `QualitySettings.skinWeights` 限制到每顶点 2 根骨骼——游戏画质菜单没有这一项，而每顶点骨骼数是每个蒙皮角色的逐顶点成本，随屏幕上的人数放大。只降不升（已经 ≤2 就不写）。启动 5 秒后才首次生效（等游戏自己的画质设置先写完，否则快照会存到玩家没选过的值），之后每 2 秒重申一次。只有本车道自己写过、且当前值仍是它写的那个值时才会在关闭/退出时写回启动值。默认关闭：肩/髋关节形变会略糙。 |

这条车道的取舍原则：**只覆盖游戏画质菜单没有提供的项，且不默认改变画面**。每顶点骨骼数在 `Assembly-CSharp` 里没有对应设置类，玩家无从调整，所以由补丁提供开关——但默认关闭，因为降低骨骼权重会让关节处的形变变糙。曾经这里还有一项强制关闭 MSAA（游戏同样没有抗锯齿设置），已移除：关抗锯齿是画面决定，应该由玩家做。阴影距离与级联数曾经也在这里，已移除——游戏自带「阴影距离」设置写的就是这两个值（`ShadowDistanceSettings.cs:17-41`，200/150/75/0 配 2/2/1/1），压它们等于偷偷覆盖玩家已经做过的选择，而且强制单级联会让远处阴影随移动漂移（级联包围球每帧跟着相机重新拟合、没有 texel 对齐）。玩家嫌卡自己调档位即可。

## 本轮修复的问题（变更说明）

- `WeightRefreshCoalescing` 改为**内容签名合并**：不再因「玩家数不变」就丢弃同帧必要的重量刷新（Photon 单帧可派发多条 RPC，签名折叠 roster 槽位/背包/卡住物品等全部输入）。
- `RagdollPhysicsMats` 改 pending 签名确认：原方法真正返回后才确认签名，避免原方法抛异常时状态错记。
- 扫描循环内 null candidate 改为静默 `continue`，移除高频诊断开销。
- `CampfireProtectionMerge` 改为每次读 `s_CampfireBuff` 当前值（不再缓存值）。
- `RagdollPhysicsMats` 签名纳入每个 Bodypart 全部 collider 材质 ID（不再是只取首个）。
- `ExplosionScale` 对负 prefab count 做保护。
- `EyeLook` 依赖验证前移、`eyeRenderers == null` 保持原 NRE 行为。
- 所有补丁补齐 `Reset` 并由安装器统一管理（卸载后清理静态/缓存状态）。
- `RagdollPhysicsMats` 的逐碰撞体材质审计改为只在既有的 300 帧 / 5 秒强制刷新 tick 上计算，其余 tick 只比对廉价托管签名，降低补丁自身的 native 读密度。
- `EyeLookComponentCache` 的眼球材质写改用 `Shader.PropertyToID("_EyePosition")` 的 int 重载，消除每角色每帧的字符串查找。
- `RemoteRagdollLod` 的 fallback 列表抽出为 `PassesFallbacks`，距离项留在 `Evaluate`；新增 `ShouldThrottleAnimation`（无距离项、按物理 tick 缓存）供聚集车道使用，原有距离层级状态机不受影响。
- 与其它补丁共享目标的新补丁（`RemoteClusterAnimationThrottle` 对 `CharacterRagdoll.FixedUpdate`、`BodypartMovementForceIdentity` 对 `Bodypart.AddMovementForce`）都读 `__runOriginal`：先前的 Prefix 已决定跳过时，本补丁不做任何事，两条车道互斥而非叠加。
- 项目新增对 `UnityEngine.PhysicsModule`、`UnityEngine.AnimationModule`、`Unity.Animation.Rigging`、URP 与 `PhotonRealtime` 的只读引用（`Private=false`），此前用发射访问器绕开这些类型只是因为缺引用。

## 曾经尝试后移除的实现

以下 15 项曾作为「保留配置键」留在配置文件里（无 `PatchDefinition`，置 `true` 也只输出 `no definition registered`）。这会让配置文件看起来像功能清单而不是变更记录，已全部从 `PatchId` 与配置绑定中删除。此处仅作记录，配置文件不再出现这些键。

| 名称 | 移除原因 |
|---|---|
| `HeatEmission` | 目标由 `HeatEmissionScan` 覆盖。 |
| `MapHandler` | 旧索引/硬编码局部可能破坏结算与切机场。 |
| `RopeRendering` | 依赖 14 条含编译器 selector 缓存字段的连续 IL，运行时匹配 0，无法证明唯一。 |
| `ShakyRock` | NonAlloc 替换与 foreach/异常区/`ComputePenetration` 语义耦合。 |
| `PlayerEyeLook` | stale target / 已销毁对象风险，目标由 `EyeLookComponentCache` 覆盖。 |
| `WeightRefresh` | 缺少可靠帧末 RPC 合并调度点，目标由 `WeightRefreshCoalescing` 覆盖。 |
| `RavenPerch` | 玩家扫描与 FlyAway/生成/视角决策交织。 |
| `EruptionSpawner` | Transpiler 生成无效 IL（HarmonyException: IL Compile Error）。 |
| `BeeSwarm` | acquisition 扫描与 owner/TryDeAggro/蜂巢即时响应耦合。 |
| `ZombieManager` | 清理优先级与 `maxActiveZombies` 顺序不应被降频改变。 |
| `SleepingZombie` | 唤醒扫描与可见性/视线/状态转换耦合，目标由 `SleepingZombieScan` 覆盖。 |
| `Tornado` | 多客户端 raycast 缓存可能分叉。 |
| `GloomVoiceConfuser` | 复杂 Update 同时处理主机转换/分配清理/偷取表现/elapsed timer。 |
| `MovingLava` | 未证明 scene view/owner 为空/主机迁移下的唯一权威规则。 |
| `ExplosionShaderWarmup` | `Shader.WarmupAllShaders()` 会全局阻塞预热全部 shader 变体，已实测导致生成炸药时主线程冻结。 |

没有 `EnableAllExperimental` 或任何批量开关；每项必须单独决定。

## 配置与诊断

首次运行生成 `BepInEx/config/com.peak.safeoptimizer.cfg`，分 `Fixes`、`Experimental`、`Graphics`、`Diagnostics` 四节。

| 配置键 | 默认 | 含义 |
|---|---:|---|
| `Diagnostics.DetailedLogging` | `true` | 每个修复原因首次命中输出一次，之后按原因最多每 30 秒聚合一次。任何情况都不逐帧输出。 |
| `Experimental.CollisionCallbackReuse` | `false` | 见上文非 Harmony 车道；需 `Fixes.SnowballContactGuard` 的两个补丁都安装成功。 |
| `Graphics.CapBoneWeights` | `false` | 把 `QualitySettings.skinWeights` 限制到每顶点 2 根骨骼。只降不升，关闭时写回启动值。代价：肩/髋关节形变略糙。 |

**配置里没有任何需要手填的数值。** 所有阈值（爆炸球数与集群/距离分档、远端 LOD 的进出距离、三个降频间隔、骨骼权重档位）都是代码里的命名常量，紧挨着论证它们的注释。理由很简单：配置文件里的一个数字，使用者无法验证它是否合理，而错误的值会安静地产生错误的行为——例如把降频间隔调到 10，远端角色就不再是「动画略顿」而是「明显坏了」。要改这些值应该改代码并重新论证，不是在 cfg 里试。

诊断实现要点：详细诊断只对修复/回退原因计数，正常优化路径不记账；同一原因首次后最多每 30 秒输出一条聚合日志，不会逐帧刷屏。

配置文件由 BepInEx 持久化，插件不会在更新、启动或退出时删除 `com.peak.safeoptimizer.cfg`。默认值只影响首次生成或缺少对应键的配置；已有 cfg 中的显式值会继续保留。

### 热重载

- 布尔补丁开关（`Fixes.*` 与 `Experimental.*` 中**已注册**的项）支持运行时热重载：在 BepInEx ConfigurationManager 里改值后立即 install/uninstall，无需重启。
- 诊断开关由运行时代码读取 `.Value`，修改后即时生效，无需 Harmony install/uninstall。
- **两条非 Harmony 车道的热重载走轮询而非事件**：`Graphics.CapBoneWeights` 与 `Experimental.CollisionCallbackReuse` 不是补丁开关，`ConfigFile.SettingChanged` 路径（可能在非主线程触发）刻意忽略它们；`Plugin.Update` 每帧各调一次它们的 `Update`/`Evaluate`，两者都幂等且在配置未变时只做一次配置读 + 早退。因此 `CapBoneWeights` 改值后最迟 2 秒生效（重申周期），`CollisionCallbackReuse` 下一帧生效，**关闭时都会写回启动值**。
- 热切换在日志输出 `HotReload:` 一行，例如 `HotReload: <名称> enabled→installed` / `disabled→uninstalled`，失败或跳过则带 `Skipped*`/`Failed*` 与原因。每项切换独立隔离，失败只影响该项。

## 构建与安装

需要 .NET SDK、PEAK 与 BepInEx 5。项目以只读方式引用游戏依赖，所有引用 `Private=false`，构建不复制 BepInEx/Harmony/Unity/Photon/Zorro/Assembly-CSharp。

```powershell
dotnet build -c Release
```

输出目录 `bin/Release/netstandard2.1/`，预期仅含本项目 DLL、PDB、deps。安装：手动复制 `PeakSafeOptimizer.dll` 到 `PEAK/BepInEx/plugins/`。

## 当前限制

- 完成的是构建、结构验证与严格 IL 自检；**多人压力基准、长时稳定性与性能基准仍未完成**，不给帧率数值承诺。
- `DetailBodypartThrottle`、`CollisionCharacterLookup`、`AnimatorValuesHash`、`PlayerNameUiWrites` 尚未完成游戏内验证；若目标构建结构不符会各自 fail-closed 单独跳过，不影响其余补丁。
- 有观感/行为代价的项已全部改为**默认关闭**，需要自行开启并亲眼确认：6 项远端动画降频（`RemoteClusterAnimationThrottle`、`DetailBodypartThrottle`、`RemoteRagdollLod` 一个开关下的 4 项）、`Graphics.CapBoneWeights`，以及全局引擎标志 `Experimental.CollisionCallbackReuse`。
- `CollisionCallbackReuse` 是**引擎全局标志**。游戏侧唯一的跨帧 `Collision` 引用已定位并加守卫，已装的 20 个模组 DLL 元数据里也没有任何 `OnCollisionEnter/Stay/Exit`（除 MMHOOK 自动生成的 hook 库），但第三方模组仍是唯一无法静态穷尽的面。装机后滚一颗雪球确认它还能变大即可覆盖这条路径。
- 长序列 IL 模式在多模组预处理环境不可靠；后续新增补丁优先窄 Prefix + 精确结构验证。
- 遇到崩溃、存档/网络/场景切换/物品/物理/渲染异常，立即关闭最近启用的单项并保留完整日志。测试步骤见 [TESTING.md](TESTING.md)。
