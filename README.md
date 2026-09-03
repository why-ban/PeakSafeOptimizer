# PeakSafeOptimizer 1.4.0

PEAK 的 BepInEx 5 插件，用于修复已确认的空引用/越界异常，并针对**多人玩家数量**与**地面物品数量**带来的每帧重复计算做保守削减。

这不是通用画质/帧率包。它不修改渲染、不修改网络发包频率、不创建任何全局调度对象，也不接管游戏的物品状态机。

本仓库不会自动复制 DLL、部署到游戏目录、创建发行包或执行 Git 操作。

## 受审计的游戏构建

| 项目 | 值 |
|---|---|
| Assembly-CSharp MVID | `c88cc526-8d4e-40fa-afdb-e24e4c233bd0` |
| Assembly-CSharp SHA-256 | `21cbf3a6585a72778a2e5fb007e36ca246002da5ae8209a6d998673633d82759` |
| 插件版本 | `1.4.0` |

SHA-256 是审计记录值，不参与运行时判断。MVID 由 `BuildFingerprint.Capture()` 在启动时读取游戏 Assembly-CSharp 的 `ModuleVersionId` 得到。

## 兼容性模型（MVID 不再是全局硬门控）

当前版本的关键变化：**MVID 不是全局硬门控，而是逐补丁的兼容条件**。结构级守卫可以在未知构建上继续自验；完整替换、网络语义改写和 Transpiler 在尚未提供方法级行为指纹前只在审计 MVID 上安装。

- 启动时读取游戏 Assembly-CSharp 的 MVID 并与其 SHA-256 一起输出一行指纹日志。
- MVID 与审计值 `c88cc526-8d4e-40fa-afdb-e24e4c233bd0` 一致 → 兼容模式为 **`certified`**。
- MVID 不一致（游戏更新或不同构建）→ 兼容模式为 **`structural-validation`**。
- **1.4.0 起 MVID 不再门控任何补丁。** 它只是一个标签，含义是「这个构建有没有人逐行重读过」。是否安装完全由每个补丁自己的
  `Initialize` 结构断言与目标签名断言决定：形状符合就装，不符合就单独报 `SkippedStructureMismatch`。
  1.3.0 及以前有一道硬门——43 个补丁写了 `requireCurrentMvid: true`，而那个参数会在结构验证**之前**就直接返回，
  于是游戏一更新就几乎整个插件被静默跳过，这与结构验证的存在意义正好相反，也让每次游戏更新都变成一次发布事件。已删除。
- 结构验证抓不到的是「成员形状没变但方法体语义变了」。这只能靠对着新反编译源重读来发现，所以看到 `structural-validation`
  时，应优先复查下表里 `Fingerprint` 与 `StrictIl` 等级的补丁。

每个补丁声明一个 `CompatibilityLevel`。它现在是**复查优先级**而非安装门控：

| 等级 | 含义 | 本项目的典型形态 |
|---|---|---|
| `Structural` | 依赖的全部是 `Initialize` 能校验的成员形状，结构验证确实充分 | 简单 FieldRef / null guard（仅 `RunBasedValues`） |
| `Fingerprint` | 完整替换 Prefix：形状相符不等于替换体仍然等价，游戏更新后要重读 | 大多数完整替换/守卫 Prefix |
| `StrictIl` | 正确性还依赖 Photon 所有权/RPC 语义或形状检查表达不了的 IL，复查优先级最高 | 网络面改写 |

补丁失败时单独跳过并分类（不连坐）：

- `SkippedStructureMismatch`：`Initialize` 结构校验（成员缺失/变形）拒绝当前构建。
- `SkippedIlMismatch`：IL 匹配或 IL 生成拒绝当前构建。
- `SkippedUncertifiedBuild`：**已废弃**，1.4.0 起不再产生（旧日志里出现过）。
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
- **原版每帧抛异常的路径在范围内**。一次异常意味着抛出 + 堆栈捕获 + 字符串格式化 + BepInEx 的同步写盘，全在主线程；每帧一次就是可观的持续开销，不只是日志噪声。`BinocularSunGuard`（1544 次）与 `ItemOptimizerDeregisterGuard`（67 次）就是按这条标准加的。
- 排查本插件问题请按 `PeakSafeOptimizer` 关键字过滤，避免与其它模组（如 `PeakStatsEx`、`PEAK_Visuals`）产生的异常混淆。

仍未完成：多人压力基准、长时稳定性、性能基准。本文不提供任何帧率数字。

## 已注册补丁（41 个补丁 / 37 个开关）

配置文件里的每一个开关都对应真实注册的补丁，不存在只显示不生效的键。下表「默认」列均为开关默认值。

**默认值策略**：只有**行为等价**（结果与原版逐位相同）或**修复原版故障**的补丁默认开启。任何用观感、手感或动画精度换帧数的项一律默认关闭——那个取舍属于安装者，不属于模组。

这条策略有**两项明文例外**，它们默认开启但并非行为等价：

- `Experimental.ExplosionScale`：削减爆炸球数并在集群档缩小尺寸，代价（尺寸只作用于球体网格、屏幕震动同比变弱）写在它自己的条目里。爆炸是本模组能碰到的最严重卡顿，按默认关闭处理等于让它一直存在。
- `Experimental.TumbleWeedTargetScan`：目标解算改为每 5 个物理刻（0.1 秒）才跑一次，期间复用旧目标，所以一个新出现的更近目标最多滞后 0.1 秒才被采纳。施力仍然每刻进行，方向按缓存目标的**当前** Center 重算，所以被追的人不会被追丢；滞后只影响「换成追谁」。风滚草只在沙漠/方山出现，可见性极低。

