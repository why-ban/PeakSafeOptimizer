# PeakSafeOptimizer 1.2.0 集中测试指引

本文是一次集中测试的执行清单，目标是在一轮联机里同时验证**默认开启的 38 项等价/修复类补丁**的安全性，并针对「方山 8 人聚集锐减」「8 人以上卡顿」「地面物品过多卡顿」「爆炸卡顿」四个痛点取得可比较的观察结果。

截至当前版本，多人压力基准与长时稳定性测试**尚未完成**；以下均为待执行计划。

## 0. 测试前准备

1. 确认 Assembly-CSharp MVID 为 `3f24c13b-87b6-4c9f-9c21-3c49b6ad17e1`（`certified` 模式）；如需验证 `structural-validation` 模式，见「MVID 不匹配验证」。
2. 备份 `BepInEx/config/com.peak.safeoptimizer.cfg`。更新 DLL 时不要删除 cfg；插件本身也不会删除它。布尔补丁开关支持运行时热重载（插件已无任何 Transpiler），但如果只想排除「热重载路径本身有问题」这个变量，仍可以用「改 cfg 后完全退出重启」这条路径复测。
3. 第一层用出厂默认。44 个补丁 / 40 个开关（`RemoteRagdollLod` 管 4 个补丁、`SnowballContactGuard` 管 2 个，各是一个开关），默认开启 38 个补丁，6 个有观感代价的默认关闭。默认开启项为：

   `RunBasedValues`、`Parachute`、`RopeSegmentGuard`、`IKItemGuard`、`EmoteWheelGuard`、`Campfire`、`HeatEmissionScan`、`RagdollPhysicsMats`、`ItemCollisionMode`、`GenericOptimizerRange`、`CampfireProtectionMerge`、`ZombieScanRange`、`ZombieSpawnScanRange`、`WeightRefreshCoalescing`、`SleepingZombieScan`、`ItemScaleRedundantWrite`、`RemoteItemInterpolationThreshold`、`EyeLookComponentCache`、`ExplosionScale`、`ItemDatabaseNameLookup`、`IsLookedAtScan`、`ItemAudioManagerHash`、`BarAfflictionLayout`、`AnimatedMouthMaterial`、`CollisionCharacterLookup`、`AnimatorValuesHash`、`PlayerNameUiWrites`、`PocketBehaviorGuard`、`BodypartDragIdentity`、`LightVolumeSampleCache`、`TumbleWeedTargetScan`、`BodypartMovementForceIdentity`、`CollisionContactsNoAlloc`、`SnowballContactSnapshot`、`SnowballContactConsume`、`BinocularSunGuard`、`ItemOptimizerDeregisterGuard`、`StatusEmitterScan`（共 38 项）。

   **默认关闭的 6 个补丁 / 3 个开关**：`Experimental.RemoteRagdollLod`（一个开关 = `RemoteRagdollLodPhysics`/`Animate`/`MovementForce`/`Animator` 四个补丁）、`Experimental.DetailBodypartThrottle`、`Experimental.RemoteClusterAnimationThrottle`。另有两条默认关闭的非 Harmony 车道 `Experimental.CollisionCallbackReuse` 与 `Graphics.CapBoneWeights`，它们不是补丁开关，既不计入 44 个补丁也不计入 40 个开关（所以「3 个开关」只数上面那三个）。

4. 准备一次不装插件的同场景基准（同人数/地点/操作序列）才能判断收益。

### 启动日志检查

启动日志逐行均带 `PeakSafeOptimizer |` 前缀，用该关键字过滤可整块取出报告。

- 版本 `1.2.0`；输出一行 Assembly-CSharp 指纹：`MVID=... expected=... current=... SHA256=...`。
- 兼容模式行：`compatibility mode: certified`（MVID 匹配）或 `compatibility mode: structural-validation`（MVID 不匹配）。结构级补丁仍验证；当前 `Fingerprint`/`StrictIl` 补丁在未知 MVID 上安全跳过。
- 汇总行 `totals:`：出厂默认且 MVID 匹配、全部通过时预期 `registered=44, enabled=38, applied=38, skipped=6, failed=0, module-failures=0`（6 行 `SkippedDisabled` 是默认关闭的观感代价项：4 项 `RemoteRagdollLod*` + `DetailBodypartThrottle` + `RemoteClusterAnimationThrottle`）。
  - 没有只显示不生效的键。`registered - enabled` 应等于默认关闭的 6 项。注意 `registered` 数的是补丁而不是开关：`Experimental.RemoteRagdollLod` 一个开关对应 4 个补丁，`Fixes.SnowballContactGuard` 一个开关对应 2 个。
