# 舰船 AI v6：下一步方向决策

本文按原 v6 方向说明压缩整理，保留公式、分支和默认参数。本次仅修改文档，未重新核验源码；这里描述的是浏览器模拟器的简化实现。距离单位 px，速度 px/s，时间秒。

## 1. 决策链与输入

每帧流程：**敌舰分群 → 选目标与舰队纠偏 → 威胁/撤退状态 → 攻击距离与方位 → 多舰站位协调 → 切线/环绕/撤退 → 期望速度 → 速度约束 → 安全速度 → 船体执行**。站位协调必须先收集相关舰船的提议，再统一分配。

| 中间结果 | 含义 |
| --- | --- |
| `maneuverTarget` | 围绕哪艘敌舰机动 |
| `threatVector` | 加权敌军威胁的单位方向 |
| `attackRange` | 希望保持的目标中心距离 |
| `proposedAttackPosition` | 单舰提出的理想攻击位置 |
| `attackPosition` | 与友军协调后的攻击位置 |
| `destination / steeringPoint` | 导航终点 / 临时切点 |
| `expectedVelocity` | 尚未加入局部安全约束的期望速度 |
| `safeVelocity` | 求解器选出的速度目标；放宽约束后可能违反原始限制 |
| `velocity / facing` | 实际平移速度 / 舰首朝向，两者独立 |

非零速度的方向为 `atan2(v.y, v.x)`，大小为 `length(v)`；零向量没有有效移动方向。`safeVelocity` 经过加减速和侧移执行，不会瞬间成为实际速度。

| 输入来源 | 主要内容与用途 |
| --- | --- |
| 自身几何与运动 | `position, velocity, facing, angularVelocity, radius, mass, hullSize, dp` |
| 运动能力 | `maxSpeed, acceleration, deceleration, strafeAcceleration, maxTurnRate, turnAcceleration` |
| 状态与角色 | `fatigue, flux, backingOff, venting, personality, isSkirmisher, isAssault, orbitSign` |
| 敌军 | 位置、速度、DP、半径、舰级、有效武器射程、残骸状态；用于选目标、威胁和机动 |
| 友军 | 位置、速度、半径、质量、移动优先级、撤退状态；主要影响站位协调和避撞 |
| 环境/舰队 | 陨石、残骸、危险弹丸、边界；敌军分组、群 DP、主战群和有效群 |

## 2. Fleet Segmentation：敌舰分群

使用并查集按距离识别战斗群，连接距离默认 `targeting.groupRange = 245`。Frigate 不作为连接两个大型舰群的桥梁，避免快速小舰把远处的主力群串在一起。

群价值为 `groupDP = Σ ship.dp`；默认 Frigate / Destroyer / Cruiser / Capital 的 DP 为 `5 / 10 / 20 / 40`。按 DP 降序排列，满足“包含 Capital”或 `groupDP × 4 >= largestGroupDP` 的群为有效群，最大有效群为 Primary Group。分群不直接产生速度，而是约束后续目标。

## 3. Target Selection / Fleet Cohesion：确定机动目标

在 `targeting.searchRange = 900` 内评分并选择局部候选：

```text
rawExposure = 1 / (1 + friendlyCover × 0.5)
exposureFactor = 1 + rawExposure × 0.18
score = target.dp × exposureFactor / (distance² + 500)
若为上一帧目标：score *= targeting.stickiness  // 默认 1.35
```

`friendlyCover` 表示目标附近约 100 px 内的掩护友舰数量。暴露修正较弱，DP 和距离平方主导评分；旧目标获得 35% 保持奖励，减少频繁切换。

随后 `applyFleetCohesion()` 纠偏：

| 条件 | 处理 |
| --- | --- |
| 本舰为 Frigate、Skirmisher 或 Hulk | 保留候选 |
| 候选属于 Primary Group | 保留候选 |
| 候选属于其他有效群，且距离 `< secondaryKeepDistance`（默认 210） | 保留候选 |
| 其他情况 | 改选主战群中最近的非 Frigate；若仅有 Frigate，则选其中最近者 |

输出 `ship.maneuverTarget`。当前 Cohesion 通过改变目标维持主战方向，并非完整舰队命令系统。

## 4. Threat Vector：综合威胁方向

对距离 `dᵢ <= D` 的敌舰计算权重；默认 `D = threat.maxDistance = 500`，`p = threat.distancePower = 2`：

```text
uᵢ = normalize(enemyᵢ.position - ship.position)
wᵢ = (1 - (dᵢ / D)^p) × enemyᵢ.dp²
rawThreat = Σ(uᵢ × wᵢ)
threatVector = normalize(rawThreat)
```

