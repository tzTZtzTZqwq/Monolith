# NSV 舰战 AI v3:战斗行为层规划

## 状态与范围

本文是 **规划稿,未排期、未实现**。v2(`docs/nsv_ship_ai_v2.md`)负责给敌舰装上「接近 / 缠斗 / 撤退」三态骨架;v3 讨论的是骨架之上的**战斗质量**——同样的三态,如何打得更像一个对手。

输入来源:参考 [Halke1986/starsector-ai-tweaks](https://github.com/Halke1986/starsector-ai-tweaks)(Starsector 舰船 AI 重写 mod,Kotlin,107 文件 ≈13k LOC),逐条对照我们的 `_Mono` 实现,筛出**可移植**、**已具备**、**不适用**三类。

本文不含代码改动。落地顺序见文末「实施分层」。

## 一、参考项目的组织方式

三层,每层的更新频率和数据所有权都不同:

```text
全局 per-frame 插件     AttackCoordinator / TargetTracker / ProjectileTracker
  ↓ 写入共享状态
每舰 CustomShipAI       薄协调层,只做状态机与仲裁,不做数学
  ↓ 下发目标与朝向
每武器 AutofireAI       弹道解算、开火时机
```

另有 `handles/` 一层把游戏 API 对象包一遍,使 AI 数学可以脱离游戏跑单测。

对我们有参考价值的是**中间层很薄**这一点:`CustomShipAI.kt` 自身几乎不含算法,算法在上层(全局信息聚合)和下层(单武器)。我们目前是反过来的——`ShipTargetingSystem` 一个类同时做目标平滑、武器扫描、弹道解算和开火。

## 二、逐条对照

### 2.1 弹道解算 — 我们已经不差

参考项目 `ballistics/Projectile.kt::intercept()` 用余弦定理解二次方程,并**按弹速归一化**,使方程解的是飞行*距离*而非时间;无解时 `approachesInfinity = 1e7f` 退化为沿目标速度方向瞄准,真正的「打不中」由 `closestHitRange()` 返回 `+∞` 表示。

我们的 `ShipTargetingSystem.FireWeapons`(`Content.Server/_Mono/NPC/HTN/ShipTargetingSystem.cs:94-173`)已经是**逐炮**解算,而且多做了一件参考项目没做的事:补偿炮塔因舰体自转产生的线速度。

```csharp
var centerToGunVec = gXform.LocalPosition - shipBody.LocalCenter;
var gunAngVel = new Vector2(-centerToGunVec.Y, centerToGunVec.X) * shipAngVel;
gunAngVel = shipXform.LocalRotation.RotateVec(gunAngVel);
leadBy = otherVel - ourVel - gunAngVel;
```

打不中的两种情形也已显式拒绝:横向分速度超过弹速(`tgVel.Length() > projVel`)、目标沿弹道方向跑得比弹快(`normVel` 同向且更长)。此外还处理了 hitscan 射程剔除、`TargetSeekingComponent` 制导弹、`TimedDespawnComponent` 寿命剔除。

**结论:弹道层不需要移植。** 唯一值得借的是参考项目的 accuracy 语义——它的 accuracy ∈ [1.0, 2.0] 通过**除以相对速度**施加,且武器在同一目标上停留越久越准。我们的 `leadingAccuracy` 是对目标速度做指数平滑:

```csharp
var leadBy = 1f - MathF.Pow(1f - leadingAccuracy, frameTime);
comp.CurrentLeadingVelocity = Vector2.Lerp(comp.CurrentLeadingVelocity, targetVel, leadBy);
```

这其实**已经隐含了「盯得越久越准」**(平滑收敛),只是语义上是「速度估计滞后」而非「瞄准误差」,且是**全舰共享一个** `CurrentLeadingVelocity`。区别在换目标时:我们全舰一起重新收敛,参考项目是每门炮独立收敛。这个差异在 v3 的逐炮改造中会自然消解,不需要单独立项。

### 2.2 火力分配 — **最大的缺口**

参考项目:`AttackCoordinator` 全局分配、`SyncFire.kt` 错开齐射、`WeaponGroup.kt::shipAttackFacing` 选朝向。

我们:`FireWeapons` 把**全部火炮指向同一个 `mapTarget`**。`ShipTargetingComponent.Target` 是单个 `EntityCoordinates`,黑板也只传一个 `TargetCoordinates`。

这是三个独立的缺口,分述于第三节。

### 2.3 射界与朝向 — 我们的形态不同,但缺口真实

参考项目每门武器有明确的 `arc`,`shipAttackFacing` 把各武器射界切成互不重叠的子扇区,选**使可开火 DPS 最大**的舰体朝向。

我们**没有射界这个概念**。`FireControlSystem.CanFireInDirection`(`:619`)直接转发给 `HasLineOfSight`(`:555`)——一条针对*同 grid* 的 `Opaque | Impassable` 射线。也就是说,我们的「射界」不是配置出来的,是**由自己的船体几何涌现出来的**:炮塔被自家墙壁挡住的方向就打不出去。`FireControllableComponent.IgnoreLos` 可逐炮豁免。

这反而更好——射界不用配,是真实几何。但 AI 完全没利用它:

- 缠斗态的 `targetRotation: 90`(侧舷对敌)是**写死的猜测**,与该舰实际炮位无关。一艘炮全在艏部的船按 90° 侧舷转,等于主动把炮遮住。
- 现有 `AttemptFire` 里 LOS 失败只是**静默返回 false**,浪费一个 tick,AI 得不到任何反馈,也不会调整姿态。

**关键发现:所需原语已经存在且是 public。**

```csharp
// FireControlSystem.cs:632
public Dictionary<float, bool> CheckAllDirections(EntityUid weapon, float maxDistance = 500f, int rayCount = 256)
```

返回「该炮各角度是否通畅」。256 条射线显然不能每帧跑,但**船体几何几乎不变**,按炮缓存即可(重建时机:锚定变化 / 船体损毁 / 建造)。把各炮的通畅扇区按 DPS 加权叠加,就得到「舰体转到哪个朝向能打出最多火力」——这正是 `shipAttackFacing` 的等价物,而且不需要动 `_Mono` 一行。

### 2.4 威胁评估 — 完全缺失

参考项目有四套彼此独立的评分,这个「不要用一个分数干四件事」的拆分本身就值得抄:

| 用途 | 评分 |
| --- | --- |
| 机动目标 | `(exposedArc²) / (dist²)` |
| 攻击目标 | 加法式:迟滞 `+1`、友军已锁 `+1`、过载 `+2`、指定歼灭 `+16`、被遮挡 `-16` |
| 威胁向量 | 势场 `weight = max(R²−d², 0) / R²` |
| 来袭伤害 | `WeaponThreat.kt` 估算 |

我们的 `NsvBluespacePatrolTargets` 只有 `TargetInverseDistanceCon` + `TargetIsAliveOrNACon`——纯距离 + 存活。没有威胁、没有价值、没有迟滞,所以会出现「两个目标等距时每次重规划都换目标」的抖动。

其中**威胁向量势场**是唯一能直接驱动走位的:`-threatVector` 给出「往哪撤」的连续方向,比 v2 的「撤退 = GoToRange 1500」聪明得多——后者在被两艘船夹住时会径直撤向第二艘。

### 2.5 机动与避障 — **我们比参考项目强,不要移植**

必须记下这条,否则后人会照抄回退:

- 参考项目**没有寻路,没有 RVO**。`CollisionAvoidance.kt` 在*速度空间*工作,把障碍表达为半平面速度上限。
- 参考项目**基本没有弹幕规避**。

我们的 `ShipSteeringSystem` 有扇区式避障(`EvasionSectorCount = 24`、`EvasionSectorDepth = 2`)、按威胁加权(`GridThreat`、`EmpThreat`),并且**有弹幕规避**(`AvoidProjectiles`、`ProjectileSearchBounds = 896`)。

唯一值得借的是 `orbitTarget()` 把 **`destination`(去哪)与 `steeringPoint`(朝哪开)分离**,朝向再由 `shipAttackFacing` 独立决定。我们的 `ShipSteererComponent` 是半分离状态:`Mode: Orbit/OrbitCW` + `OrbitOffset` 已经把轨道点和目标点分开了,但**朝向仍被耦合**成三选一的常量(`AlwaysFaceTarget` / `InRangeRotation` / 相对运动方向的 `TargetRotation`)。补上 2.3 的朝向计算,这条就完成了。

### 2.6 不适用

Starsector 的 flux(能量槽)、护盾朝向、相位跃迁、hullmod、导弹弹药管理在我们这里没有对应机制。参考项目中大量评分项(`overloaded +2` 等)建立在 flux 之上,直接抄会得到无意义的常数。

## 三、实施分层

按 **价值 / 成本** 排序,不是按重要性。每层可独立落地、独立回滚。

### 层 A:射界感知朝向(建议先做)

**为什么排第一:** 所需 `_Mono` API 全部 public 且已存在,不需要新的黑板通道,单目标即可工作,直接修掉「`targetRotation: 90` 是盲猜」这个确定的错误。

新增 `Content.Server/_NSV/NPC/NsvShipArcSystem.cs` + `NsvShipArcComponent`:

1. 对本舰每门 `FireControllable` 炮调 `CheckAllDirections`,缓存通畅扇区;失效时机为锚定变化 / 船体结构变更 / 武器列表刷新(复用 `WeaponCheckSpacing = 3f` 的节奏,不要每帧)。
2. 按各炮 DPS 加权叠加成舰体朝向评分函数。
3. 结果写入 `ShipSteererComponent.InRangeRotation` 或 `TargetRotation`,替代 YAML 常量。

**风险:** 256 射线 × N 炮的重建开销。先用 `rayCount` 降采样(32 足够选朝向),实测再调。

### 层 B:逐炮目标选择

**为什么排第二:** 价值最高,但需要把 NSV→Mono 的数据通道从「一个 `TargetCoordinates`」拓宽成目标列表,是本文最大的一次改动。

架构决策:**火控分叉到 `_NSV`,操舵继续复用 `_Mono`。**

- 操舵(`ShipSteeringSystem`)质量好且含我们独有的弹幕规避,只喂更好的输入,不改。
- 火控的逐炮决策**无法**通过「全炮共享一个坐标」的接口表达,必须自己持有循环。

新增 `NsvShipGunnerySystem`,与 `ShipTargetingSystem` 并列(挂哪个组件就用哪套,互斥):沿用 `FireWeapons` 已验证的弹道数学,但每门炮独立选目标,经 public 的 `FireControlSystem.AttemptFire(weapon, user, coords, comp, noServer: true)`(`:476`)下发。逐炮 accuracy 收敛(2.1)在这里自然实现。

### 层 C:威胁势场驱动走位

`weight = max(R²−d², 0) / R²` 累加所有敌对 grid,得到威胁向量。v2 的撤退分支不再是「远离当前目标 1500」,而是「沿 `-threatVector` 撤离」。需要 v2 三态先落地。

### 层 D:目标评分与错开齐射

- 评分迟滞(参考项目的 `+1`)修掉等距抖动;补刀(低血量加权)、价值(火控台/驾驶台优先)对应 v2 已列的「层 2」。
- `SyncFire` 错开:`stagger = combinedCycleDuration / weapons`,且写回 `lastAttack = opportunity` 而非 `now` 以防漂移。我们的 `FireControllableComponent.NextFire` / `FireCooldown`(`:21` / `:27`)是 public 可写字段,但**更干净的做法是在层 B 的 NSV 循环里门控**——不到时隙就不调 `AttemptFire`,完全不碰 `_Mono` 状态。
- 注意 `FireCooldown = 0.2f` 只是火控层节流,真实循环时间来自 `GunComponent` 射速,计算 stagger 要用后者。

## 四、与 v2 的关系

v2 三态是 v3 的**前置**:层 C 直接替换 v2 撤退分支的走位,层 D 的评分挂在 v2 已有的 `NsvBluespacePatrolTargets` 上。层 A、B 不依赖 v2,可以并行。

建议顺序:**v2 三态 → 层 A → 层 B → 层 C/D**。层 A 提前也无妨,它只改朝向来源。

## 五、待验证假设

以下写入本文时未实测,落地前需确认:

- `CheckAllDirections` 在 `rayCount = 32` 下是否仍足以区分朝向优劣;
- 炮塔缓存的失效时机是否覆盖了所有船体几何变更路径(建造/损毁/解锚);
- 逐炮目标选择后,`GunComponent` 的散布与 `_Mono` 火控节流叠加,实际 DPS 是否符合预期;
- 势场半径 R 取多少能让撤退在测试扇区尺度下有意义。

## 相关文档

- 三态状态机与残废信号:`docs/nsv_ship_ai_v2.md`
- Mono 舰船 HTN 结构与执行层:`docs/feature_monoshiphtnai.md`
- 扇区、faction 与 GUST 生成:`docs/nsv_tech_design.md`
- 路线图与里程碑:`docs/nsv_game_loop_design.md`