- 逐项结果三类行：
  - `applied  : <名称> [<兼容等级>] <目标方法>`（38 行，兼容等级为 Structural/Fingerprint/StrictIl）。
  - `failed   : ...`（预期 0 行）。
- 任何 `Failed*`（`FailedInitialize`/`FailedPatch` 等）都要记录对应项与 `Reason`；`SkippedStructureMismatch`/`SkippedIlMismatch` 表明该项结构校验拒绝当前构建，应记录具体项。
- `SkippedGroupIncomplete` 表明某个分组开关里有成员装不上，整组被撤回。`Reason` 会指名是哪个成员、为什么。这不是失败，但必须记录——它意味着该开关在当前构建上整体不可用（而不是半装）。
- 未认证构建上的行为替换补丁若出现 `SkippedUncertifiedBuild` 属于预期安全拒绝，不计为失败。
- 退出时无 `verified uninstall failed`，且没有遗留 Harmony 方法；插件使用逐补丁卸载，不调用全局 `UnpatchSelf`。

### MVID 不匹配验证（模拟旧/新 MVID，验证逐项 fail-closed 而非全灭）

MVID 不是全局门控；目标是证明「结构补丁可继续自验，行为替换补丁在未知构建安全跳过，局部更新只影响相关补丁」。

**方法 A（改预期值模拟，不改游戏文件）**：临时把 `Infrastructure\BuildFingerprint.cs` 中 `CurrentAssemblyCSharpMvid` 改为一个不存在的 GUID，重新 `dotnet build` 后启动。

- 预期日志：`compatibility mode: structural-validation`；`mode meaning` 行说明结构守卫继续验证，行为替换补丁因未认证而安全跳过。
- 因游戏实际结构未变，`RunBasedValues` 等 `Structural` 补丁仍应验证；其余当前行为替换补丁预期为 `SkippedUncertifiedBuild`。**关键断言**：没有全局失败，也没有在未知构建上静默替换新版原方法。
- 测完改回原 GUID 并重建。

**方法 B（真实结构差异，需一个实际更新/热修过的 Assembly-CSharp）**：

- 预期：`structural-validation` 模式；当前 `Fingerprint`/`StrictIl` 补丁先因未认证而 `SkippedUncertifiedBuild`，结构级补丁继续验证；若未来提供方法级指纹，则只有结构校验（成员缺失/变形 → `SkippedStructureMismatch`）或 IL 匹配失败（`SkippedIlMismatch`）的补丁被单独跳过。
- 关键断言：被跳过的是**特定项**（如某个目标方法被改写的补丁），而不是整个模组；日志里每个跳过项都带明确的 `Reason`。

### 热重载自测

1. 进游戏后，在 ConfigurationManager 里把一个默认开启的 Prefix 补丁（例如 `Fixes.ItemCollisionMode`）关掉再打开。
2. 观察日志：应出现 `HotReload: ItemCollisionMode disabled→uninstalled` 与 `HotReload: ItemCollisionMode enabled→installed`。
3. 把另一个默认开启的 `Experimental.*` 补丁（例如 `Experimental.ExplosionScale`）关掉再打开，观察同格式两行。
4. **分组开关自测**：把 `Fixes.SnowballContactGuard` 关掉再打开。预期看到**两行**（`SnowballContactSnapshot` 与 `SnowballContactConsume` 各一行），因为一个开关管两个补丁且整组一起装卸。把 `Experimental.RemoteRagdollLod` 关掉再打开，预期看到**四行**。任何一次只出现一行都说明分组语义坏了。
5. **引擎标志时序自测**（需先手动开启 `Experimental.CollisionCallbackReuse`）：在它已生效的状态下把 `Fixes.SnowballContactGuard` 关掉。预期日志顺序是「先 `reuseCollisionCallbacks restored to False (patch set changing)`，再两行 `disabled→uninstalled`」——标志必须在守卫卸载**之前**落下。再把守卫打开，预期立刻（同一帧，不是下一帧）重新出现 `reuseCollisionCallbacks = true`。顺序颠倒就说明这个窗口没关住。
6. `Diagnostics.DetailedLogging` 改值后即时生效，无需重载或重启。配置里已没有任何数值项——所有阈值都是代码常量。

## 1. 立即停止的恶性判据

