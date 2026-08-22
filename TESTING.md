# PeakSafeOptimizer 0.1.0 集中测试指引

本文是一次集中测试的执行清单，目标是在一轮联机里同时验证**默认开启的 35 项等价/修复类补丁**的安全性，并针对「方山 8 人聚集锐减」「8 人以上卡顿」「地面物品过多卡顿」「爆炸卡顿」四个痛点取得可比较的观察结果。

截至当前版本，多人压力基准与长时稳定性测试**尚未完成**；以下均为待执行计划。

## 0. 测试前准备

1. 确认 Assembly-CSharp MVID 为 `3f24c13b-87b6-4c9f-9c21-3c49b6ad17e1`（`certified` 模式）；如需验证 `structural-validation` 模式，见「MVID 不匹配验证」。
2. 备份 `BepInEx/config/com.peak.safeoptimizer.cfg`。更新 DLL 时不要删除 cfg；插件本身也不会删除它。布尔补丁开关支持运行时热重载，但关键开关（尤其 `BreakableBridge` 这个 Transpiler）仍建议以「改 cfg 后完全退出重启」为最稳妥路径。
3. 第一层用出厂默认。41 个补丁 / 37 个开关（`RemoteRagdollLod` 与 `SnowballContactGuard` 各是一个开关管多个补丁），默认开启 35 个补丁（全部为行为等价或修复原版故障），6 个有观感代价的默认关闭。默认开启项为：

   `RunBasedValues`、`Parachute`、`RopeSegmentGuard`、`IKItemGuard`、`EmoteWheelGuard`、`Campfire`、`BreakableBridge`、`HeatEmissionScan`、`RagdollPhysicsMats`、`ItemCollisionMode`、`GenericOptimizerRange`、`CampfireProtectionMerge`、`ZombieScanRange`、`ZombieSpawnScanRange`、`WeightRefreshCoalescing`、`SleepingZombieScan`、`ItemScaleRedundantWrite`、`RemoteItemInterpolationThreshold`、`EyeLookComponentCache`、`ExplosionScale`、`ItemDatabaseNameLookup`、`IsLookedAtScan`、`ItemAudioManagerHash`、`BarAfflictionLayout`、`AnimatedMouthMaterial`、`RemoteRagdollLodPhysics`、`RemoteRagdollLodAnimate`、`RemoteRagdollLodMovementForce`、`RemoteRagdollLodAnimator`、`JointProjection`、`DetailBodypartThrottle`、`CollisionCharacterLookup`、`AnimatorValuesHash`、`PlayerNameUiWrites`、`PocketBehaviorGuard`、`BodypartDragIdentity`、`LightVolumeSampleCache`、`TumbleWeedTargetScan`、`RemoteClusterAnimationThrottle`、`BodypartMovementForceIdentity`、`CollisionContactsNoAlloc`、`SnowballContactSnapshot`、`SnowballContactConsume`，另有非 Harmony 车道 `Experimental.CollisionCallbackReuse` 与 `Graphics.*`。

4. 准备一次不装插件的同场景基准（同人数/地点/操作序列）才能判断收益。

### 启动日志检查

启动日志逐行均带 `PeakSafeOptimizer |` 前缀，用该关键字过滤可整块取出报告。

- 版本 `0.1.0`；输出一行 Assembly-CSharp 指纹：`MVID=... expected=... current=... SHA256=...`。
- 兼容模式行：`compatibility mode: certified`（MVID 匹配）或 `compatibility mode: structural-validation`（MVID 不匹配）。结构级补丁仍验证；当前 `Fingerprint`/`StrictIl` 补丁在未知 MVID 上安全跳过。
- 汇总行 `totals:`：出厂默认且 MVID 匹配、全部通过时预期 `registered=41, enabled=35, applied=35, skipped=6, failed=0, module-failures=0`（6 行 `SkippedDisabled` 是默认关闭的观感代价项：4 项 `RemoteRagdollLod*` + `DetailBodypartThrottle` + `RemoteClusterAnimationThrottle`）。
  - 没有只显示不生效的键。`registered - enabled` 应等于默认关闭的 6 项。注意 `registered` 数的是补丁而不是开关：`Experimental.RemoteRagdollLod` 一个开关对应 4 个补丁，`Fixes.SnowballContactGuard` 一个开关对应 2 个。