默认关闭的项（**6 个补丁 / 3 个开关**）：`Experimental.RemoteRagdollLod`（一个开关 = 4 个补丁，远距姿态凝固）、`Experimental.DetailBodypartThrottle`、`Experimental.RemoteClusterAnimationThrottle`（远端动画 25Hz 步进）。另有两条默认关闭的非 Harmony 车道 `Graphics.CapBoneWeights`（关节形变变糙）与 `Experimental.CollisionCallbackReuse`（进程级引擎标志），它们不是补丁开关，不计入 44 个补丁 / 40 个开关。

**必须协同的补丁合并为一个开关**，避免半开状态看起来受支持却不是：`Experimental.RemoteRagdollLod` 管 4 个补丁（它们共用同一份距离层级判定，只开其中几个会让远端角色一边跳过动画图求值、一边仍被驱动向动画目标，这不是任何一档被论证过的状态）；`Fixes.SnowballContactGuard` 管 2 个补丁（一个写快照、一个读快照，单开任一方都无意义）。因此 41 个补丁对应 37 个开关。

**本文按优化性质分节（稳定性 / 等价 / 规模化 / 聚集专项 / 远端实验），与源码目录不是一对一**。各小节标题只给项数不给目录；按目录统计 `context.Register(PatchId.…)`，四个目录的实际注册数是：`Patches\Stability` 7 个、`Patches\Scale` 20 个、`Patches\ScaleExperimental` 13 个、`Patches\Equivalent\MathAndScan` 1 个，合计 41。

### 稳定性修复（8 项，全部默认开启）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.RunBasedValues` | `true` | Structural | Prefix：`SerializableRunBasedValues.SerializeRunBasedValues(BinarySerializer)` | 为 null 的两个运行数据字典分配空字典，避免序列化空引用。纯守卫、不吞原方法异常。 |
| `Fixes.Parachute` | `true` | Fingerprint | Prefix（完整替换）：`CharacterData.UpdateHasParachute()` | 对不完整 character/player/slot/backpack 状态保守清空降落伞缓存，保留 Scoutmaster 与「手持→槽位→背包」优先级。 |
| `Fixes.RopeSegmentGuard` | `true` | Fingerprint | Prefix：`CharacterRopeHandling.Climbing()` | 当所持绳索的 `GetRopeSegments()` 为 null/空，或 `climbingAPI` 为 null 时跳过该帧攀爬。守卫每帧入口而非底层 API，避免把异常换成 `NullReferenceException`。 |
| `Fixes.IKItemGuard` | `true` | Fingerprint | Prefix：`CharacterAnimations.ConfigureIK()` | 当 `currentItem` 非空但 prefab 缺 `Hand_R`/`Hand_L` 子节点时跳过该帧手部 IK 定位。只在手部节点缺失时跳过，ReachIK 分支不受影响。诊断**按缺失的是哪一侧手部节点分别记账**，所以日志能直接区分是右手还是左手节点缺失，不必再去猜是哪个 prefab。 |
| `Fixes.EmoteWheelGuard` | `true` | Fingerprint | Prefix：`GUIManager.UpdateEmoteWheel()` | **只在原版真会解引用空值的那一帧跳过**。原版有两处短路：`canEmote` 在 `data.dead` 时提前返回、不读 `refs.stats`；`canEmote && input.emoteIsPressed` 在 `canEmote` 为假时不碰 `input`。这两种帧原版不抛异常，而是落入 `else if (usingEmoteWheel)` 把表情轮**关掉**。1.1.0 把这条关闭分支一起跳过了，使 `usingEmoteWheel` 锁死，连带暂停菜单、光标锁定与一个移动门控卡住——这是 1.2.0 修掉的功能性回归。现在精确复刻两处短路，其余一律放行。只读不写。 |
| `Fixes.BinocularSunGuard` | `true` | Fingerprint | Prefix：`Action_ShowBinocularOverlay.TestLookAtSun()` | 举起望远镜时该方法每帧执行，而原版的 guard clause 只判了 `localCharacter`，随后无保护地解引用 `Singleton<MapHandler>.Instance`、`DayNightManager.instance.sun`，以及 guard clause **之后**的 `MainCamera.instance`、`localCharacter.Center`（走 `partDict` 直索引，可抛 `KeyNotFoundException`）与 `Singleton<AchievementManager>.Instance`（`Action_ShowBinocularOverlay.cs:40-55`）。实测单局 1544 次逐帧空引用。守卫补齐这五处，并**先复刻原版的 guard clause**（biome、日照强度）再检查其后三项，避开非方山/夜间下的诊断假阳性。跳过的唯一后果是该帧不做天文成就判定。 |
| `Fixes.ItemOptimizerDeregisterGuard` | `true` | Fingerprint | Prefix：`Peak.ItemOptimizationManager.DeregisterItem(Item)` | 原版 `ItemOptimizer.OnEnable` 在 item 为 null 时写 `enabled = false`，该写入同步触发 `OnDisable`，后者拿同一个 null 去 `Dictionary.Remove` 并抛 `ArgumentNullException`（`ItemOptimizer.cs:9-24`）。实测 67 次。null 键本就不在字典里，跳过不丢任何条目。 |