崩溃/卡死/无限加载/掉线循环；存档异常；场景切换/机场/结算/复活/重连异常；网络状态分叉（桥梁人数、营火范围、物品位置/尺寸长期不一致）；地面物品卡住/抖动/无法拾取/尺寸卡住；负重/荆棘/箭矢数值错误；打滑抓地突变；嘴部贴图卡在说话帧；名字牌长期不消失；日志高速增长或 `InvalidProgramException`。

回滚顺序：先关最近启用的单项，再逐项关闭默认开启项定位，最后无插件复现确认是否原版问题。

## 2. 第一层：默认配置安全性（默认开启的 38 项）

先单人后 1 / 2 / 4 人递增；通过条件是「与原版表现一致 + 无本插件异常」。

### 2.1 稳定性修复（8 项）

下表 8 行对应 8 个补丁。注意目录归属：其中 7 个实现在 `Patches\Stability`，`StatusEmitterScan`（A8）实现在 `Patches\Scale\StatusEmitterScanPatch.cs`——本节是按「稳定性修复」这个性质分的，不是按目录数的，别去数 `Patches\Stability` 里的文件数来核对。

| 步骤 | 补丁 | 内容 | 通过条件 |
|---|---|---|---|
| A1 | `RunBasedValues` | 保存/读取进度 | 无序列化空引用，进度正常 |
| A2 | `Parachute` | 手持/栏/背包取放降落伞 | 优先级正确、移除不残留、Scoutmaster 不变 |
| A3 | `RopeSegmentGuard` | 绳索攀爬 | 攀爬手感正常；`ArgumentOutOfRangeException` 不再出现 |
| A4 | `IKItemGuard` | 装备切换（左右手各持一件、快速来回切换） | 手部不跳变；无 `GetItemPosRight` NRE。诊断按**缺失的是哪一侧手部节点**分别记账，记录命中的是哪一侧 |
| A5 | `EmoteWheelGuard` | **1.2.0 重点回归项**：正常开合表情轮；在**已死亡/观战**状态下按住再松开表情键；结算画面（`stats.won`）下同样操作；重生、退出 | 表情轮开合正常，松手后必须**关闭**且光标消失、暂停菜单能打开（1.1.0 在「角色已死 + refs/stats 或 input 缺失」时会把这条关闭分支一起跳过，导致表情轮与光标卡死）。无 `canEmote` NRE。诊断五条 `localCharacter-` / `data-` / `refs-` / `stats-` / `input-unavailable`，记录命中哪一条——新逻辑下 `refs-`/`stats-` 只在角色**存活**时可达，`input-` 只在 `canEmote` 为真时可达 |
| A6 | `BinocularSunGuard` | 举起望远镜（机场、方山白天、方山夜间、其它图各一次） | 望远镜功能正常；日志不再出现 `TestLookAtSun` 的每帧 `NullReferenceException`；在方山白天举镜看太阳仍能拿到天文成就。机场没有 MapHandler，是 1544 次异常最可能的来源，应命中 `mapHandler-unavailable`。**非方山或夜间举镜时，`mainCamera-` / `center-` / `achievementManager-` 三条必须一次都不命中**（原版在那些条件下会提前 return，1.2.0 已把这三项检查移到复现 guard clause 之后，就是为了消除这种假阳性） |
| A7 | `ItemOptimizerDeregisterGuard` | 正常游戏，重点在物品密集生成/销毁时 | 日志不再出现 `ItemOptimizationManager.DeregisterItem` 的 `ArgumentNullException`；物品优化行为无变化 |
| A8 | `StatusEmitterScan` | 沼泽/丛林：走进孢子毒气范围、停在边缘来回、多个毒气区重叠处；Roots 风吹过毒气时（`WindAffectedStatusEmitter`）站在范围内；另外在刚重生/刚进图（ragdoll 尚未注册）时站在毒气里 | 中毒速率与原版一致（同一位置停 10 秒对比中毒条增量，**任何差异都说明等价论证有漏洞**）；进出范围时孢子警告全屏效果照常出现/消失；风吹停毒气时警告也随之消失；重叠区只有最近的那个发出警告；刚重生那一瞬不应出现 `Character.Center` 的 `KeyNotFoundException`（1.2.0 把它从「交回原版再抛一次」改成了真正消除，诊断 `center-unavailable` 可以命中但异常不应出现） |

### 2.2 等价改写与规模化（14 项，12 组）