- 逐项结果三类行：
  - `applied  : <名称> [<兼容等级>] <目标方法>`（35 行，兼容等级为 Structural/Fingerprint/StrictIl）。
  - `failed   : ...`（预期 0 行）。
- 任何 `Failed*`（`FailedInitialize`/`FailedPatch` 等）都要记录对应项与 `Reason`；`SkippedStructureMismatch`/`SkippedIlMismatch` 表明该项结构校验拒绝当前构建，应记录具体项。
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
6. 值类型配置（`Diagnostics.DetailedLogging`、爆炸集群/距离参数、远端 LOD 距离、`DetailBodypartThrottleInterval`）改值后即时生效，无需热重载动作。

## 1. 立即停止的恶性判据

崩溃/卡死/无限加载/掉线循环；存档异常；场景切换/机场/结算/复活/重连异常；网络状态分叉（桥梁人数、营火范围、物品位置/尺寸长期不一致）；地面物品卡住/抖动/无法拾取/尺寸卡住；负重/荆棘/箭矢数值错误；打滑抓地突变；嘴部贴图卡在说话帧；名字牌长期不消失；日志高速增长或 `InvalidProgramException`。

回滚顺序：先关最近启用的单项，再逐项关闭默认开启项定位，最后无插件复现确认是否原版问题。

## 2. 第一层：默认配置安全性（默认开启的 35 项）

先单人后 1 / 2 / 4 人递增；通过条件是「与原版表现一致 + 无本插件异常」。

### 2.1 稳定性修复（5 项）

| 步骤 | 补丁 | 内容 | 通过条件 |
|---|---|---|---|
| A1 | `RunBasedValues` | 保存/读取进度 | 无序列化空引用，进度正常 |
| A2 | `Parachute` | 手持/栏/背包取放降落伞 | 优先级正确、移除不残留、Scoutmaster 不变 |
| A3 | `RopeSegmentGuard` | 绳索攀爬 | 攀爬手感正常；`ArgumentOutOfRangeException` 不再出现 |
| A4 | `IKItemGuard` | 装备切换 | 手部不跳变；无 `GetItemPosRight` NRE |
| A5 | `EmoteWheelGuard` | 表情轮/观战/重生/退出 | 表情轮正常；无 `canEmote` NRE；记录命中的是 localCharacter 还是 data/refs/stats 不完整诊断 |

### 2.2 等价改写与规模化（15 项，13 组）

| 步骤 | 补丁 | 内容 | 通过条件 |
|---|---|---|---|
| B1 | `Campfire` + `CampfireProtectionMerge` | 营火范围/休息 | `_charactersInRadius` 内容正确；有人进出/死亡时表现不变；OrbFogHandler 写入一致 |
| B3 | `HeatEmissionScan` | 取暖 | 范围与速率一致 |
| B4 | `RagdollPhysicsMats` | 倒地/拖拽/冰面 | 摩擦切换正常，无打滑突变 |
| B5 | `ItemCollisionMode` | 拾取/丢弃/背包 | 不穿地、碰撞正常 |
| B6 | `GenericOptimizerRange` | 靠近/远离带优化器物 | 远处脚本/动画启停与原版一致（默认无穷范围预期无可见差异） |
| B7 | `ZombieScanRange` / `ZombieSpawnScanRange` | 蘑菇僵尸生成/禁用（主机） | 僵尸启停/生成时机与原版一致 |
| B8 | `SleepingZombieScan` | 蘑菇僵尸睡眠唤醒 | 唤醒距离/角度与原版一致 |
| B9 | `WeightRefreshCoalescing` | 连续拾取丢弃触发负重刷新 | 负重/荆棘/箭矢数值与特效次数与原版一致 |
| B10 | `ItemScaleRedundantWrite` | 拾取丢弃/背包/雪球 | 物品缩放不卡住、背包 0.5 倍正确、各客户端一致 |
| B11 | `RemoteItemInterpolationThreshold` | 大量掉落物静置/撞击/远处 | 远端物品不卡住/不抖动、被撞后立刻跟上 |
| B12 | `EyeLookComponentCache` | 多人围站/说话/死亡/重连 | 眼球朝向、说话者抢占、离开后不盯空位 |
| B13 | `ExplosionScale` | 多人同时投掷炸药 | 默认近距单发由 13 球降为 7 球，集群继续自动降级；视觉可接受且无卡死 |

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