### 等价改写（1 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.Campfire` | `true` | Fingerprint | Prefix：`Campfire.EveryoneInRange(float)` | 单次扫描同时维护 `_charactersInRadius` 副作用；补丁自身状态不可用时回退原方法。 |

### 规模化（7 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Fixes.HeatEmissionScan` | `true` | StrictIl | Prefix（完整替换）：`CharacterHeatEmission.Update()` | 距离判定平方化，等价替换原方法。网络面（读 `photonView.IsMine`），故升级为 StrictIl。 |
| `Fixes.ItemCollisionMode` | `true` | Fingerprint | Prefix：`Item.UpdateCollisionDetectionMode()` | 目标值仍由 `itemState` 决定，仅当 Rigidbody 已是目标值时跳过赋值。 |
| `Fixes.CampfireProtectionMerge` | `true` | Fingerprint | Prefix：`Campfire.ApplyCampfireProtection()` | 把同帧两次全量扫描合并为一次，单次扫描同时得出 `everyoneInRange` 与 `_charactersInRadius`，再复用于 OrbFogHandler 写入与 buff 分支。 |
| `Experimental.ZombieScanRange` | `true` | Fingerprint | Prefix：`MushroomZombie.ReadyToDisable()` | 主机侧每帧调用。平方距离 + 两标记都真后提前结束循环；阈值非正回退原方法。 |
| `Experimental.ZombieSpawnScanRange` | `true` | Fingerprint | Prefix：`MushroomZombieSpawner.ReadyToSpawn()` | 主机侧每帧调用。单阈值平方化，命中即返回；阈值负时回退。 |

### 规模化实验（6 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Experimental.WeightRefreshCoalescing` | `true` | StrictIl | Prefix：`CharacterItems.RefreshAllCharacterCarryWeightRPC()` | 同帧**内容签名**合并：仅当折叠签名未变时跳过同帧重复全员负重刷新。目标即 RPC 入口，网络面升级 StrictIl。 |
| `Experimental.SleepingZombieScan` | `true` | Fingerprint | Prefix：`MushroomZombie.DoSleeping()` | 距离平方化并把 `Vector3.Angle` 延后到距离判定通过后。私有方法用 `AccessTools.MethodDelegate` 绑定。 |
| `Experimental.ItemScaleRedundantWrite` | `true` | StrictIl | Prefix：`ItemScaleSyncer.Update()` | 缩放输入、目标 transform 身份、当前 localScale 全部未变时跳过原方法。owner 缩放变化（可能发 RPC）必定放行，网络面升级 StrictIl。 |
| `Experimental.RemoteItemInterpolationThreshold` | `true` | StrictIl | Prefix：`ItemPhysicsSyncer.FixedUpdate()` | 远端 Ground 物品已精确到达插值目标时跳过两次无效 Rigidbody 移动；不触碰发包/kinematic。 |
| `Experimental.EyeLookComponentCache` | `true` | Fingerprint | Prefix（完整替换）：`PlayerEyeLook.Update()` | 逐行复刻原方法，仅把循环内每帧 `GetComponent<AnimatedMouth>` 改为按 Character 缓存。 |
| `Experimental.ExplosionScale` | `true` | Fingerprint | Prefix：`ExplosionEffect.GetPoints()` | **默认开启政策的第一个明文例外**（第二个是 `TumbleWeedTargetScan`），不是行为等价。爆炸球数与尺寸自适应削减。资产解码确认全库只有 3 个 `ExplosionEffect`（三个 Dynamite 系预制体），主炸药的序列化值是 3/2 = **10 球**（不是代码默认 4/2 算出的 13）、Small/Fae 是 3/0 = 4 球。默认上限 2/1：近距单发 5 球（Small/Fae 3 球）；集群时第 2 发起缩到 85%、第 4 发起降为 2 球 + 60% 尺寸；50–120 米 2 球，120 米外仅中心球。集群判定半径不是魔数，而是按当发自己的 `baseScale` 推导出的火球半径（orb 根 localScale 4.4401164 × Icosphere AABB extent 1.372998 ≈ `baseScale × 6.0962`，主炸药约 6.10 米、Small/Fae 约 3.05 米），病态 `baseScale` 时回退到 3.5 米常量。只减不增、不为负。**两项观感代价**：缩放只作用于 orb 的球体网格（orb 内两个粒子系统是 `scalingMode = Local`，不继承父级缩放）；减球数会同比减弱屏幕震动（每球两个叠加式 `AddScreenshake`，20→10→4 次）。伤害/击退/半径均不受影响（在兄弟节点 `AOE` 上）。火箭/背包/GhostBall 的爆炸不含 `ExplosionEffect`，不在本补丁范围。 |

### 其余等价规模化（8 项）

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