下表 12 行、共 14 个补丁：B1 含 `Campfire` 与 `CampfireProtectionMerge` 两个，B7 含 `ZombieScanRange` 与 `ZombieSpawnScanRange` 两个，其余每行一个。**编号有跳号：没有 B2**，编号保持原样不重排，因为 `B13` 这类编号在别处（第 7 节验收优先级）被引用。

| 步骤 | 补丁 | 内容 | 通过条件 |
|---|---|---|---|
| B1 | `Campfire` + `CampfireProtectionMerge` | 营火范围/休息 | `_charactersInRadius` 内容正确；有人进出/死亡时表现不变；OrbFogHandler 写入一致 |
| B3 | `HeatEmissionScan` | 取暖 | 范围与速率一致 |
| B4 | `RagdollPhysicsMats` | 倒地/拖拽/冰面 | 摩擦切换正常，无打滑突变 |
| B5 | `ItemCollisionMode` | 拾取/丢弃/背包 | 不穿地、碰撞正常 |
| B6 | `GenericOptimizerRange` | 靠近/远离带优化器物（弓箭发射器 82m、食人花 60m 两个档位各走一次） | 远处脚本/动画启停距离与原版一致。注意范围**不是**代码里的 `+inf`：全库 2133 个实例都被序列化值覆写为 82m 或 60m，所以这是真实可见的门控距离，进出时机必须与原版逐次一致 |
| B7 | `ZombieScanRange` / `ZombieSpawnScanRange` | 蘑菇僵尸生成/禁用（主机） | 僵尸启停/生成时机与原版一致 |
| B8 | `SleepingZombieScan` | 蘑菇僵尸睡眠唤醒 | 唤醒距离/角度与原版一致 |
| B9 | `WeightRefreshCoalescing` | 连续拾取丢弃触发负重刷新 | 负重/荆棘/箭矢数值与特效次数与原版一致 |
| B10 | `ItemScaleRedundantWrite` | 拾取丢弃/背包/雪球 | 物品缩放不卡住、背包 0.5 倍正确、各客户端一致 |
| B11 | `RemoteItemInterpolationThreshold` | 大量掉落物静置/撞击/远处 | 远端物品不卡住/不抖动、被撞后立刻跟上 |
| B12 | `EyeLookComponentCache` | 多人围站/说话/死亡/重连 | 眼球朝向、说话者抢占、离开后不盯空位 |
| B13 | `ExplosionScale` | 单发引爆一次；随后 10 根炸药堆在 1 米内同时引爆并贴脸观察；另外分别用火箭背包与 GhostBall 各爆一次 | 单发：主炸药由 **10 球降为 5 球**，火球大小与原版完全一致（`baseScale` 不被改写）。堆爆：第 2 发起球体缩到 85%、第 4 发起缩到 60% 且只剩 2 球，火球整体轮廓仍明显大于单发，帧率相对本项关闭时有可感知改善。**屏幕震动会同比变弱**（每球两个叠加式 shake，20→10→4 次），确认这个代价可接受。火箭背包与 GhostBall 的爆炸**应完全不受影响**（它们的预制体不含 `ExplosionEffect`）——若观察到它们也变了，说明覆盖面判断错了。任一条不满足记录下来 |

### 2.3 等价规模化重点观测（8 项）

| 步骤 | 补丁 | 观察点 | 通过条件 |
|---|---|---|---|
| C1 | `ItemDatabaseNameLookup` | **负重数值** | 拾取/丢弃/背包存取后负重数值正确、无延迟或错误；反复操作无异常 |
| C2 | `IsLookedAtScan` | **名字牌** | 远端玩家名字牌显示/隐藏时机、可见角度与距离与原版一致 |
| C3 | `ItemAudioManagerHash` | **使用物品动画** | 吃/喝/解毒/使用物品时动画与音效正常，切换物品不卡动画 |
| C4 | `BarAfflictionLayout` | **状态条平滑** | 状态条宽度平滑收敛到目标值，无跳动/卡住 |
| C5 | `AnimatedMouthMaterial` | **嘴部动画** | 说话时嘴部贴图正常切换，无卡在说话帧 |
| C6 | `CollisionCharacterLookup` | **踩人/堆叠** | 踩在蹲下玩家身上仍能触发原版逻辑；普通地面与非 bodypart 子碰撞体行为不变 |
| C7 | `AnimatorValuesHash` | **移动动画** | 输入方向、接地、冲刺与垂直速度动画连续，无冲刺参数卡住 |
| C8 | `PlayerNameUiWrites` | **名字牌状态** | 位置每帧跟随；说话图标、静音、房主星、透明淡出、食人显示状态与原版一致 |

