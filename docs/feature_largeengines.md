# 2×4 等离子-氧气大型发动机实现与 3×5 后续设计

## 目标与状态

大型燃烧推进器按以下范围实现：

| 名称 | 占地 | 角色 | 状态 |
| --- | ---: | --- | --- |
| Large Combustion Thruster | 2×4 | 中大型舰主推进器 | 已实现 |
| Capital Combustion Thruster | 3×5 | 主力舰主推进器 | 后续设计，尚未实现 |

本文约定尺寸为“宽 × 长”，长度方向沿推进轴；发动机尾部朝向真空并产生尾焰。

发动机只有同时满足以下条件才提供推力：

1. 已锚定在 shuttle grid；
2. APC 有电；
3. 玩家或 DeviceLink 没有关闭发动机；
4. 全部喷口朝向外部空间；
5. 等离子进气与氧气进气均已连接并有足够燃料；
6. 正在收到对应方向的推进请求时才消耗燃气。

首版不模拟真实燃烧温度、爆炸或废气，只按固定比例从管网原子扣除 Plasma 与 Oxygen。燃烧副产物、排气管和过热可作为后续升级。

## 当前机制

### 现有推进器

普通 1×1 推进器：

- 原型：`Resources/Prototypes/Entities/Structures/Shuttles/thrusters.yml`；
- 默认 `Thrust = BaseThrust = 200`：`Content.Server/Shuttles/Components/ThrusterComponent.cs:27-32`；
- APC 负载 `1500`：`thrusters.yml:40-41`。

现有 2×2 `ThrusterLarge`：

- 原型：`Resources/Prototypes/_Mono/Entities/Structures/Shuttles/capital_thrusters.yml:1-90`；
- 推力 `800`，APC 负载 `10000`；
- fixture 密度 `1750`；
- 4500 调整后伤害触发摧毁；
- 2×2 sprite，offset `0.5,-0.5`；
- 支持 `On/Off/Toggle` DeviceLink；
- 已锚定时不可旋转；
- `ShipRepairable` 为 15 秒、15 维修点。

当前关系：

```text
普通推进器：200 thrust/tile，7.5 power/thrust
2×2 推进器：200 thrust/tile，12.5 power/thrust
```

因此 `ThrusterLarge` 等于四台普通推进器的推力，但耗电更高。

### 可复用的推进系统

`Content.Server/Shuttles/Systems/ThrusterSystem.cs` 已处理：

- 锚定、解锚和换 grid；
- APC 供电变化；
- shuttle 方向与推力汇总；
- 电容升级；
- DeviceLink 开关；
- 尾焰视觉、碰撞区和伤害；
- 旋转；
- 组件删除后的推力清理。

2×4 发动机继续使用现有 `ThrusterComponent` 和 `ThrusterSystem`，没有另写推进物理；实现只增加尺寸、数值、燃气约束和多格喷口验证。3×5 后续实现也应沿用同一架构。

### 电容升级

`ThrusterSystem.cs:644-655` 使用：

```text
Thrust = BaseThrust × 1.25^(平均电容等级 - 1)
```

| 电容等级 | 倍率 |
| ---: | ---: |
| T1 | 1.0 |
| T2 | 1.25 |
| T3 | 1.5625 |

现有通用升级公式使用可配置的 `PartRatingThrustMultiplier`。2×4 燃烧推进器将该值设为 `1.118034`，使本项目中 rating 3 的 `AdvancedCapacitorStockPart` 将 2400 推力提升至 3000；燃气消耗按当前 `Thrust / BaseThrust` 同比增加，APC 负载保持 18000。

## 推荐定位与初始数值

若只按现有 2×2 推进器线性放大：

- 2×4 = 两台 2×2：1600 thrust、20000 power；
- 3×5 = 3.75 台 2×2：3000 thrust、37500 power。

但新发动机还需要两路燃气与管道维护。如果推力和电力完全等同于多个 2×2，它会严格劣于电推进器，没有安装价值。