### 聚集与方山专项（4 项）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用 |
|---|---:|---|---|---|
| `Fixes.PocketBehaviorGuard` | `true` | Fingerprint | Prefix：`CharacterItems.UpdatePocketBehaviors()` | 加入/注册窗口 `character.player` 未链接时跳过该帧，消除实测 14 次逐帧空引用；数据就绪后逐行走原方法。 |
| `Fixes.BodypartDragIdentity` | `true` | Fingerprint | Prefix：`Bodypart.Drag(float, bool)` | 有效 drag 精确等于 `1f` 时跳过两次速度乘法（IEEE 恒等）。倒地/被风滚草撞倒的角色 `currentRagdollControll == 0`，命中率最高。 |
| `Fixes.LightVolumeSampleCache` | `true` | Fingerprint | Prefix+Postfix：`LightVolume.SamplePositionAlpha(Vector3, bool)` | 按截断后的光照体素缓存 `Texture3D.GetPixel` 结果；每角色每帧的语音遮蔽采样、环境混响与风区采样共享同一缓存。贴图实例或 `updateCount` 变化即整体失效。 |
| `Experimental.TumbleWeedTargetScan` | `true` | Fingerprint | Prefix：`TumbleWeed.FixedUpdate()` | **默认开启政策的第二个明文例外**（第一个是 `ExplosionScale`），不是行为等价。目标扫描默认每 5 个物理 tick （0.1 秒）解一次并在期间复用旧目标，所以一个新出现的更近/角度更好的目标最多滞后 0.1 秒才被采纳（原版每 tick 重选，`TumbleWeed.cs:40`）。`AddForce` 仍每 tick 以同样大小施加，方向按缓存目标的**当前** Center 重算，所以滞后只影响「换成追谁」，不影响对已选目标的跟踪；目标被销毁/数据丢失/死亡时下一 tick 立即重扫。距离比较改平方（对原版非负距离的 `<` 比较精确）。风滚草只在沙漠/方山出现，可见性极低。 |

### 远端与物理实验（5 项，**全部默认关闭**）

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与风险 |
|---|---:|---|---|---|
| `Experimental.RemoteRagdollLod`（本组共用一个开关） | `false` | Fingerprint | Prefix：`CharacterRagdoll.FixedUpdate()` | 仅对 60 米外、已预热且无交互的远端玩家跳过动画图、IK 与姿态采样；50 米内恢复。近距聚集场景不触发。 |
| 同上开关 | `false` | Fingerprint | Prefix：`Bodypart.Animate(float, float)` | 与同一远距判定共享状态，跳过关节动画驱动；Gravity、Drag、ToggleUseGravity、ApplyForces 保持运行。 |
| 同上开关 | `false` | Fingerprint | Prefix：`Bodypart.AddMovementForce(float)` | 远距被动 ragdoll 不再加入本地动画移动力；网络位置与碰撞路径不变。 |
| 同上开关 | `false` | Fingerprint | Prefix：`CharacterAnimations.Update()` | 远距被动 ragdoll 跳过大部分 Animator 参数写入，但保留投掷/表情计时、表情退出、水状态复位和辅助位置更新；本地、bot、观战、抓取、攀爬、死亡、装备窗口等均回退原版。 |
| `Experimental.DetailBodypartThrottle` | `false` | Fingerprint | Prefix：`Bodypart.Animate(float, float)` | 非本地、非交互玩家的 59 个手指/jiggle/脚趾/嘴颌部件默认每 3 个物理 tick 驱动一次，并按部件错峰。近距聚集时仍生效，是当前直接削减该场景托管物理工作的主要补丁。 |

爆炸相关说明：`Dynamite.RPC_Explode` 每次实例化 `explosionPrefab`（含 `ExplosionEffect`），原版 `GetPoints` 按**预制体序列化后的**值生成爆炸球——主炸药 `VFX_DynamiteExplosion` 是 `explosionPointCount=3` / `subExplosionPointCount=2`（`baseScale=1.0`），即 `1 + 3 + 3×2 = 10` 个球；`VFX_DynamiteExplosionSmall` 与 `VFX_DynamiteExplosionFae` 都是 `3/0`（`baseScale=0.5`），即 4 个球。`ExplosionEffect.cs:30-32` 里的 `4/2` 只是字段默认值，被这三个预制体的序列化值覆盖，所以 `1 + 4 + 2*4 = 13` **不是运行时真相**（与本文前面 `ExplosionScale` 条目里的数字一致）。每个球再实例化 orb 并做 Animator/MeshRenderer/MaterialPropertyBlock 处理，多人同时投掷时放大。`ExplosionScale` 使用 16 项预分配环形缓冲记录邻近爆炸，整个判定零分配；它**既削减球数**（基础上限 `PointCap=2` / `SubPointCap=1`，主炸药 10 球降到 5 球）**又在集群档缩小尺寸**（第 2 发起 85%、第 4 发起 60%）。球内 `new MaterialPropertyBlock()` 的复用**未实现**——该分配位于 `Start()` 内嵌套的 `IExplode` 局部函数（编译器生成方法）里，属于多模组环境下已证明不可靠的目标形态，故不做。全局 `Shader.WarmupAllShaders()` 实现已因主线程冻结事故彻底删除。

### 聚集专项（5 项 + 2 条非 Harmony 车道）

这一组是针对「离队友远就不卡、聚集起来才十几帧」这条实测事实设计的：每一项的收益都必须随屏幕上的玩家数增长，纯距离门控的老车道（`RemoteRagdollLod*`）在聚集时完全不触发，因此不算在内。