范围外权重为零。默认 Frigate 的 DP² 为 25，Capital 为 1600，相同距离下后者影响为前者的 64 倍。`threatVector` 指向加权威胁合力，`-threatVector` 用于攻击侧和撤退方向。

反向合力只是启发式，不能保证该方向最安全；对称分布的敌军可能使向量抵消。原说明未列出零向量归一化的具体回退实现。

## 5. Fatigue / Backoff / Attack Range

疲劳限制在 `[0, 1]`：最近敌人距离 `< 150` 时以 `0.10/s` 增长，距离 `> 245` 时以 `0.065/s` 恢复。原说明未列出中间距离区间的额外变化规则。

以下任一条件触发 `backingOff`：`flux > 0.75`；`fatigue > 0.88`；或 `recentDamage > 0.12 && flux > 0.55`。Backoff 在机动阶段覆盖正常攻击位置；原方向说明未列出精确退出条件。

攻击距离为：

```text
R = effectiveWeaponRange × baseRangeScale × personalityRangeFactor
    + target.radius + fatigue × fatigueRangeBonus
    + (backingOff ? backoffRangeBonus : 0)
```

| 项 | 当前说明 |
| --- | --- |
| `effectiveWeaponRange` | 非 PD 武器最大射程，至少 100 |
| `baseRangeScale` | 默认 0.88 |
| `personalityRangeFactor` | AGGRESSIVE 0.92；STEADY 1.00；CAUTIOUS 1.12 |
| `fatigueRangeBonus` | 默认 135；疲劳 0.5 时增加 67.5 px |
| `backoffRangeBonus` | 仅撤退时加入；原说明未给默认值 |

疲劳先通过扩大 `R` 逐渐拉开距离，超过阈值后才切换撤退导航，因此“软撤退”可以早于 Backoff。

## 6. Preferred Attack Vector：选择攻击侧

设 `currentSide = normalize(ship.position - target.position)`。输出 `attackVector` 是**从目标指向理想站位**的单位方向。

| 分支条件 | 初始攻击方向 |
| --- | --- |
| `isAssault` | `currentSide`，从当前侧建立距离 |
| 无附近威胁，或目标不在 Threat 集内 | `currentSide` |
| 单威胁，且目标沿“本舰→目标”的速度分量 `> ship.maxSpeed × 0.7` | `currentSide`，优先追击 |
| 单威胁且不满足高速逃跑条件 | 将 `-threatVector` 按 `orbitSign` 旋转 ±90° |
| 多个威胁 | `-threatVector` |

`orbitSign` 在生成舰船时固定为 ±1，不逐帧随机切换。随后加入角色与边界修正：

```text
若 isSkirmisher：attackVector = normalize({x: attackVector.x, y: attackVector.y × 0.66})
centerDirection = normalize(mapCenter - ship.position)
nx = abs(ship.x - centerX) / halfWidth
ny = abs(ship.y - centerY) / halfHeight
w = max(nx², ny²) × 0.75
attackVector = normalize(attackVector + centerDirection × w)
proposedAttackPosition = target.position + attackVector × R
```

Skirmisher 是依赖地图坐标轴的简化角度偏置；Border Bias 随离中心距离增大而增强，只修正战术站位，后续仍有边界速度限制。

## 7. Attack Coordinator：分配攻击位置

收集所有提议后，按 `maneuverTarget.id` 分组。每舰根据攻击半径和 `ship.radius × 1.6` 计算所需角宽 `angularSize`；原说明未给出角宽函数的精确公式。

若两舰提议角度的最短角距 `< (angularSizeA + angularSizeB) / 2`，合并进同一 Formation。Formation 内按各舰当前实际角度排序，围绕群中心依次展开，最大偏转限制 ±130°，输出 `ship.attackPosition`。

该步骤减少站位重合，但不保证飞行路径无交叉，途中碰撞仍由速度约束处理。

## 8. Maneuver：撤退、切线接近和环绕

**撤退覆盖：** Backoff 时，有有效威胁方向则取 `away = -threatVector`，否则取 `normalize(ship.position - target.position)`；设 `destination = ship.position + away × 1000`，导航目标速度为零。正常攻击位置被覆盖，但后续避撞保留。

**远处接近：** 正常状态下 `destination = attackPosition`。当目标距离 `> max(target.effectiveWeaponRange, R × orbit.startFactor)`，且需避免直线切入攻击圆时，尝试切线导航；`orbit.startFactor` 默认 1.2。求圆的两个切点，选角度更接近最终攻击位置者作为 `steeringPoint`。本帧 `goal = steeringPoint || destination`。

**近处环绕：** 以目标为圆心计算径向与切向速度：