推荐以燃料、管路、体积和单点故障换取更高的推力密度：

| 项目 | 现有 2×2 | 推荐 2×4 | 推荐 3×5 |
| --- | ---: | ---: | ---: |
| 占地 | 4 | 8 | 15 |
| BaseThrust | 800 | **2400** | **4500** |
| 推力/格 | 200 | **300** | **300** |
| APC powerLoad | 10000 | **18000** | **33750** |
| T2 推力 | 1000 | 3000 | 5625 |
| T3 推力 | 1250 | 3750 | 7031.25 |
| Plasma/s（T1 暂定） | — | **1.5 mol/s** | **2.8125 mol/s** |
| Oxygen/s（T1 暂定） | — | **2.1 mol/s** | **3.9375 mol/s** |

依据：

- 大发动机为 300 thrust/tile，比现有推进器高 50%；
- APC 约为 7.5 power/thrust，回到普通推进器的电力效率；
- 两种新发动机保持相同的单位面积推力和单位推力燃料消耗；
- 额外收益由燃气消耗、管网、巨大尺寸和单点故障抵偿。

这些仅是首轮 playtest 基线。保守方案可改为 1600/3000 thrust 和 20000/37500 power，但此时应提供其他优势，例如更高装甲、更低维护量或更好的升级效率。

## 燃气方案

### 推荐：两路直接管网进气

每台发动机拥有两个独立 `PipeNode`：

```text
Plasma pipe ── plasma intake ┐
                              ├─ combustion thruster
Oxygen pipe ── oxygen intake ┘
```

推荐组件：

- `NodeContainerComponent`；
- 两个命名为 `plasma`、`oxygen` 的 `PipeNode`；
- `AtmosDeviceComponent`，设置 `requireAnchored: true`、`joinSystem: true`。

`joinSystem: true` 很重要：普通 AtmosDevice 默认依赖 grid atmosphere，而 shuttle 在真空中仍必须从管网取气。该设置使其在没有 grid atmosphere 时也按 Atmos 间隔收到 `AtmosDeviceUpdateEvent`。

参考实现：

- `Content.Server/Power/Generator/GasPowerReceiverSystem.cs:21-79`；
- `Content.Server/NodeContainer/EntitySystems/NodeContainerSystem.cs`；
- `Content.Server/NodeContainer/Nodes/PipeNode.cs`；
- `Content.Server/Atmos/Piping/Components/AtmosDeviceComponent.cs:19-27`；
- `Resources/Prototypes/Entities/Structures/Piping/Atmospherics/binary.yml`。

玩家可直接使用现有管道、泵、过滤器、canister 和 connector port 供气，无需首版新增专用燃料罐 UI。

### 多格机器的管口限制

`PipeNode` 的连接位置取实体 Transform 所在锚定格，不能直接声明“位于实体第 N 个占用格”的偏移管口。因此首版应把实体原点设计为控制/进气格，并在该格的两个不同方向设置 Plasma 和 Oxygen 接口。

若美术要求接口位于 2×4/3×5 机器的其他格，需要独立子实体管口或扩展 NodeContainer 的偏移节点能力，范围会明显扩大，不建议首版采用。

### 消耗比例与公式

现有 Plasma 火焰反应定义 `Atmospherics.OxygenBurnRateBase = 1.4`，因此首版采用固定比例：

```text
1 mol Plasma : 1.4 mol Oxygen
```

不直接调用 `PlasmaFireReaction`，因为它会根据温度改变耗氧量并产生热量、CO₂/Tritium 等副作用，不适合作为稳定推进燃料模型。

每个 Atmos 更新：

```text
plasmaRequired = PlasmaMolesPerSecond × dt × (Thrust / BaseThrust)
oxygenRequired = plasmaRequired × 1.4
```

因此 T2/T3 电容提升推力时，燃气消耗同比提升。

### 原子扣除

每次更新必须：