### 2.4 远端与细节物理实验（6 项）

| 步骤 | 补丁 | 场景 | 通过条件 |
|---|---|---|---|
| D1 | `Experimental.RemoteRagdollLod`（一个开关，四个补丁） | 一名远端玩家从 40 米走到 70 米再返回并切换装备 | 60 米外可降级，50 米内完整恢复；抓取、攀爬、死亡、观战始终完整运行；装备/卸装备首帧不使用陈旧姿态 |
| D3 | `DetailBodypartThrottle` | 8 人近距聚集、移动、抓取、攀爬、倒地 | 本地角色不受影响；远端手指/jiggle/嘴颌轻微降帧可接受；关节不漂浮、不爆力、不出现 NaN |
| D4 | `PocketBehaviorGuard` | 新玩家加入 / 重连 | 注册窗口不再出现 `UpdatePocketBehaviors` 空引用刷屏；口袋物品（背包内被动效果）行为正常 |
| D5 | `BodypartDragIdentity` | 倒地堆叠、被风滚草撞倒、拖拽 | 阻尼观感与原版一致；手指自然下垂正常；无速度异常累积 |
| D6 | `LightVolumeSampleCache` | 多人聚集说话、进出隧道/开阔地、风区 | 语音混响随位置变化正常；环境混响与风区判定与原版一致；重烘焙/切段后无陈旧值 |
| D7 | `TumbleWeedTargetScan` | 方山风滚草过境（单人与多人） | 风滚草仍追人、撞击掉血与击倒正常；转向无明显迟钝；非房主端行为不变 |

### 2.6 聚集专项（本轮新增 5 项 + 2 条非 Harmony 车道）

这一组必须在**多人**下看，单人不可能命中。

| 步骤 | 项 | 场景 | 通过条件 |
|---|---|---|---|
| E1 | `Experimental.RemoteClusterAnimationThrottle`（默认关闭，需手动开启） | 2 人近距互看：站立、走动、跳跃、蹲下、挥手表情、切换手持物品、抓取互拉、倒地 | 远端角色动画略微「一顿一顿」是预期（25Hz），但**动作速度必须与原版相同**（不能变慢也不能变快）；四肢不瘫软、不漂浮；持物手部位置正常；切换物品瞬间不出现姿态错乱。任一条不满足就把 `RemoteClusterAnimationInterval` 设回 `1`（等于关闭）而不是继续调大。 |
| E2 | 同上 | 8 人聚集 2 分钟，其中夹杂表情、抓取、装备切换、倒地 | 与 E1 相同判据；同时记录帧时间与该项关闭时的对比。fallback 命中的人（被抓、攀爬、观战中）应看不出任何差别。 |
| E3 | `Fixes.BodypartMovementForceIdentity`（默认开启） | 全员站立不动 → 同时开始移动 → 冲刺 → 停下 | 起步/停下手感与原版一致，无延迟感；站立时无异常抖动。 |
| E4 | `Fixes.CollisionContactsNoAlloc` + `Experimental.CollisionCallbackReuse`（后者默认关闭，需手动开启才测这一步） | 落地、踩冰面、踩弹床（蘑菇）、被仙人掌粘住、踩队友（Pal jump）、上桥踩断、火箭 | 全部触发效果与原版一致：弹床弹起、仙人掌扣血粘住、Pal jump 起跳、桥梁断裂、火箭爆炸。任一失效立刻关 `CollisionCallbackReuse` 复测，以区分是复用标志还是等价改写的问题。 |
| E5 | `Fixes.SnowballContactGuard`（一个开关，两个补丁） | 在雪地上滚一颗雪球两圈，再在石头/冰面上滚 | 雪球在雪地上变大、在非雪面不变大、可拾取上限行为与原版一致。这一步是 `CollisionCallbackReuse` 的专用验收项，**必做**。 |
| E6 | `Graphics.GraphicsSkinWeightCap`（默认 0 = 不限制，需手动设为 `2` 才测这一步） | 8 人聚集观察角色关节（肩、肘、髋）形变 | 日志出现一行 `graphics budget snapshot: skinWeights=<你的启动值>`；设为 `2` 后关节处形变会略微变糙（这是代价），改回 `0` 或把 `GraphicsBudgetEnabled` 设 `false` 应立刻还原。 |