```text
radial = normalize(ship.position - target.position)
error = distanceToTarget - R
radialSpeed = clamp(-error × radialGain, -maxRadialSpeed, +maxRadialSpeed)
currentAngle = angle(ship.position - target.position)
targetAngle = angle(attackPosition - target.position)
side = sign(wrapToPi(targetAngle - currentAngle))  // 为零时使用 orbitSign
tangent = perpendicular(radial, side)
orbitVelocity = radial × radialSpeed + tangent × orbit.speed
goalVelocity = target.velocity × targetVelocityBlend + orbitVelocity
```

默认 `radialGain = 2.15`、`maxRadialSpeed = 62`、`orbit.speed = 45`、`targetVelocityBlend = 0.78`。太远时径向朝内，太近时朝外，距离合适时主要沿切线移动；切向方向由协调后的站位决定。远处主要跟随目标平移速度，近处再叠加环绕速度。

## 9. Expected Velocity：导航转换为期望速度

`headingExpectedVelocity()` 接收导航位置和目标速度，根据剩余距离、减速度、`dt` 估算可刹停的 `brakeSpeed`：

```text
heading = normalize(goal - ship.position)
speed = min(ship.maxSpeed, brakeSpeed + 8)
expectedVelocity = clampLength(heading × speed + goalVelocity, ship.maxSpeed)
```

接近导航点时推进速度下降，目标速度跟随和环绕仍可保留。输出尚未考虑局部安全限制。原说明未单独列出这里的制动函数体；下节给出的是 SpeedLimit 使用的离散制动关系。

## 10. Collision Avoidance：生成速度约束

每条限制为 `{direction, speedLimit, obstacle}`。令单位方向 `n = direction`、速度上限 `L = speedLimit`，候选速度必须满足 **`dot(v, n) <= L`**，对应速度空间的一个半平面。

### 制动关系

令 `d = distanceToObstacle - minimumAllowedDistance`，`b` 为该方向可用减速度：

```text
a = b × dt²
vMax = (sqrt(a² + 2a × d) - a) / dt
```

该式估算剩余间距内可刹住的接近速度。障碍在前方时减速能力接近 `deceleration`，侧面接近 `strafeAcceleration`，后方接近 `acceleration`，中间方向按角度插值。Capital 侧移弱，因此侧向接近障碍时更早受限。

原说明未展开负剩余距离保护、移动障碍相对速度到世界速度上限的转换和具体插值函数，不能将 `vMax` 直接当作所有障碍最终的 `L`。

### 各类限制

| 来源 | 条件与安全距离 | 特点 |
| --- | --- | --- |
| 友军 | `(ship.radius + ally.radius) × allySpacing`；默认 1.4 | 假设双方配合，可用减速度合并双方能力；半径均为 10 时留出 28 px |
| 机动目标 | `max(R × targetRangeFactor, ship.radius + target.radius)`；默认比例 0.85 | 避免高速切进目标 |
| 大型残骸 | 作为障碍参与 | 不假设对方避让，依靠自身制动 |
| 陨石 | 在 `maxConstraintRange` 内且质量 `>= ship.mass × 0.8`；距离为双方半径之和加 8 | 较大陨石影响路线 |
| 弹丸/导弹 | Frigate、Destroyer 或 Backoff 舰采用最危险的前 4 个威胁 | 形成局部闪避约束 |
| 地图边界 | `borderNoGo = 72`、`borderHardNoGo = 18` | 接近边界时向外速度上限逐渐趋零 |

Border Bias 改变站位意图，Border SpeedLimit 限制当前速度，二者分别作用于战术层和安全层。

## 11. Safe Velocity Solver：选择可行速度

若 `expectedVelocity` 满足全部限制，直接输出并设 `avoidingCollision = false`；否则标记避撞并构造候选：

| 候选 | 作用 |
| --- | --- |
| `(0, 0)` | 停车候选；当上限为负时也可能不合法 |
| 原期望速度 | 保留在集合中，仍须过滤 |
| 期望速度到单条限制边界的投影 | 减少危险方向分量，保留沿障碍的切向速度 |
| 限制边界与最大 Dodge Speed 圆的交点 | 提供高速左绕/右绕候选 |
| 两条限制边界的交点 | 提供同时受两个约束时的速度空间拐角 |

对通过全部约束的候选评分。候选与期望速度夹角为 `θ ∈ [0, π]`：

```text
alignment = 1 - θ / π
score = length(candidate) × alignment²
若候选与当前实际速度方向相差 > 60°：score *= 0.5
```

选择最高分候选，兼顾速度、期望方向和转向稳定性；这**不等于严格最小化与期望速度的欧氏距离**。转向惩罚缓和左右切换，但不保证绝对不抖动。零速度的夹角处理未在原说明中展开。