1. 重新解析两个 `PipeNode`，不要跨 tick 缓存 `PipeNet`；
2. 先读取两种气体的可用 mol；
3. 同时确认两者都 `>= required`；
4. 只有两者都足够才分别调用 `AdjustMoles`；
5. 任意一种不足时两者都不扣，并停止发动机推力。

```text
if plasma >= plasmaRequired AND oxygen >= oxygenRequired:
    plasma -= plasmaRequired
    oxygen -= oxygenRequired
else:
    consume nothing
    mark starved
    remove thrust
```

两个管口可能最终接入同一个混合 PipeNet，因此必须先检查两种气体，再做任何扣除，不能先扣 Plasma 后发现 Oxygen 不足。

首版只检查 mol，不设置最低管压。最低压力会增加玩家诊断和小型测试管网的复杂度，可在燃料机制稳定后增加。

### 何时消耗

`ThrusterComponent` 区分：

- `Enabled`：玩家是否允许发动机工作；
- `IsOn`：发动机是否注册为可用推进器；
- `Firing`：当前方向是否正在实际喷射。

只有以下条件全部满足时消耗燃气：

```text
Enabled && IsOn && Firing && anchored && APC powered
```

发动机空闲或当前驾驶输入不需要该方向时不耗气。APC 继续按现有机制在发动机启用期间承担负载，首版不改为“仅喷射时耗电”。

### 缺燃料与自动恢复

不能用 `Enabled = false` 表示缺燃料，因为该字段代表玩家/DeviceLink 的持久开关。燃料恢复后不应要求玩家再次手动打开。

建议新增状态：

```text
Ready
Disconnected
NoPlasma
NoOxygen
NoFuel
```

缺燃料时：

- 保留 `Enabled = true`；
- 调用现有 `ThrusterSystem.DisableThruster()` 移除 shuttle 推力；
- 设置 starvation 状态并关闭可用/喷射视觉；
- Atmos 更新继续检查燃料；
- 燃料恢复后执行完整 enable 验证并自动重新注册推力。

## 已实现代码架构

### 燃烧组件

2×4 发动机使用 server-only 组件：

```text
Content.Server/_NSV/Shuttles/Components/NsvCombustionThrusterComponent.cs
```

组件保存 Plasma/Oxygen node 名称、基础 Plasma mol/s、Oxygen/Plasma 比例、单次处理上限、恢复所需最小燃料时长和 starvation 状态，不缓存 `PipeNode` 或 `PipeNet`。

### 燃烧系统

实现位于：

```text
Content.Server/_NSV/Shuttles/NsvCombustionThrusterSystem.cs
```

系统订阅 `AtmosDeviceUpdateEvent`、`ThrusterEnableAttemptEvent`、`AnchorStateChangedEvent`、`ExaminedEvent` 和 `GasAnalyzerScanEvent`。Atmos tick 负责重新解析两路网络、原子扣气和 starvation 状态转换；通用 `ThrusterSystem` 继续权威处理供电、锚定、旋转、DeviceLink、升级和删除清理。

无需每帧 Update。Atmos tick 提供稳定 `dt`，并将单次燃料计算限制在最多 1 秒。

### ThrusterSystem 通用 hook

通用推进系统已增加 public `TryEnableThruster(...)`，统一执行 player intent、生命周期、锚定、shuttle grid、APC、全部喷口和本地 veto 事件检查。`NsvCombustionThrusterSystem` 通过 by-ref `ThrusterEnableAttemptEvent` 拒绝无燃料的启用请求，燃料恢复后也重新走同一入口。

初始化、重新供电、重新锚定、旋转、DeviceLink 打开、零件刷新、喷口解除堵塞和燃料恢复因此共享完整验证路径。临时故障只改变实际 `IsOn`，不会覆盖玩家的 `Enabled` 意图。

## 多格 fixture、sprite 与原点

fixture、sprite offset、burnShape、雷达 bounds、管口和喷口检查必须共享同一个本地坐标原点。

沿用 2×2 大推进器的 0.05 格碰撞留边，初始建议：