| 配置键 | 默认 | 兼容等级 | Harmony / 目标 | 作用与代价 |
|---|---:|---|---|---|
| `Experimental.RemoteClusterAnimationThrottle` | `false` | Fingerprint | Prefix（执行 tick 完整替换）：`CharacterRagdoll.FixedUpdate()` | 远端、满控、无交互玩家的动画图默认每 2 个物理 tick 求值一次，并按实例错峰。求值时传入**累计 dt**，所以动画总时长不变，只是时间分辨率降到 25Hz。跳过的 tick 仍执行 `SetPhysicsMats`；`SaveAnimationData`/`SaveAdditionalTransformPositions` 故意不执行（它们读的是动画姿态，在没求值的 tick 上读到的是物理姿态，照读会让角色瘫软）。渲染骨骼由 PhysX 直接驱动，不会冻结。代价：远端动画以 25Hz 步进，近距可感知。 |
| `Fixes.BodypartMovementForceIdentity` | `true` | Fingerprint | Prefix：`Bodypart.AddMovementForce(float)` | 力恰为零向量时跳过（`forcesToAdd += Vector3.zero` 逐位恒等）。站着不动的人 `worldMovementInput_Lerp` 收敛到零、`GetMovementForce` 对不可移动状态直接返回 0，所以聚集聊天时全员每部件每 tick 命中。 |
| `Fixes.CollisionContactsNoAlloc` | `true` | Fingerprint | Prefix（完整替换）：`CharacterMovement.OnCollision(Collision, bool, Bodypart)` | 原版对 `Collision.contacts` 取值 6 次（`CharacterMovement.cs:928`、`:948`、`:953`×2、`:955`×2），本版通过 `GetContact(0)` 只读一次。引擎源码逐位等价（`Collision.cs:104-117` 与 `ContactPair.cs:110-137` 翻转逻辑一致）。零接触点这一分支交回原版（原版会抛 `IndexOutOfRangeException`，不替它改语义）。 |
| `Fixes.SnowballContactGuard`（本组共用一个开关） | `true` | Fingerprint | Prefix（完整替换）：`Snowball.OnCollisionStay(Collision)` | 全仓唯一跨帧持有 `Collision` 的地方（`Snowball.cs:105/227`）。改为在回调内快照 collider / gameObject / `GetContact(0).normal`，这是开启回调复用的前提。 |
| 同上开关 | `true` | Fingerprint | Prefix（完整替换）：`Snowball.ProcessLastCollision()` | 消费上面的快照（内联原版 `Scale` 与 `ClassifyCurrentContact`），分支、比较与字段写入顺序逐行复刻。无快照时（补丁半装）交回原版。 |

两条非 Harmony 车道：

这两条不走 Harmony，因为它们要改的不是某个方法的行为，而是**引擎/管线的全局状态**：一个是 `Physics` 的静态标志，一个是 URP 资产与 `QualitySettings` 的字段。没有可打的目标方法，直接写就是最省的做法。它们由 `Plugin.Update` 每帧驱动，`OnDestroy` 里在 Harmony 卸载**之前**先还原（引擎全局状态与补丁卸载是否成功无关）。回滚都是「只还原自己写过、且当前值仍是自己写的那个值」，玩家或游戏中途改过的值不会被覆盖。

| 配置键 | 默认 | 作用与回滚 |
|---|---:|---|
| `Experimental.CollisionCallbackReuse` | `false` | 置 `Physics.reuseCollisionCallbacks = true`。默认 false 时引擎对**每次上报的碰撞**都先分配一个 `Collision` 对象加一个 `ContactPoint[]`（`Collision.cs:87-94` 构造函数里就填好，回调看不看 contacts 都付钱），而每个接触对的两侧各派发一次（`Physics.cs:1440-1454`）——聚集的布娃娃正是这个量最大的场景。仅当上面两个 Snowball 守卫都安装成功才会开启，任一未装立即写回原值；退出时也写回。 |
| `Graphics.CapBoneWeights` | `false` | 把 `QualitySettings.skinWeights` 限制到每顶点 2 根骨骼——游戏画质菜单没有这一项，而每顶点骨骼数是每个蒙皮角色的逐顶点成本，随屏幕上的人数放大。只降不升（已经 ≤2 就不写）。启动 5 秒后才首次生效（等游戏自己的画质设置先写完，否则快照会存到玩家没选过的值），之后每 2 秒重申一次。只有本车道自己写过、且当前值仍是它写的那个值时才会在关闭/退出时写回启动值。默认关闭：肩/髋关节形变会略糙。 |

这条车道的取舍原则：**只覆盖游戏画质菜单没有提供的项，且不默认改变画面**。每顶点骨骼数在 `Assembly-CSharp` 里没有对应设置类，玩家无从调整，所以由补丁提供开关——但默认关闭，因为降低骨骼权重会让关节处的形变变糙。曾经这里还有一项强制关闭 MSAA（游戏同样没有抗锯齿设置），已移除：关抗锯齿是画面决定，应该由玩家做。阴影距离与级联数曾经也在这里，已移除——游戏自带「阴影距离」设置写的就是这两个值（`ShadowDistanceSettings.cs:17-41`，200/150/75/0 配 2/2/1/1），压它们等于偷偷覆盖玩家已经做过的选择，而且强制单级联会让远处阴影随移动漂移（级联包围球每帧跟着相机重新拟合、没有 texel 对齐）。玩家嫌卡自己调档位即可。

## 1.4.0 修复的问题（变更说明）

游戏做了一次底层更新（官方自己做了一批性能优化，外加主机版移植），Assembly-CSharp 的 MVID 从
`3f24c13b-…` 变为 `c88cc526-…`。本版做三件事：拆掉 MVID 硬门、把被官方改动打到的补丁适配到新版、
删掉三个收益已被官方吃掉的补丁。