### 2.4 远端与细节物理实验（9 项，6 组）

下表 6 行、共 9 个补丁：D1 是 `Experimental.RemoteRagdollLod` 一个开关下的 4 个补丁，其余每行一个。**编号有跳号：没有 D2**，同样保持原编号不重排。

| 步骤 | 补丁 | 场景 | 通过条件 |
|---|---|---|---|
| D1 | `Experimental.RemoteRagdollLod`（一个开关，四个补丁） | 一名远端玩家从 40 米走到 70 米再返回并切换装备；远端玩家在 70 米外反复蹲下站起与冲刺；**本地角色死亡进入观战后，观察镜头附近的远端玩家** | 60 米外可降级，50 米内完整恢复；抓取、攀爬、死亡、观战始终完整运行；装备/卸装备首帧不使用陈旧姿态。远端蹲/冲刺切换时该角色应短暂恢复完整行为（1.2.0 新增：两个目标高度只由被跳过的那段写，冻结会让站立力与地面射线用错值）。**观战时镜头前的角色绝不应姿态凝固**（1.2.0 把距离基准从本地角色改成了观察对象） |
| D3 | `DetailBodypartThrottle` | 8 人近距聚集、移动、抓取、攀爬、倒地 | 本地角色不受影响；远端手指/jiggle/嘴颌轻微降帧可接受；关节不漂浮、不爆力、不出现 NaN |
| D4 | `PocketBehaviorGuard` | 新玩家加入 / 重连 | 注册窗口不再出现 `UpdatePocketBehaviors` 空引用刷屏；口袋物品（背包内被动效果）行为正常 |
| D5 | `BodypartDragIdentity` | 倒地堆叠、被风滚草撞倒、拖拽 | 阻尼观感与原版一致；手指自然下垂正常；无速度异常累积 |
| D6 | `LightVolumeSampleCache` | 多人聚集说话、进出隧道/开阔地、风区 | 语音混响随位置变化正常；环境混响与风区判定与原版一致；重烘焙/切段后无陈旧值 |
| D7 | `TumbleWeedTargetScan` | 方山风滚草过境（单人与多人） | 风滚草仍追人、撞击掉血与击倒正常；转向无明显迟钝；非房主端行为不变 |

### 2.5 聚集专项（5 个补丁 + 2 条非 Harmony 车道）

这一组必须在**多人**下看，单人不可能命中。

下表 6 行覆盖 5 个补丁加 2 条车道：E1 与 E2 是同一个补丁的两个场景（2 人与 8 人），E5 的 `SnowballContactGuard` 一个开关管 2 个补丁，E4 与 E6 各带一条非 Harmony 车道（`CollisionCallbackReuse`、`CapBoneWeights`），车道不计入 44 个补丁。

| 步骤 | 项 | 场景 | 通过条件 |
|---|---|---|---|
| E1 | `Experimental.RemoteClusterAnimationThrottle`（默认关闭，需手动开启） | 2 人近距互看：站立、走动、跳跃、蹲下、挥手表情、切换手持物品、抓取互拉、倒地 | 远端角色动画略微「一顿一顿」是预期（25Hz），但**动作速度必须与原版相同**（不能变慢也不能变快）；四肢不瘫软、不漂浮；持物手部位置正常；切换物品瞬间不出现姿态错乱。任一条不满足就把这一项关掉。 |
| E2 | 同上 | 8 人聚集 2 分钟，其中夹杂表情、抓取、装备切换、倒地 | 与 E1 相同判据；同时记录帧时间与该项关闭时的对比。fallback 命中的人（被抓、攀爬、观战中）应看不出任何差别。 |
| E3 | `Fixes.BodypartMovementForceIdentity`（默认开启） | 全员站立不动 → 同时开始移动 → 冲刺 → 停下 | 起步/停下手感与原版一致，无延迟感；站立时无异常抖动。 |
| E4 | `Fixes.CollisionContactsNoAlloc` + `Experimental.CollisionCallbackReuse`（后者默认关闭，需手动开启才测这一步） | 落地、踩冰面、踩弹床（蘑菇）、被仙人掌粘住、踩队友（Pal jump）、上桥踩断、火箭 | 全部触发效果与原版一致：弹床弹起、仙人掌扣血粘住、Pal jump 起跳、桥梁断裂、火箭爆炸。任一失效立刻关 `CollisionCallbackReuse` 复测，以区分是复用标志还是等价改写的问题。 |
| E5 | `Fixes.SnowballContactGuard`（一个开关，两个补丁） | 在雪地上滚一颗雪球两圈，再在石头/冰面上滚 | 雪球在雪地上变大、在非雪面不变大、可拾取上限行为与原版一致。这一步是 `CollisionCallbackReuse` 的专用验收项，**必做**。 |
| E6 | `Graphics.CapBoneWeights`（默认关闭，需手动开启） | 8 人聚集观察角色关节（肩、肘、髋）形变 | 日志出现一行 `bone weight cap: launch value was <你的启动值>`；开启后关节处形变会略微变糙（这是代价），关掉应在 2 秒内还原。 |