| 占地 | 发动机 fixture bounds | Sprite 尺寸 | Sprite offset |
| --- | --- | ---: | --- |
| 2×4 | `-0.45,-3.45,1.45,0.45` | 128×128 | `0.5,-1.5` |
| 3×5 | `-0.45,-4.45,2.45,0.45` | 96×160 | `1,-2` |

这些数值假设默认方向下原点位于最前方一排左侧控制格，机器向负 Y 延伸。最终必须配合 RSI 在地图编辑器中验证四种旋转。

新 RSI 继续提供现有视觉层：

```text
base
thrust
thrust_burn_unshaded
```

每个 state 包含四个方向，即可复用现有客户端 `ThrusterVisuals`。缺燃料灯可后续通过 Appearance 增加；首版也可只用 Examine 文本和尾焰关闭反馈。

### 尾焰区域

每种发动机需要独立的宽尾焰 `burnShape`。初始 Heat damage 可按现有 2×2 的面积线性估算：

| 发动机 | Heat damage / 默认 2 秒触发 |
| --- | ---: |
| 现有 2×2 | 120 |
| 2×4 | 240 |
| 3×5 | 450 |

注意组件注释称其为每秒伤害，但当前实现按默认 `FireCooldown = 2 秒` 每次应用完整 DamageSpecifier，调参应以实际两秒一跳为准。

## 多格喷口空间检查

`ThrusterComponent.NozzleOffsets` 现在支持多个本地整格偏移，默认仍为单喷口 `(0,1)`，因此现有推进器行为不变。2×4 发动机配置：

```text
(0,1)
(1,1)
```

启用时系统按实体旋转将每个 offset 转换为 grid tile，要求两个喷口外侧全部为空间；任一喷口堵塞即停止整台发动机。tile 状态双向变化时，系统从已登记 offset 的四向旋转反推出有限数量候选原点，并对对应推进器重新执行完整验证，不扫描整个 grid。

3×5 后续实现时可复用该机制并配置三个喷口 offset。

## 新质量系统的影响

`NsvGridMassSystem` 会自动读取已锚定发动机的 `FixturesMass`，再按 `/2000` 折算进 grid 质量；新发动机无需额外接入质量系统。

若沿用 density `1750` 和上述 fixture：

| 发动机 | Fixture 面积 | FixturesMass | 计入 grid 的额外质量 |
| --- | ---: | ---: | ---: |
| 2×4 | 1.9×3.9 = 7.41 | 12967.5 | **6.48** |
| 3×5 | 2.9×4.9 = 14.21 | 24867.5 | **12.43** |

对照地板：

- 2×4 下方 8 格地板基础质量为 4.0，发动机额外 +6.48；
- 3×5 下方 15 格地板基础质量为 7.5，发动机额外 +12.43。

现有尾焰 collision fixture 使用默认 density 1，理论上也会进入推进器 `FixturesMass`。相对 density 1750 的主体很小，但长期可让质量系统按 fixture ID 排除 `thruster-burn`，确保视觉/伤害触发区完全不影响质量。

## 建造系统

### 当前状态

2×4 已有独立 `Machine2x4` construction graph，包含：

- `UnfinishedMachineFrame2x4`；
- `MachineFrame2x4`；
- `MachineFrameDestroyed2x4`；
- 玩家 construction recipe；
- `box_0`、`box_1`、`box_2`、`destroyed` frame RSI；
- construction icon RSI；
- `NsvCombustionThruster2x4MachineCircuitboard`；
- circuitboard lathe recipe 和 advanced shuttle components pack 接入；
- 正确尺寸的 construction、deconstruction 和 destruction 转换。

`MachineFrameSystem` 严格要求：

```text
MachineBoard.FrameSize == MachineFrame.FrameSize
```

因此 2×4 board 只能插入 `MachineFrame2x4`。3×5 仍没有 graph、frame、board、recipe 或资源，后续实现时必须建立独立 `Machine3x5` 链。

### 建造成本基线

