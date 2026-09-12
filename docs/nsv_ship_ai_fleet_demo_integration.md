# fleet-ai-demo → NSV Ship AI 接入计划

依据 `docs/fleet-ai-demo.md`(JS 浏览器模拟器 v6 AI 的方向说明)与当前 NSV 独立 AI 的实现状态,逐层映射哪些层已有等价物、哪些值得移植、哪些明确不搬,以及接入顺序。

当前 NSV 实现基线:`NsvShipAiSystem` + `NsvShipAiComponent`(决策)+ `ShipSteeringSystem` / `ShipTargetingSystem`(执行,`_Mono` 共享)。已验证:旋转 PID 控制(自旋测试)、AI 喂航点模式(保距测试)、Tier 1 战术参数数据驱动。

JS 模拟器的距离单位是 px、速度 px/s,所有数值搬入 NSV 前必须按我们的尺度重标定(参考:当前交战距离 750m、搜索范围 4000m)。

---

## 1. 逐层映射

JS v6 决策链:**分群 → 选目标 → 威胁向量 → 疲劳/撤退 → 攻击距离与侧位 → 多舰站位协调 → 切线/环绕导航 → 期望速度 → 半平面速度约束 → 安全速度求解 → 船体执行**。

| # | JS v6 层 | NSV 现状 | 处置 | 评注 |
| --- | --- | --- | --- | --- |
| 1 | 敌舰分群(并查集,groupRange 245) | ❌ 无 | **跳过** | 需要多敌多友场景才有意义,单船/少量船阶段收益为零 |
| 2 | 目标评分(`dp × 暴露 / (距离²+500)` + stickiness 1.35) | 🔄 部分 | **P2 移植** | `Decide()` 已有最近敌 + stickiness 1.35(数值巧合相同);dp 可映射为 `DamageableComponent.TotalDamage` 反比或 `StaticPrice`;暴露修正需数友舰,可后置 |
| 3 | Fleet Cohesion 纠偏 | ❌ 无 | **跳过** | 依赖分群,同 #1 |
| 4 | **threatVector**(`wᵢ = (1-(d/D)^p) × dp²` 加权合力) | 🔄 机制已验证 | **P1 移植** | 保距测试(`KeepDistanceFromGrids`)就是它的退化版(斥力源=grid);换成敌舰加权即得撤退方向与侧位输入 |
| 5 | **疲劳/软撤退**(疲劳先扩大 R,过阈值才 Backoff) | ❌ 无 | **P1 移植** | 设计核心:渐进拉距离而非二值切换;输入用护盾 `Damage/DamageLimit` + hull(`DamageableComponent.TotalDamage`)替代 JS 的 fatigue/flux |
| 6 | 攻击距离公式(`射程 × baseRangeScale × 性格系数 + 半径 + 疲劳加成 + 撤退加成`) | ❌ 无(`EngageRange` 固定值) | **P1 移植** | 射程可从 grid 上 `FireControllableComponent` + `GunComponent` 读出(hitscan 读 `HitscanBasicRaycastComponent.MaxDistance`,抛射物=弹速×存活时间);消灭固定 750 |
| 7 | 攻击侧位选择(`-threatVector` 旋转 ±90°,orbitSign 固定) | ❌ 无 | **P2 移植** | 纯航点计算,路 A 直接可做;orbitSign 生成时固定 ±1 防抖是关键设计点 |
| 8 | 多舰站位协调(Formation 合并,±130° 展开) | ❌ 无 | **跳过** | 前提是多 AI 核心同场协同,后置到 fleet 阶段 |
| 9 | 切线接近 + 环绕(径向/切向速度分解) | ✅ 部分 | **P2 增强** | steering 的 `Orbit`/`OrbitCW` 模式已是环绕(机制等价);切线接近(远处先切圆再入轨)无;环绕方向目前由原型固定,可升级为由侧位选择动态决定 |
| 10 | 期望速度 → 速度约束(半平面)→ 安全速度求解器 | ✅ **Mono 更强** | **不搬** | 明确结论:Mono steering 的 24×2 扇区避障 + 弹道感知躲弹优于 JS 的半平面求解器,保持复用 |
| 11 | Engine Controller(侧移加速度分级) | ✅ 物理引擎管 | **不搬** | 不适用 |
| 12 | facing 独立计算(FRONT/BROADSIDE 偏转) | ✅ 已有 | — | `FacingCoordinates`(移动/朝向解耦)+ `TargetRotation`(0=正面 / 90=broadside)完全同构;无目标时朝 `-threatVector` 可作为 P1 补充 |

### 明确不搬清单(及理由)

- **分群 + Cohesion + 站位协调**:全部依赖"多船协同"前提,当前单核心/少量核心阶段无收益。
- **速度求解器(§10-11)**:Mono 的实现更优,移植是倒退。
- **Engine 执行层(§12)**:与真实物理引擎冲突。
- **flux 相关**:NSV 无此系统,由护盾/hull 承担其角色。

---

## 2. NSV 侧已有能力清单(接入的积木)