**拆掉 MVID 硬门（最重要的一条）。** 1.3.0 及以前，`PatchInstaller` 在结构验证**之前**有一句
`if (definition.RequiresCertifiedBuild && !fingerprint.IsCurrent) return;`，而 44 个补丁里有 43 个写了
`requireCurrentMvid: true`。于是游戏一更新，几乎整个插件在任何补丁有机会检查「我读的成员是否真的变了」
之前就被静默跳过——这与结构验证的存在意义正好相反，也让每次游戏更新都变成一次必须发版的事件。该门已删除。
现在唯一的门是每个补丁自己的 `Initialize` 结构断言与目标签名断言：形状符合就装，不符合就单独报
`SkippedStructureMismatch`。MVID 降级为日志标签，`CompatibilityLevel` 降级为**复查优先级**。
`SkippedUncertifiedBuild` 这个结果不再产生。

代价要说清：结构验证只能证明「成员形状没变」，证明不了「方法体语义没变」。后者只能靠对着新反编译源
重读来发现，所以 `structural-validation` 的正确读法是「这个构建还没人逐行重读过，优先复查
`Fingerprint` 与 `StrictIl` 等级的补丁」。

**删除三个补丁**（44 → 41，开关 40 → 37，默认开启 38 → 35）：

- `GenericOptimizerRange` —— 官方自己把 `AnyCharacterWithinRange` 改成了平方比较 + 索引循环 +
  position 提出循环，与本补丁做的事完全相同。更糟的是官方给 `GenericOptimizer` 加了一个 **private**
  的 transform 缓存属性，插件的程序集绑不到它，C# 成员查找会落到 `Component.transform` —— 于是本补丁
  变成每帧每实例多一次 native 调用换零收益。2133 个实例。
- `StatusEmitterScan` —— 官方给 `StatusEmitter` 加了 transform 缓存并把距离合并成一次求值，
  `Update()` 甚至不再调用 `InRange()`。逐项计数后，本补丁的 native 调用与开方次数与原版**完全相同**，
  而多出 Harmony 包装帧、三次委托判空、try/catch 帧与两次 FieldRef 调用。净负。
- `RagdollPhysicsMats` —— 官方把 `CharacterRagdoll.FixedUpdate` 里的 `SetPhysicsMats()` 门控成了
  `if (character.IsLocal)`，所以这个冗余过滤器现在只能省下一个角色的量（原来是每个角色）。它是这条
  车道里最复杂的补丁（CWT + 双阶段 pending/promote + 300 帧/5 秒材质审计），收益/复杂度不再成立。

**适配新版的行为偏离（4 处真实偏离 + 1 处逻辑缺口）**：

- `RemoteClusterAnimationThrottle` 在非求值 tick 与替换体里各调一次 `SetPhysicsMats`。新版原版对远端
  角色**不调**，而这个补丁只处理远端角色 —— 继续调就是新增原版不做的 native `col.sharedMaterial` 写入。
  两处调用、访问器、委托、诊断名 `set-physics-mats-failed` 一并删除。
- `IsLookedAtScan`：`UIPlayerNames.Init` 从「返回 int」改成「接收 `Action<int>` 回调」，这是全仓
  **唯一一条在新版会失败的断言** —— 不改则补丁在拆门之后被 fail-closed 跳过。同时 `Update()` 把
  `isBlind || isStruggling` 提到最前并提前 return，替换体跟上，并把相机判空下移到该 gate 之后
  （否则盲/挣扎状态下相机为 null 时会交还，而原版根本不碰相机、不会抛）。
- `BarAfflictionLayout`：官方新增 `size == 0f → width = 0f` 与 `Approximately → width = size` 两个
  分支。旧过滤器只预测 `Lerp` 结果，会跳过一次本该发生的吸附写入。谓词逐分支复刻，并把浮点相等换成
  位比较（`-0f == +0f` 会误跳）。
- `GenericOptimizerRange`（删除前顺带确认）：旧补丁里的 `candidate == null → continue` 是偏离 ——
  新旧原版都无条件解引用 `character.data`，null 元素两版都抛。
- `RemoteRagdollLod` 的不降级列表补上 `data.carrierCondor != null`。新版 `CharacterData` 新增了这个
  字段：被兀鹫抓走的角色 `currentRagdollControll` 仍是 1、能过控制阈值，同时会被带到远超 60m，
  而现有三条搬运条件全看不见它 —— 这种角色会在被抓走的过程中变软。

**其余官方改动核实为无影响**：`Bodypart` 的 `SetPositionAndRotation` 合并与 `FollowRotation_Rotation`
去掉的 `isKinematic` 守卫（那个守卫是死代码：唯一调用者 `Animate` 开头就已经在 kinematic 时提前
return）、`CharacterMovement.FixedUpdate` 把四个 `currentRagdollControll` 乘法提出循环、新增的
`AdjustDrag`（`AdjustDrag(1f) == 1f` 精确成立且逆像唯一，所以 `BodypartDragIdentity` 的恒等判定不变）、
`CharacterAnimations.ConfigureIK` 的左右手求值顺序颠倒（`IKItemGuard` 的两侧记账随之对调）、
`ItemPhysicsSyncer.FixedUpdate` 的整体重写、`TumbleWeed.GetTarget` 与 `GenericOptimizer` 的平方比较。
所有 41 个补丁的 `Initialize` 断言都用 `System.Reflection.Metadata` 直读 DLL 逐条核过签名，除上面那条
`Init` 外全部通过。

**注释约定变更**：此前注释里有约 450 处 `文件.cs:行号` 引用，游戏每更新一次就全部作废，校准成本已超过
修代码本身。新约定是**只写类名与方法名，不写行号**；已存在的行号引用不再逐条校准，视为历史坐标。