现有大推进器板为 4 capacitor、20 steel、4 electromagnet、4 armor plate，frame 约为 5 steel/tile。面积线性基线：

| 项目 | 2×4 | 3×5 |
| --- | ---: | ---: |
| Frame steel | 40 | 75 |
| Capacitor | 8 | 15 |
| Board steel | 40 | 75 |
| Electromagnet | 8 | 15 |
| Armor plate | 8 | 15 |

该成本很高。可让 frame 承担主要钢材，board 只收电子、磁体和装甲零件，避免 frame 与 board 重复收取同一结构成本。

## 耐久、维修与失败模式

按现有 2×2 的 4500 damage、15 秒维修、15 点维修线性放大：

| 项目 | 2×4 | 3×5 |
| --- | ---: | ---: |
| 线性摧毁阈值 | 9000 | 16875 |
| 线性维修时间 | 30 s | 56.25 s |
| 线性维修消耗 | 30 | 56.25 |

大型发动机本身是推进单点故障，过高 HP 会削弱工程岗位。建议首轮使用较低值：

- 2×4：7500 damage，30 秒/30 点维修；
- 3×5：12000 damage，50 秒/50 点维修。

### DamagedThrusters modifier

`DamagedThrusters` 已为 2×4 主原型和 T2 原型增加独立的 25% 替换规则，命中时生成 `MachineFrameDestroyed2x4`。通用 Thruster→1×1 规则通过 `NsvCombustionThruster` 组件 blacklist 排除该发动机，避免专用规则 chance 失败后继续落入 1×1 fallback。

3×5 后续实现时也必须增加对应尺寸的专用残骸规则。

## 服务器权威与反馈

所有燃料判断和 mol 修改必须在服务器完成：

- `NodeContainerComponent.Nodes` 是 server-only；
- PipeNet mixture 不由客户端修改；
- 客户端不能提供可信 gas ID、燃料数量或运行许可；
- thrust enable/disable 由服务器统一判断 power、anchor、nozzle 和 fuel。

当前 2×4 反馈：

- Examine 显示具体供气状态以及当前 Plasma/Oxygen mol/s；
- 只有实际 `Firing` 才显示尾焰；
- 缺气时关闭实际推进与尾焰，但保留玩家启用意图；
- Gas Analyzer 分别显示 Plasma intake 和 Oxygen intake 的 node-local mixture clone。

未来 BUI 只需低频同步摘要：APC、enabled、firing、两路压力/mol、starvation reason 和预计续航；不要每帧复制完整 `GasMixture`。

## 性能预计

- 无需逐帧扫描整个 grid；
- 使用 Atmos tick，仅处理带燃烧组件的发动机；
- 每次每台发动机约 2 次 node lookup、2 次 mol 读取和 2 次 mol 调整；
- 仅状态变化时调用 `EnableThruster`/`DisableThruster`，避免反复创建/删除 burn fixture和刷新音效、center of thrust；
- 不要每 tick `GasMixture.Clone()`；
- 不缓存 PipeNet，因为管道拆除、旋转和重新锚定会重建网络。

主要风险不是气体运算，而是燃料临界点反复 enable/disable。需使用明确状态转换，并可要求恢复量至少覆盖一个完整 Atmos tick，避免抖动。

## 边界情况

| 场景 | 预期行为 |
| --- | --- |
| Plasma 不足 | 不扣 Oxygen，立即停止 thrust |
| Oxygen 不足 | 不扣 Plasma，立即停止 thrust |
| 两路都不足 | 两者都不扣，状态为 NoFuel |
| 数量恰好够一个 tick | 使用 `>=`，允许消耗到 0 |
| 两个节点连接同一 PipeNet | 先检查两种 gas，再原子扣除 |
| 管道拆除/reflood | 下次更新重新解析，不使用旧引用 |
| APC 断电 | 立即停机，不等待 Atmos tick，不再耗气 |
| 解锚 | 立即停机并断开管网 |
| DeviceLink Off | 不耗气，保留玩家关闭意图 |
| 燃料恢复 | 保持 Enabled，完整验证后自动恢复 |
| 任一喷口被堵 | 整台发动机停机且不耗气 |
| Engine 删除 | 清理 shuttle thrust、burn fixture 和状态 |
| 长时间 server hitch | 限制单次 dt 或分步消耗，避免巨量瞬时扣除 |
| 污染气体进入管网 | 首版只扣目标气体，污染不影响效率 |
| 电容升级 | 推力和燃气消耗同比提高，APC 负载保持固定 |