### 2.5 组合长时

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

全员同时投掷多枚炸药，记录主观卡顿与帧率（外部工具或主观记录，务必与无插件基准对比）。默认基础上限为 3 / 1：近距单发 7 球；1.2 秒、3.5 米内累计到 2 / 5 个邻近爆炸后自动降到 5 / 2 球；50–120 米为 2 球，120 米外仅中心球。确认视觉可接受，并重点排查生成炸药时卡死；当前实现绝不调用 `Shader.WarmupAllShaders()`。

### 3.6 八人近距聚集（核心对比）

在同一位置让 8 人以上持续移动、跳跃、蹲下、倒地并互相碰撞。保持场景和镜头近似一致，至少各记录 60 秒帧率/帧时间。四项 `RemoteRagdollLod*` 因 60 米距离门槛在该场景预期不触发，不得把其收益计入近距结果。

按下面顺序逐项单关，每次只关一个（其余保持默认），这样才能分辨收益来自哪条车道：

1. `Experimental.RemoteClusterAnimationThrottle`（预期这一项是聚集场景 CPU 收益最大的单项）
2. `Experimental.DetailBodypartThrottle`
3. `Fixes.BodypartMovementForceIdentity`
4. `Experimental.CollisionCallbackReuse`（GC 维度，看帧时间毛刺而不是平均帧率）
5. `Graphics.GraphicsBudgetEnabled`（GPU 维度，MSAA 与每顶点骨骼数）

如果只有第 5 项关掉后帧率明显下降、前四项都无差别，那说明这个场景是 GPU bound，继续调 CPU 侧没有意义。反之则相反。这一步的判定力比任何单项收益都高。

### 3.7 人数阶梯

按 4 → 6 → 8 → 10 人递增复测 3.1、3.3、3.5，每档记录人数、地面物品数、主观流畅度、周期卡顿、日志新增。

### 3.8 方山（Mesa / Desert）专项对比（本轮核心）

在方山同一位置、同镜头方向做 8 人聚集，每组至少 60 秒帧率/帧时间：

1. 全部默认（35 项开启）与本插件完全卸载的基线对比。
2. 仅关闭 `Experimental.TumbleWeedTargetScan`，在风滚草过境时对比。
3. 仅关闭 `Fixes.LightVolumeSampleCache`，在多人同时说话时对比。
4. 仅关闭 `Fixes.BodypartDragIdentity`，在全员倒地堆叠时对比。
5. 仅关闭 `Graphics.GraphicsBudgetEnabled`（MSAA 与每顶点骨骼数），同一位置同镜头对比。

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
5. 本轮启用键列表（只列 `true`）。
6. 已安装其它模组清单。
7. 网络/渲染问题需房主与客户端同期日志。

检索关键词：`PeakSafeOptimizer`、`compatibility mode`、`MVID`、`applied`、`skipped`、`failed`、`SkippedStructureMismatch`、`SkippedIlMismatch`、`SkippedUncertifiedBuild`、`Harmony`、`InvalidProgramException`、`NullReferenceException`、`ArgumentOutOfRangeException`、`IndexOutOfRangeException`、`Repair reason`、`HotReload`、`verified uninstall`。

## 6. 结果记录模板

```text
日期 / 插件版本 / 游戏版本 / MVID：
兼容模式（certified / structural-validation）：
BepInEx 版本 / 其它模组数量：
人数与房主：
本轮启用键（仅列 true）：
启动状态摘要（registered/enabled/applied/skipped/failed 数量与异常项及 Reason）：
退出时是否 verified uninstall 失败：
热重载自测结果（含 Transpiler）：
第一层结果（A1–A6、B1–B13、C1–C8、D1–D3、组合长时）：
第二层结果（掉落物数量、爆炸卡顿、人数阶梯、流畅度）：
第三层结果（本轮实验项、专用观察点）：
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
- 本轮新增的 5 项补丁与 2 条非 Harmony 车道（见 2.6）同样尚未完成游戏内验证。