### 2.6 组合长时

4 人组合长时（覆盖以上全部）：无累积日志、无状态分叉、无 `failed` 项。

## 3. 第二层：痛点场景（默认配置，8 人以上 + 大量地面物品 + 爆炸）

### 3.1 大量掉落物

全员持续丢弃物品堆到明显数量；物品堆旁站立 1 分钟；同时快速拾取丢弃 30 秒；走远 50 米再走回。观察位置漂移/抖动/卡住/残留/尺寸异常。

### 3.2 集中拾取丢弃与背包

反复放背包再取出、交叉传递。观察负重/荆棘/箭矢数值、背包 0.5 倍尺寸。

### 3.3 营火集结与桥梁并发

全员进营火范围、有人走出、有人死亡；全员同时上桥制造断裂、断裂中重连。

### 3.4 场景切换与重连

推进地图段落、至少 1 人退出重连、房主迁移（若可行）。

### 3.5 爆炸卡顿（本次痛点）

全员同时投掷多枚炸药，记录主观卡顿与帧率（外部工具或主观记录，务必与无插件基准对比）。当前基础上限为 **2 / 1**：近距单发主炸药 **5 球**（原版 10）、Small/Fae 3 球（原版 4）；**2.5 秒**内、**一个火球半径**内累计到 **1** 个邻近爆炸即进轻档（只缩到 85%，球数不变，因为轻档上限恰等于基础上限）、累计到 **3** 个进重档（2 球 + 60% 尺寸，即中心球加一个环球）。判定半径按当发自己的 `baseScale` 推导（`baseScale × 6.0962`，主炸药约 **6.10 米**、Small/Fae 约 **3.05 米**），只有 `baseScale` 为 NaN/0/负/无穷时才回退到 3.5 米常量；50–120 米为 2 球，120 米外仅中心球。确认视觉与震动削弱可接受，并重点排查生成炸药时卡死；当前实现绝不调用 `Shader.WarmupAllShaders()`。注意：孔菌爆炸、间歇泉/闪光植物的 `Explosion`、气球、火箭、GhostBall 都不含 `ExplosionEffect`，本补丁对它们无效——如果卡顿在那些场景，不属于本项的验收范围。

### 3.6 八人近距聚集（核心对比）

在同一位置让 8 人以上持续移动、跳跃、蹲下、倒地并互相碰撞。保持场景和镜头近似一致，至少各记录 60 秒帧率/帧时间。四项 `RemoteRagdollLod*` 因 60 米距离门槛在该场景预期不触发，不得把其收益计入近距结果。

按下面顺序逐项单关，每次只关一个（其余保持默认），这样才能分辨收益来自哪条车道：

1. `Experimental.RemoteClusterAnimationThrottle`（预期这一项是聚集场景 CPU 收益最大的单项）
2. `Experimental.DetailBodypartThrottle`
3. `Fixes.BodypartMovementForceIdentity`
4. `Experimental.CollisionCallbackReuse`（GC 维度，看帧时间毛刺而不是平均帧率）
5. `Graphics.CapBoneWeights`（GPU 维度，每顶点骨骼数；默认关闭，需先手动开启才能测“关掉它”）

如果只有第 5 项关掉后帧率明显下降、前四项都无差别，那说明这个场景是 GPU bound，继续调 CPU 侧没有意义。反之则相反。这一步的判定力比任何单项收益都高。

### 3.7 人数阶梯

按 4 → 6 → 8 → 10 人递增复测 3.1、3.3、3.5，每档记录人数、地面物品数、主观流畅度、周期卡顿、日志新增。

### 3.8 方山（Mesa / Desert）专项对比（1.2.0 核心）

在方山同一位置、同镜头方向做 8 人聚集，每组至少 60 秒帧率/帧时间：