## 1.3.0 修复的问题（变更说明）

七条只读审计覆盖全插件，随后把其中**能靠读代码判定**的每一项都改掉。当时补丁数不变：44 补丁 / 40 开关 / 默认开启 38（1.4.0 已降为 41 / 37 / 35）。

功能性回归（`IKItemGuard` 自诞生起就有）：

- 持有「有 `Hand_R` 子节点但没有 `Hand_L`」的道具时，右手 IK 目标被冻结。C# 对 `a.position = f()` 是先求接收方
  再求实参，所以这种道具上原版**已经完成**两次右手写入（`CharacterAnimations.cs:397-398`）才在 `:399` 抛
  NRE，而守卫把整个 item 分支一起跳过了 —— 右手每物理帧都不跟随道具。现在先用绑定到两个 `internal` helper
  的委托复现那两次写入再跳过，并把两种缺失分开记账（`right-hand-node-missing` / `left-hand-node-missing`）。
  这与 1.1.0 表情轮回归是同一类错误：跳过了原版「抛异常之前」的写入。

分组安装其实不是原子的（三个缺陷）：

- HarmonyX 在生成包装器**之前**就把 prefix 登记进 patch info（`PatchProcessor.Patch`），所以包装器生成抛异常时
  `GetPatchInfo` 已经列出我们的方法。安装器把它记成「已安装」，`IsInstalled` 对一个**失败**的补丁返回 true，
  分组回滚根本不触发，日志里也没有任何一条说明这个开关处于半装状态。现在这种记录有独立标志：永不算 live、
  永不重装（重装会把 prefix 挂两遍）、卸载时仍会清理。
- 分组回滚在成员 `Reset` 抛异常时谎称「已干净撤回」——它问的那个判断恰好排除了这种情形，于是走了成功分支并
  丢弃了真实原因。现在会区分「Unpatch 失败、方法仍挂着」与「Unpatch 成功但 Reset 抛异常」两种形态。
- 失败安装之前就已存活的组成员会继续运行且完全不报告。现在会明确报成「故意留下的半装开关」。
- 失败启动的回滚顺序与正常关机相反（先摘补丁后关引擎标志），存在「Snowball 守卫已摘、
  `Physics.reuseCollisionCallbacks` 仍为 true」的窗口。已交换。

集群判定半径不再是魔数：

- `ExplosionScale` 原来用固定 3.5 米判断两发爆炸是否重叠，而这个值**小于主炸药自己的火球半径**——相距 5 米的
  两发在屏幕上几乎完全叠加（正是这个补丁要削的填充），却被判成两次独立爆炸、各保 5 个全尺寸球。现在按资产里的
  orb 几何推导：orb 根 localScale 4.4401164 × Icosphere 网格 AABB extent 1.372998，即 `baseScale × 6.0962`，
  主炸药约 6.10 米、Small/Fae 约 3.05 米。`baseScale` 为 NaN/0/负/无穷时回退到旧常量。判据用**当发自己**的
  半径，因为要决定的是这一发的火球会不会落在已被覆盖的屏幕上。
- 场景句柄防抖整段删除。它防的是「上一场景的坐标被当成邻居」，但 2.5 秒时间窗已经独立提供了这个保证：本游戏
  所有场景加载都是 `LoadSceneMode.Single`、都经过还要等本地角色生成的加载画面，段落切换是同场景 `SetActive`。
  它唯一的可观察行为是一条假的 `scene-handle-thrash` 报告，该诊断名已随之移除。
- 轻档的两行 `Math.Min` 在 2/1 基线下是恒等变换（注释却声称从 1 个邻居起就减球）。现在轻档有自己的命名常量，
  no-op 变成显式的，将来给轻档加真实减球是改一个常量。

诊断在度量错误的东西（修复计数被当作原版故障的证据，所以「没坏也报」比不报更糟）：

- `WeightRefreshCoalescing` 在补丁**成功生效**的路径上记诊断。`same-frame-duplicate-skipped` 已移除。
- `PocketBehaviorGuard` 每帧每 bot 记一次。`Character.player` 对 bot 恒为 null 是设计，跳过调用是对的、记账是
  错的 —— `Parachute` 早就做了同样的排除。
- `AnimatedMouthMaterial` 把 renderer / 纹理数组的检查排在 audioSource 之前，而原版守卫在 audioSource 上短路、
  整个方法体不执行，于是「没有 audioSource」的 mouth 每帧被报成原版故障。
- `RemoteClusterAnimationThrottle` 静默吞掉 null rig builder 与无效动画图并报成功。原版在那里每 tick 抛异常。
  守卫保留（重新引入每步异常更糟），但畸形 rig 现在会以 `rig-builder-unavailable` /
  `animator-graph-invalid` 暴露；非求值 tick 上的 `SetPhysicsMats` 也纳入了既有失败闩锁
  （`set-physics-mats-failed`）。

热路径：

- `LightVolumeSampleCache` 每次 cache miss 分配一个载体对象（插件里最后一处每次调用的堆分配）。Postfix 现在用
  同一个纯函数从同一个实参重算体素 key，不需要跨调用传任何状态。
- `RemoteRagdollLod` 在每帧决策缓存**之前**读一个背后有 native 判活的静态属性 —— 而缓存命中正是每秒约两万次
  调用的落点。已移到缓存之后，两处 CWT 查找也改为先 `TryGetValue`。