无可行候选时，统一放宽为 `dot(v, n) <= L + tolerance`，在容差 `[0, 600]` 内默认进行 8 次二分搜索，寻找能得到候选的较小容差；仍无解则回退零速度。放宽只解决当前搜索下的可行性，不保证满足原始限制，也不保证得到非零速度。

输出 `safeVelocity`。求解器只改本帧速度，不改战术站位；障碍消失后，舰船仍围绕原目标生成运动意图。

## 12. Engine Controller / Facing：执行方向

计算 `dv = safeVelocity - currentVelocity`，投影到由 `facing` 决定的前向轴、右向轴。前向正分量使用 `acceleration`，负分量使用 `deceleration`，横向使用 `strafeAcceleration`，按帧时间逐渐调整实际速度并限制最大速度。

| 舰级 | 默认侧移加速度 / 前进加速度 |
| --- | --- |
| Frigate | 1.00 |
| Destroyer | 0.75 |
| Cruiser | 0.50 |
| Capital | 0.25 |

同样的速度目标，小舰响应更快，大舰需要更长时间。例如从 `(60, 0)` 改为 `(0, 60)`，必须逐步减少横向速度并增加纵向速度。

Facing 独立计算：有 Attack Target 时，选择代表性非 PD 武器并求拦截点，FRONT 舰朝向拦截点，BROADSIDE 舰偏转 ±90°；无目标但有威胁时，原说明采用 `-threatVector`。再由 `maxTurnRate`、`turnAcceleration` 控制旋转。舰首影响武器朝向和推进能力，不等于位移方向。

下一帧实际位移取决于执行后的 `velocity`，还可能受碰撞和边界的物理修正影响；仅凭 `safeVelocity` 不能断言本帧无碰撞。

## 13. 典型行为、排错与参数

| 场景 | 关键决策 |
| --- | --- |
| 低疲劳 1v1 | 选择侧向站位，远处切线接近，近处径向修正与切向环绕 |
| 多敌压迫 | 非当前目标的敌舰也通过 Threat 改变攻击侧 |
| 疲劳升高 | 攻击圆扩大，先主动拉开，超过阈值再 Backoff |
| Backoff | 覆盖正常站位，远离威胁合力，同时继续避障 |
| 前方友军 | 限制朝友军的速度分量，允许斜向滑过 |
| 多方向拥堵 | 在半平面共同允许区选速度，无候选时尝试放宽 |
| 地图边缘 | 高层偏向中心，底层限制向外速度 |

排错应找出最早出现异常的中间结果：

| 异常结果 | 优先检查 |
| --- | --- |
| `maneuverTarget` | 分群、目标评分、Stickiness、Cohesion |
| `proposedAttackPosition` | Threat、攻击距离、角色/1v1 分支、Skirmisher、Border Bias |
| `attackPosition` | 同目标分组、角宽、Formation 合并、角度排序 |
| `steeringPoint / expectedVelocity` | 切点、径向误差、切向方向、目标速度跟随、制动 |
| `safeVelocity` | 各约束来源、方向、上限；候选过滤、评分、容差 |
| 实际 `velocity` | 当前惯性、Facing、加减速、侧移、物理修正 |

| 参数组 | 主要字段 |
| --- | --- |
| `targeting` | `searchRange, stickiness, groupRange, secondaryKeepDistance` |
| `threat` | `maxDistance, distancePower` |
| `combat` | `baseRangeScale, fatigueRangeBonus, backoffRangeBonus` |
| 角色 | `personality, isAssault, isSkirmisher, orbitSign` |
| `orbit` | `speed, radialGain, maxRadialSpeed, startFactor, targetVelocityBlend` |
| `collision` | `allySpacing, targetRangeFactor, maxConstraintRange, borderNoGo, borderHardNoGo, relaxationSteps` |
| 船体响应 | `maxSpeed, acceleration, deceleration, strafeAcceleration, maxTurnRate, turnAcceleration` |

## 14. 当前实现边界

| 部分 | 未完整覆盖的内容 |
| --- | --- |
| 舰队指挥 | Assignment、Waypoint、Escort、Avoid、Eliminate、Full Assault、Retreat Task；当前 Cohesion 主要是目标纠偏 |
| 机动 | Station module、复杂残骸遮挡、导航任务、撤退舰截击、复杂 Assault formation |
| 速度求解 | 原项目完整 RawBound、Bound、边界区间求交、Yield Direction、Movement Deconfliction |
| 朝向与推进 | 真实 Starsector engine commands、engine slot、完整转向加速度曲线 |

原方向说明未给出的无目标分支、Backoff 退出条件、角宽函数、零向量处理及部分约束细节，需查源码后补充，本文不将其写成已确认实现。