1. 全部默认（38 项开启）与本插件完全卸载的基线对比。
2. 仅关闭 `Experimental.TumbleWeedTargetScan`，在风滚草过境时对比。
3. 仅关闭 `Fixes.LightVolumeSampleCache`，在多人同时说话时对比。
4. 仅关闭 `Fixes.BodypartDragIdentity`，在全员倒地堆叠时对比。
5. 仅关闭 `Graphics.CapBoneWeights`（每顶点骨骼数；需先手动开启），同一位置同镜头对比。

方山主因在 GPU 侧。原先怀疑是 Desert `DayNightProfile` 的全屏效果，**已实测证伪**：`_GlobalHazeAmount` 运行时实际只有 0.016，几乎等于关闭，对应的热浪补丁已移除。当前判断指向 8 套角色的渲染成本（十几个 Renderer/人，其中 4-5 个是纯阴影重复网格，且游戏完全没有角色 LOD）。补丁能从代码侧碰的部分：MSAA（关掉还顺带恢复 STP）、每顶点骨骼数，以及待实现的近/中距角色 LOD。阴影距离/级联属于游戏自带设置，由你在菜单里调，补丁不再覆盖。

## 4. 第三层：逐项开关实验（每次只关/开一个）

第一、二层稳定后执行。先验证「默认开→关」回退是否消除某一异常，再验证「单独开」收益。每项测完恢复默认。

| 顺序 | 键 | 建议人数 | 场景 | 专用观察点 |
|---:|---|---:|---|---|
| 2 | `Fixes.HeatEmissionScan` | 2/4 | 取暖 | 范围/速率与原版一致 |
| 3 | `Fixes.RagdollPhysicsMats` | 2/4 | 倒地/拖拽/冰面 | 摩擦切换无突变 |
| 4 | `Fixes.CampfireProtectionMerge` | 4 | 营火范围 | `_charactersInRadius` 内容正确 |
| 5 | `Experimental.WeightRefreshCoalescing` | 2/4 | 连续拾取丢弃 | 负重/荆棘/箭矢数值与特效次数与原版一致 |
| 6 | `Experimental.ExplosionScale` | 4+ | 多人同时投掷炸药 | 降低球数后视觉可接受度、卡顿改善、无负面效果 |
| 7 | `Experimental.DetailBodypartThrottle` | 8+ | 全员近距聚集/碰撞 | 开关两组帧时间差；远端手指/jiggle 可接受度；无物理异常 |
| 8 | 四项 `Experimental.RemoteRagdollLod*` | 2+ | 50/60 米往返 | 远距收益与近距无收益均符合预期；交互时正确回退 |
| 9 | `Experimental.TumbleWeedTargetScan` | 1/8+ | 方山风滚草过境 | 追踪与撞击行为不变；扫描降频后帧时间差 |
| 10 | `Fixes.LightVolumeSampleCache` | 8+ | 多人说话 + 进出遮蔽 | 混响随位置变化正常；GetPixel 调用量下降 |
| 11 | `Fixes.BodypartDragIdentity` | 8+ | 全员倒地堆叠 | 阻尼一致；倒地场景帧时间差 |

## 5. 日志与配置收集清单

1. `LogOutput.log` 完整副本（房主与至少一名客户端）。
2. 对应 `com.peak.safeoptimizer.cfg`。
3. `Player.log`（LocalLow）与崩溃转储。
4. 游戏版本、MVID、人数、地图段落、地面物品数、复现步骤、时间点。
5. 本次测试启用的键列表（只列 `true`）。
6. 已安装其它模组清单。
7. 网络/渲染问题需房主与客户端同期日志。

检索关键词：`PeakSafeOptimizer`、`compatibility mode`、`MVID`、`applied`、`skipped`、`failed`、`SkippedStructureMismatch`、`SkippedIlMismatch`、`SkippedUncertifiedBuild`、`Harmony`、`InvalidProgramException`、`NullReferenceException`、`ArgumentOutOfRangeException`、`IndexOutOfRangeException`、`Repair reason`、`HotReload`、`verified uninstall`。

## 6. 结果记录模板

```text
日期 / 插件版本 / 游戏版本 / MVID：
兼容模式（certified / structural-validation）：
BepInEx 版本 / 其它模组数量：
人数与房主：
本次启用键（仅列 true）：
启动状态摘要（registered/enabled/applied/skipped/failed 数量与异常项及 Reason）：
退出时是否 verified uninstall 失败：
热重载自测结果（含分组开关与引擎标志时序两项）：
第一层结果（A1–A8、B1–B13 无 B2、C1–C8、D1–D7 无 D2、E1–E6、组合长时）：
第二层结果（掉落物数量、爆炸卡顿、人数阶梯、流畅度）：
第三层结果（本次实验项、专用观察点）：
无插件基准对比：
日志中本插件异常数 / 其它模组异常数：
结论：通过 / 停止 / 待复现
日志与配置文件：
备注：
```