## 实施状态

### 2×4 已完成

- `NsvCombustionThrusterComponent/System` 与两路 PipeNode；
- 2400 base thrust、18000 APC load、1.5 Plasma/s 和 2.1 Oxygen/s；
- 原子扣气、单次 dt 上限、缺气停机与自动恢复；
- 通用多喷口校验及 tile 变化后的双向重验证；
- 四方向正式 RSI、2×4 fixture、burnShape、radar 和尾焰；
- Machine2x4 frame、graph、board、lathe recipe、建造、反建造和残骸；
- T2 parts 原型，3000 thrust 且燃料同比为 1.25 倍；
- `DamagedThrusters` 的 2×4 replacement 和通用规则 blacklist；
- 定向集成测试覆盖燃料、供电、DeviceLink、锚定、喷口、升级、删除、损毁和 board/frame 尺寸。

### 3×5 尚未实现

保留本文的 3×5 数值和设计作为后续参考，但当前没有 3×5 组件配置、实体原型、frame、board、recipe、资源或测试。

### 后续平衡与扩展

- 多人游戏内测试加速、制动、转向和电网压力；
- 调整 thrust、power 和 gas rate；
- 可选过热、排气、副产物和燃料爆炸；
- 可选气体纯度或燃烧室效率。

## 自动化验证

定向集成测试当前共 16 项并已通过（14 项显式行为测试及测试原型校验），覆盖：

1. 生产原型固定值、fixture、PipeNode、Atmos flags 和双喷口 offset；
2. 正常喷射、空闲不耗气和 dt 上限；
3. 缺 Plasma、缺 Oxygen、两者均缺和恰好够一次扣除的原子性；
4. 两个 intake 位于同一混合 PipeNet；
5. starvation 保留 `Enabled` 并在恢复供气后重新启用；
6. DeviceLink Off 后供气恢复不会覆盖关闭意图；
7. APC 断电/恢复与解锚/重新锚定；
8. 四方向任一喷口堵塞与解除堵塞；
9. 删除后的 shuttle thrust、Firing 和 burn fixture 清理；
10. T2 3000 thrust 与 1.25 倍燃气消耗；
11. 7500 damage 后生成 2×4 destroyed frame；
12. board/frame 尺寸匹配；
13. GridMod 专用规则 chance 失败时 blacklist 阻止 1×1 fallback；
14. GridMod 专用规则命中时生成 2×4 wreck。

### 待完成的游戏内 smoke test

- admin spawn 与完整建造/反建造；
- 四方向 sprite、fixture、双管口、双喷口和尾焰对齐；
- 空闲不耗气与满推 10 秒约消耗 15 Plasma / 21 Oxygen；
- 单路断气、自动恢复、APC 和 DeviceLink；
- T2 3000 thrust；
- 7500 damage 后的 2×4 wreck；
- 新质量系统下的实际加速度。

## 当前范围结论

已实现范围：

- 2×4：2400 thrust、18000 power、1.5 Plasma/s + 2.1 Oxygen/s；
- 两路直接 PipeNode 与 Atmos tick 消耗；
- 原子扣气、缺气自动停机与恢复；
- 多格喷口完整暴露检查；
- 完整 Machine2x4 建造链、board、lathe recipe 和 2×4 wreck；
- T2 parts 版本和定向集成测试。

尚未实现：3×5 发动机及其组件配置、实体、资源、建造链和测试。

范围外：内部储气罐、真实燃烧反应、废气、过热、爆炸、燃料纯度、按喷口比例降推力。