- `AnimatedMouthMaterial` 每帧用字符串名读两个 material 属性；属性 id 改为安装时解析一次。
- `RemoteItemInterpolationThreshold` 在四个能否决该帧的托管检查之前先付了三次 native 调用。已重排，单机与未同步
  的物品现在一次 native 都不付。
- `DetailBodypartThrottle` 每个细节部件每步算两次物理 tick，且完全不校验 `Time.fixedDeltaTime` —— 步长为 0 会
  让 tick 卡死并**永久**跳过某些部件，这是 tick 卡死唯一不指向安全方向的情形。
- `PlayerNameUiWrites` 对同一引用重复解析 2–4 次；`ItemScaleRedundantWrite` 每次调用三次多余的判活。

论证与断言错误：

- `StatusEmitterScan` 调 `ScreenVFX.StartFX()`，那个 `0.5f` 默认值是编译进本 DLL 的。整条覆盖层链一条断言都没有；
  现在四个成员全部断言，外加一条「默认值仍是 `0.5f`」的自检 —— 这是任何签名断言看不见的。
- `RopeSegmentGuard` 用「idempotent copy, recomputed next frame」为跳过辩护。那个值每次抓绳只写一次、整段攀爬
  是常量，所以 copy 只在首次成功那一帧有实质效果。真正的理由是它喂给的旋转在同一物理步内被还原。
- `BoneWeightCap` 声称 URP 每帧写 `antiAliasing`，而那个写在管线构造函数里。游戏与 URP 都不写 `skinWeights`、
  也不切质量档，所以周期性重申只作为「防这里看不见的写者」的保险并如实写明。它的 `Restore` 还把状态清零放在
  写回**之后**而不是 `finally` 里，写回抛异常会留下陈旧快照、下次关闭时覆盖玩家的新选择。
- `RemoteRagdollLodAnimator` 声明了一项不存在的观感代价（水中标志滞后）——替换体确实写了那个标志读取的参数。
- `RemoteRagdollLodPhysics` 以「无法判定委托调用是否命中 Harmony detour」为由拒绝修摩擦审计窗口。MonoMod 自己的
  启动自检做的就是这件事并断言 hook 应答，否则 BepInEx 根本起不来。真正的理由更强：全游戏写
  `Collider.sharedMaterial` 的只有四处、全部在被跳过的那个方法体内，所以原版里没有任何东西需要那些审计去纠正。
- `RemoteRagdollLod` 声称聚集车道替两条车道校验固定步长；它只替自己校验。
- `Acquire()` 排在两条会抛的语句之前，而注释断言「下面没有任何东西会抛」。

减法：

- `DetailBodypartThrottle` 自己维护了一份 fallback 列表，现在调共享的那份。
- `RemoteRagdollLodAnimator` 为了访问 Animator 的 public 成员发射了两个 `DynamicMethod`，而那个程序集本项目
  已经引用。
- 删掉 `ExplosionScale` 里九条对「既不读也不写的字段」的断言：形状断言保护不了依赖**序列化值**的论证，而那些
  论证恰好全部依赖序列化值；MVID 门控已经覆盖了 `ExplosionEffect` 被重写的情形。
- 删掉两个 docstring 声称「热重载会用」但实际零调用的方法、一条空的模块清理通道、三个零调用的发射 helper、
  以及十二处未使用的 `using`。

文档：

- README 在一段里说 13 球、另一段里说 10 球，且声称本补丁「只削减球数」（实际是既削球数又缩尺寸）。
- `Collision.contacts` 是 6 次，不是 7 或 4 —— 四处来源给了三个不同数字。
- `TumbleWeedTargetScan` 成为默认开启政策的**第二个明文例外**，代价写清（新出现的更近目标最多滞后 5 个物理刻
  被采纳）。
- `TESTING.md` 五处分节小计与自己的表格不符，结果模板漏了五项；README 各小节挂的目录路径五处错三处（分节是按
  优化性质分的，目录不是）。

## 1.2.0 修复的问题（变更说明）

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
- `CollisionCharacterLookup`、`AnimatorValuesHash`、`PlayerNameUiWrites` 尚未完成游戏内验证；若目标构建结构不符会各自 fail-closed 单独跳过，不影响其余补丁。
- 有观感/行为代价的项默认关闭（**两项明文例外见上文默认值策略**：`Experimental.ExplosionScale` 与 `Experimental.TumbleWeedTargetScan` 默认开启）。默认关闭、需要自行开启并亲眼确认的是：6 项远端动画降频（`RemoteClusterAnimationThrottle`、`DetailBodypartThrottle`、`RemoteRagdollLod` 一个开关下的 4 项）、`Graphics.CapBoneWeights`，以及全局引擎标志 `Experimental.CollisionCallbackReuse`。
- `CollisionCallbackReuse` 是**引擎全局标志**。游戏侧唯一的跨帧 `Collision` 引用已定位并加守卫，已装的 20 个模组 DLL 元数据里也没有任何 `OnCollisionEnter/Stay/Exit`（除 MMHOOK 自动生成的 hook 库），但第三方模组仍是唯一无法静态穷尽的面。装机后滚一颗雪球确认它还能变大即可覆盖这条路径。
- 长序列 IL 模式在多模组预处理环境不可靠；后续新增补丁优先窄 Prefix + 精确结构验证。
- 遇到崩溃、存档/网络/场景切换/物品/物理/渲染异常，立即关闭最近启用的单项并保留完整日志。测试步骤见 [TESTING.md](TESTING.md)。