| 积木 | 状态 | 在接入中的作用 |
| --- | --- | --- |
| `NsvShipAiSystem` 决策循环(0.3s 决策 + 每帧跟踪) | ✅ | 所有层的宿主 |
| `ShipSteererComponent` 战术参数全部 DataField 化 | ✅ 刚完成 | Tier 1:新战术=纯 YAML |
| `FacingCoordinates` 朝向解耦 | ✅ | 撤退时边退边打、broadside |
| 自旋测试(`TestSpinSpeed`) | ✅ 已验证 | 旋转 PID 可用的证据 |
| 保距测试(`TestKeepDistance`) | ✅ 已验证 | **threatVector 的原型**:合成斥力向量 → 航点 → `Steer()` |
| `NsvAiKeys` + `NPCBlackboard` 键值存储 | ✅ 刚接好 | 状态机 scratch 状态、跨决策记忆 |
| 护盾 stress 读取(`ShipShieldEmitterComponent`) | 设计已确认,未接 | 软撤退的输入 |
| 武器射程读取(`FireControllableComponent` + Gun) | 设计已确认,未接 | 攻击距离公式的输入 |
| hull 读取(`DamageableComponent.TotalDamage`) | 未接 | 软撤退输入 |
| faction(`NsvBluespaceFactionSystem.IsHostile`) | ✅ 在用 | threat 集合的过滤条件 |

---

## 3. 接入计划

### P1 — 感知层 + 软撤退(对应 JS §4/§5/§6,一个状态机的最小可用版)

目标:AI 会"看"自己状态,恶化时渐进拉距离,崩溃时撤退。

1. **感知 API**(NsvShipAiSystem 内,private + 3s 缓存):
   - `GetShieldStress(grid)`:扫 `ShipShieldEmitterComponent`,取 `Max(Damage/DamageLimit)`,0=满盾→1=将过载;`Recharging` 单列
   - `GetHullFraction(grid)`:`DamageableComponent.TotalDamage` 归一化
   - `GetWeaponRange(grid)`:扫 `FireControllableComponent`,hitscan 读 `MaxDistance`,抛射物=弹速×存活时间,返回最短/最长/代表值
2. **软撤退状态**(组件加运行时字段,或用 blackboard + `NsvAiKeys`):
   - `stress = max(shieldStress, 1-hullFraction)` 之类合成
   - 交战距离动态化:`engageRange = weaponRange × RangeScale + stress × StressRangeBonus`(JS §5 公式的直译,性格系数后置)
   - 阈值以上切 withdraw:复用保距测试的合成向量逻辑,斥力源换成 threat 集合(`wᵢ=(1-(d/D)^p)×权`),`FacingCoordinates` 仍指目标(边退边打)
3. **weaponRange 集成**:替换 `EngageRange` 默认值的来源(保留 DataField 显式覆盖能力)
4. 验证:admin 实战场景,手动打盾/打船,观察渐进拉距与撤退

### P2 — 目标评分升级 + 攻击侧位(对应 JS §2/§6/§9)

1. **评分函数**:`score = dp × 暴露 /(距离²+偏置)`,dp 用 hull 反比或 StaticPrice;stickiness 已就位
2. **侧位选择**:`attackVector = -threatVector` 旋转 orbitSign×90°;orbitSign 存组件(生成时定,不逐帧换)
3. **航点合成**:每帧航点 = 目标位置 + attackVector × 动态 engageRange;GoToRange 的 Range=0 变体
4. **切线接近**(可选):远距时先切攻击圆的切点,近距入轨——评估 steering 现有避障是否已足够,够就不做
5. **环绕方向动态化**:`Orbit`/`OrbitCW` 由侧位选择结果决定,替代原型写死

### P3 — 多船协同(对应 JS §1/§3/§7/§8)

触发条件:同 map 多个 NSV AI 核心成为常态。届时再引入分群、Cohesion、站位角宽分配。**在此之前不写任何 fleet 代码。**

---

## 4. 每步的验证方式

- 感知 API:临时 Examine/日志输出数值,admin 实测
- 软撤退:editor 给自己船造伤/断盾,观察 AI 距离变化曲线(渐进无跳变)
- 目标评分:摆多目标场景,核对选择符合公式预期
- 全程:沿用测试 core 模式(spin/keepdistance 的变体)做最小验证,通过后删或保留为回归工具

## 5. 风险与注意

- **数值重标定**:JS 全部 px 量纲,直接抄必错;先定"我们的 1v1 标准场景"再标定 baseRangeScale、stressRangeBonus、threat.maxDistance
- **敌方全知**:护盾/hull 读取对敌 grid 同样无限制,AI 读敌方 stress 属作弊读数;P1 限读自己 grid,敌方状态用命中情况推断(公平性优先)
- **性能**:感知全部 3s 缓存,威胁向量每帧只算向量合成(已是 O(敌舰数) 量级)
- **`_Mono` 边界**:本计划不动 `_Mono` 的 steering/targeting(除已合入的 FacingCoordinates);需要新运动原语时优先路 A(AI 喂航点),路 B(新 Mode)需单独立项