## 7. 测试限制

- 单人/多人异常修复已实测；多人压力基准与长时稳定性未完成。
- 插件不提供帧率计数，流畅度请外部工具或主观记录并与无插件基准对比。
- 单项失败被隔离，报告写明具体项与原因。
- `DetailBodypartThrottle`、`CollisionCharacterLookup`、`AnimatorValuesHash`、`PlayerNameUiWrites` 尚未在游戏内验证；需按 C6–C8、D3 实测，若目标构建结构不符将各自 fail-closed（`SkippedStructureMismatch`）单独跳过。
- **1.2.0 的全部改动都未经游戏内验证。** 按验收优先级排列：
  1. **A5（表情轮）是最高优先级** —— 1.1.0 引入了一个真实的功能性回归（角色已死或已结算且 `refs.stats`/`input` 缺失时，守卫会把原版的「关闭表情轮」分支一起跳过，导致表情轮 UI、光标与暂停菜单卡住）。1.2.0 重写了守卫使其精确复刻原版的两处短路。必须实测死亡/观战/结算三种状态下的开合。
  2. **B13（爆炸）** —— 基础上限从 3/1 降到 2/1（主炸药 10→5 球），并新增了震动变弱这个代价声明。同时确认了火箭/背包/GhostBall 的爆炸不含 `ExplosionEffect`、完全不受本补丁影响。
  3. **D1/E1/E2（六项默认关闭）** —— 距离基准改为观察对象、crouch/sprint 纳入不降级、warmup 改按物理步、tick 号改双精度、相位改哈希、失败闩锁改按角色。开启前逐项走查，不要一次全开。
  4. **A6/A8（两个守卫与孢子）** —— 望远镜补了 `Center` 守卫且消除了诊断假阳性；孢子的 `EndFX` 次序改回原版同序、`Center` 异常改为真正消除。
  5. **热重载 4/5 两步（分组与引擎标志时序）** —— 分组改为原子安装、引擎标志改为在补丁变更前落下，这两条只能靠日志顺序验收。
- **`ParticleCountCullerWrite` 已在 1.2.0 删除**：用 UnityPy 解码全部 16 个 `.assets` 与 14 个 level 后确认，`ParticleCountCuller` 这个组件在原版资产里**零个实例**（MonoScript 存在只说明类被编进程序集）。该补丁的收益在原版恒等于零，故按「无实现/无收益即删除」的既有原则移除。因此补丁数从 45 降为 44、开关从 41 降为 40、默认开启从 39 降为 38。
- 卡死（Windows AppHangB1）排查：静态审计在这 6 项里没有发现任何无限循环、锁或跨线程等待，因此单靠代码结构无法解释主线程完全不响应。判定方法只有一个——把插件 DLL 移出 `plugins` 跑一局同样条件的对照。
- 以下事实由资产解码确认，可直接引用：`ExplosionEffect` 全库仅 3 个实例（三个 Dynamite 系预制体，主炸药 3/2 = 10 球、Small/Fae 3/0 = 4 球）；`StatusEmitter` 1571 个 + `WindAffectedStatusEmitter` 108 个，均无距离门控；`GenericOptimizer` 2133 个实例的 `rangeToDisableScripts` 全部被序列化值覆写为 82m 或 60m（不是代码默认的 `+inf`）；孢子云的 `Particles` 子物体挂了 `ParticleCuller`（`cullDistance = 100`，管 2 个粒子系统），但它只 `Play`/`Stop` 粒子、管不到 `StatusEmitter`，且 `ParticleManager` 每帧只扫 3 个 culler；游戏 100% 使用 `LoadSceneMode.Single`，地图段落切换是同场景 `Map` 子树的 `SetActive`。
- 以下项仍**未验证**，不要当已验证用：orb 的网格填充与两个粒子系统各占多少帧时间（需运行期 profiler）；`Phase` 输出的实际分布（需运行期直方图）；第三方模组是否做 additive 场景加载或添加 `ParticleCountCuller`（未反编译 18 个插件）；Mono 的委托 invoke stub 是间接跳转还是地址快照（两种情况下「委托命中 Harmony detour」的结论都成立）。
