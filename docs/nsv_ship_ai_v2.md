# NSV 敌方船只 AI 计划 v2

## 状态与范围

本文档是敌方船只 AI 的第二版行为设计,服务于路线图 M2(一场可玩的舰战)。范围为首版切片:

1. 战术状态机:**接近 / 缠斗 / 撤退** 三态,按优先级分支选择;
2. 残废信号:以 **AI core 被摧毁 / 断电 5 秒** 判定本舰失去战斗能力。

不在本版范围(层 2/3,见文末"后续方向"):

- 智能目标选择(优先打瘫指挥/补刀残件);
- 多舰协同、focus-fire;
- 新舰船内容变体。

所有新增代码与原型位于 `_NSV`;不修改 `_Mono` 的 steering、targeting、fire-control 与通用 NPC 系统。

## 现状与问题

当前 GUST 行为是一条扁平直线(`Resources/Prototypes/_NSV/Bluespace/AI/ship_ai.yml`):

```text
UtilityOperator(NsvBluespacePatrolTargets) 选最近 hostile 目标
  → ShipMoveToOperator: GoToRange 750,容差 150,始终面向目标
  → ShipFireGunsOperator: leadingAccuracy 0.6
  → UtilityService 每 0.25–0.6s 刷新目标
```

问题:

1. **没有状态。** 血量再低也不撤,距离再远也不换走位,三个岗位(尤其 Gunner 的"追不追"决策和 Engineer 的维修窗口)没有对手行为制造取舍。
2. **AI 无法感知自身状态。** 所有现有 consideration / precondition 都评估*目标*;黑板中不存在任何"本舰是否残废"的键。
3. **broadside 变体同样无状态机,且精度参数与 patrol 从未对齐。** `NsvBluespaceBroadsideAttackCompound`(`ship_ai.yml:88-117`)也是单分支扁平计划:固定 OrbitCW 450、侧舷 90°、全速,没有接近与撤退。Hunter 舰(`Resources/SharedMaps/_NSV/Bluespace/hunter.yml`,uid 173)直接内嵌 `NsvBluespaceBroadsideCore`,星图 Asteroid 节点经 `NSVBluespaceHunterSector` 使用该舰——因此本计划若只改 patrol root,Hunter 行为不变。另外 broadside 的 `leadingAccuracy` 为 0.4 而 patrol 为 0.6,调参时需一并确认。

   > **已排除的问题(v1 稿误判)。** 本文 v1 曾断言 broadside 两个 operator 的 DataField 被误写为 `operator:` 的兄弟节点而全部失效,推导出"贴脸 5 格、几乎刹停的固定靶"并将其列为前置修复项。复核 `ship_ai.yml:95-111`,DataField 均正确缩进在 `operator:` 之下,与 patrol 变体(36-56 行)结构一致;`git blame` 显示这些行自初始提交 `b958f19a4f` 起未被改动。**该 bug 不存在**,对应的修复章节与回归测试已从本计划移除。

4. 目标选择只看距离与存活(`TargetInverseDistanceCon` + `TargetIsAliveOrNACon`),评分无威胁/价值维度。

## 残废信号定义(核心决策)

按既定决策,本舰"失去战斗能力"的衡量标准是:

```text
AI core 被摧毁,或 ApcPowerReceiver 断电持续 ≥ 5 秒
```

对两种情形的机制分析:

### core 被摧毁

core 实体终止即 HTN 实体消失,不存在"被摧毁后撤退"的可能——舰船直接失去全部 AI,变成惰性残骸。这是既有终点(现有 `NSVPatrolContract` 的 Objective 即以 core 终止判定完成),无需新行为。玩家击毁 core = 击杀;本设计不涉及。

### 断电 ≥ 5 秒(设计重点)

`ShipMoveToOperator.Update` 在 `RequirePowered` 且 core 的 `ApcPowerReceiver` 未供电时直接返回 `Failed`(ShipMoveToOperator.cs:232-234)。也就是说:

- **断电期间无法转向**,断电本身已经使舰船瘫痪在原地——这本身就是有价值的玩法(打掉供电 = 击瘫,不击杀);
- **撤退只能在电力恢复后执行**。因此"断电 5 秒"的实现语义是**闩锁(latch)**:一旦累计断电达到阈值,该舰被永久标记为残废;电力恢复、operator 可以再次运转时,重规划使计划落入撤退分支。

行为效果:

```text
玩家打掉 GUST 供电 ≥5 秒
  → GUST 瘫痪漂移(断电期间)
  → 电力恢复(RTG 补给等)
  → GUST 重新苏醒,但姿态变为撤退:拉开距离 1500,继续边撤边打
```

这给 Pilot/Gunner/Engineer 提供了 M2 要求的取舍:追击残废目标消耗弹药与时间,放走它则丢失击杀;瘫痪窗口是玩家损管/补给的时间。

## 行为设计:三态状态机

### 状态与转换

```text
                    目标存在 + 未残废
          ┌──────────────────────────────┐
          ▼                              │
   [接近 Approach]                目标距离 > 缠斗阈值
   GoToRange 500±150                     │
   时速 4,规避弹道                        │
          │                              │
          │ 进入缠斗阈值(600)              │
          ▼                              │
   [缠斗 Brawl]                           │
   OrbitCW 450±50,侧舷 90°,全速 ──────────┘(追击逃离目标)

任意状态下闩锁 NsvCoreCrippled = true 且电力恢复:
          ▼
   [撤退 Retreat]
   GoToRange 1500±300,全速,始终面向目标,继续开火(leadingAccuracy 0.4)
```

三态的判别发生在 `NsvBluespaceEngageCompound` 的**分支优先级**上(HTN 原生能力:branches 顺序求值,第一个 precondition 通过的分支生效):

```text
分支 1 [NsvCoreCrippled == true]                  → 撤退(最高优先级)
分支 2 [目标距离 ≤ NsvBrawlRange]                 → 缠斗
分支 3 [TargetCoordinates 存在]                   → 接近(兜底)
```

### 参数表(全部待游戏内调参)

| 状态 | 参数 | 初值 | 说明 |
| --- | --- | --- | --- |
| 残废判定 | 断电阈值 | 5 s | DataField 可配 |
| 接近 | range / tolerance | 500 / 150 | 比现版 750 近,否则永远触不到缠斗阈值 |
| 接近 | inRangeMaxSpeed | 4 | 与现版一致 |
| 缠斗 | 进入阈值 NsvBrawlRange | 600 | 黑板 float 键,由计划头部 SetFloatOperator 写入 |
| 缠斗 | range / tolerance | 450 / 50 | OrbitCW 轨道半径 |
| 缠斗 | targetRotation | 90° | 侧舷对敌 |
| 缠斗 | inRangeMaxSpeed | null | 轨道全速 |
| 缠斗 | leadingAccuracy | 0.5 | 介于 patrol 0.6 与 broadside 0.4 之间;缠斗距离近,精度可低于接近态 |
| 撤退 | range / tolerance | 1500 / 300 | 拉开距离 |
| 撤退 | inRangeMaxSpeed | null | 全速撤退 |
| 撤退 | alwaysFaceTarget | true | 边撤边打(风筝) |
| 撤退 | leadingAccuracy | 0.4 | 远距低精度压制 |

### 已知取舍:分支重估时机

距离分支(接近↔缠斗)只在**重规划时**求值,不是每帧。重规划的触发来源:

- `ShipFireGunsOperator.Update` 检测到 TargetCoordinates 变化(战斗中 service 每 0.25–0.6s 刷新目标,目标移动即触发);
- 任务失败 / 计划完成 / 计划冷却到期。

转向本身由 `ShipSteeringSystem` 每帧维持距离,不受此限制。对首版切片可接受:缠斗轨道会主动追击逃离的目标(Orbit 模式本身是追踪的),只有"目标大幅度改变距离且未触发重规划"时状态切换略有延迟。若游戏内手感不足,后续再考虑给 HTNComponent 调低 plan cooldown 或增加强制重规划 service。

## 实现

### 1. 新增 C#(全部 `Content.Server/_NSV/NPC/`)

`NsvShipAiCoreStateComponent.cs`(服务器侧状态,挂在与 `NsvBluespaceShipAiCore` 相同的 core 实体上):

```csharp
[RegisterComponent]
public sealed partial class NsvShipAiCoreStateComponent : Component
{
    /// 断电累计阈值(秒)。达到即闩锁 Crippled。
    [DataField] public float CrippleAfterSeconds = 5f;

    /// 本次断电开始时刻;-1 表示当前有电。
    [ViewVariables] public TimeSpan UnpoweredSince = TimeSpan.MinValue;

    /// 一旦置 true 不再复位:该舰余下生命周期保持撤退姿态。
    [ViewVariables] public bool Crippled;
}
```

`NsvShipAiCoreStateSystem.cs`:

- 订阅 `PowerChangedEvent`,过滤带 `NsvBluespaceShipAiCoreComponent` 的 core:断电时记录 `UnpoweredSince = _timing.CurTime`;来电时若已 Crippled 则触发重规划(让现有计划在可转向后落入撤退分支);
- `Update()`:对处于断电的 core 检查 `CurTime - UnpoweredSince ≥ CrippleAfterSeconds`,首次达到时闩锁 `Crippled`,向黑板写入键并唤醒:

```csharp
htn.Blackboard.SetValue(NsvShipAiBlackboard.CoreCrippled, true);
_npc.WakeNPC(uid, htn);
_htn.Replan(htn);
```

写入与唤醒复用 `NsvBluespaceFactionSystem.ReplanSector` 已验证的同一模式(NsvBluespaceFactionSystem.cs:193-204)。`NPCBlackboard.SetValue(string key, object value)` 为通用 API,黑板不网络同步,无序列化风险(与 NSV 出售 UI key 曾缺失 `[NetSerializable]` 的问题不同源)。

`NsvShipAiBlackboard.cs`(常量):

```csharp
public static class NsvShipAiBlackboard
{
    /// 本舰是否已判定残废(bool)。
    public const string CoreCrippled = "NsvCoreCrippled";
}
```

### 2. `ship_ai.yml` 重构

根计划改为:写入调参常量 → 选目标 → 进入三态 engage compound:

```yaml
- type: htnCompound
  id: NsvBluespacePatrolCompound
  branches:
  - tasks:
    - !type:HTNPrimitiveTask
      operator: !type:SetFloatOperator
        targetKey: NsvBrawlRange   # 缠斗进入阈值,黑板 float 键
        amount: 600
    - !type:HTNPrimitiveTask
      operator: !type:UtilityOperator
        proto: NsvBluespacePatrolTargets
    - !type:HTNCompoundTask
      task: NsvBluespaceEngageCompound
```

`SetFloatOperator`(通用,Content.Server/NPC/HTN/PrimitiveTasks/Operators/Math/SetFloatOperator.cs)在规划期把常量写进黑板,使 `CoordinatesInRangePrecondition` 的 `rangeKey` 可用——两个现成 range precondition(`TargetInRange` / `CoordinatesInRange`)都只读黑板 rangeKey,不支持 YAML 字面量,因此必须用这一步桥接。调参仍在 YAML。

三态 engage compound:

```yaml
- type: htnCompound
  id: NsvBluespaceEngageCompound
  branches:
  # 分支 1:撤退(残废闩锁,最高优先级)
  - preconditions:
    - !type:KeyExistsPrecondition
      key: TargetCoordinates
    - !type:KeyBoolEqualsPrecondition
      key: NsvCoreCrippled
      value: true
    tasks:
    - !type:HTNPrimitiveTask
      operator: !type:ShipMoveToOperator
        shutdownState: PlanFinished
        removeKeyOnFinish: false
        targetKey: TargetCoordinates
        range: 1500
        rangeTolerance: 300
        inRangeMaxSpeed: null
        alwaysFaceTarget: true
        avoidProjectiles: true
    - !type:HTNPrimitiveTask
      operator: !type:ShipFireGunsOperator
        shutdownState: TaskFinished
        removeKeyOnFinish: false
        targetKey: TargetCoordinates
        leadingAccuracy: 0.4
      services:
      - !type:UtilityService
        id: TargetsService
        proto: NsvBluespacePatrolTargets
        key: Target
        coordinatesKey: TargetCoordinates

  # 分支 2:缠斗(目标已进入缠斗阈值)
  - preconditions:
    - !type:KeyExistsPrecondition
      key: TargetCoordinates
    - !type:CoordinatesInRangePrecondition
      targetKey: TargetCoordinates
      rangeKey: NsvBrawlRange
    tasks:
    - !type:HTNPrimitiveTask
      operator: !type:ShipMoveToOperator
        shutdownState: PlanFinished
        removeKeyOnFinish: false
        targetKey: TargetCoordinates
        mode: OrbitCW
        range: 450
        rangeTolerance: 50
        inRangeMaxSpeed: null
        targetRotation: 90
        alwaysFaceTarget: true
        avoidProjectiles: true
    - !type:HTNPrimitiveTask
      operator: !type:ShipFireGunsOperator
        shutdownState: TaskFinished
        removeKeyOnFinish: false
        targetKey: TargetCoordinates
        leadingAccuracy: 0.5
      services:
      - !type:UtilityService
        id: TargetsService
        proto: NsvBluespacePatrolTargets
        key: Target
        coordinatesKey: TargetCoordinates

  # 分支 3:接近(兜底)
  - preconditions:
    - !type:KeyExistsPrecondition
      key: TargetCoordinates
    tasks:
    - !type:HTNPrimitiveTask
      operator: !type:ShipMoveToOperator
        shutdownState: PlanFinished
        removeKeyOnFinish: false
        targetKey: TargetCoordinates
        range: 500
        rangeTolerance: 150
        inRangeMaxSpeed: 4
        alwaysFaceTarget: true
        avoidProjectiles: true
    - !type:HTNPrimitiveTask
      operator: !type:ShipFireGunsOperator
        shutdownState: TaskFinished
        removeKeyOnFinish: false
        targetKey: TargetCoordinates
        leadingAccuracy: 0.6
      services:
      - !type:UtilityService
        id: TargetsService
        proto: NsvBluespacePatrolTargets
        key: Target
        coordinatesKey: TargetCoordinates
```

### 3. 实体原型改动

`NsvBluespacePatrolCore` 增加:

```yaml
- type: NsvShipAiCoreState   # 断电 5 秒闩锁 + 撤退信号来源
```

`NsvBluespaceBroadsideCore` 本版**不加**:它的 root 仍是单态 `NsvBluespaceBroadsideCompound`,没有读 `NsvCoreCrippled` 的分支,挂上只会空转闩锁。Hunter 是否改用三态 `NsvBluespaceEngageCompound`,待 patrol 三态在游戏内调参稳定后再定。

## 边界与不变量

- 全部新增在 `_NSV`;`_Mono` 的 `ShipSteeringSystem` / `ShipTargetingSystem` / `ShipMoveToOperator` / `ShipFireGunsOperator` 与通用 NPC precondition / operator 零修改,仅组合使用。
- `NsvCoreCrippled` 是服务器侧黑板键,不网络同步,客户端不可见也不可伪造;与 faction 变化一样,只有服务器写、服务器读。
- 闩锁生命周期 = core 组件生命周期:sector 回收重建后新 core 全新状态,不跨 sector 记忆。
- 残废舰对*任何后续目标*都保持撤退姿态(闩锁不复位):设计上"残废的 GUST 不再主动接战",行为可预期。
- 不给 core 断电期间编造移动:断电即瘫(ScopeToPowered 语义保持原样)。

## 测试计划

`Content.IntegrationTests/Tests/_NSV/NPC/` 新增:

1. **闩锁触发**:在测试扇区生成 patrol core,将其 `ApcPowerReceiver` 置为无电,推进 ≥5 秒,断言 `HTNComponent.Blackboard` 的 `NsvCoreCrippled == true` 且组件 `Crippled` 已闩锁;断电 <5 秒恢复电力则不闩锁。
2. **撤退分支选中**:闩锁后恢复电力,提供 hostile 目标,等待重规划,断言 `ShipSteererComponent` 的 `Range == 1500`、`AlwaysFaceTarget == true`(撤退参数生效)。
3. **缠斗/接近切换**:未残废 core + 远距目标 → 断言 steerer 为 `GoToRange` 且 `Range == 500`;目标位于 600 内 → 断言 `Mode == OrbitCW`、`Range == 450`。
4. **闩锁不影响目标查询**:残废 core 仍能锁定新目标(service 正常刷新),但分支恒为撤退。

回归:现有 `NsvShipTargetQueryTest`、`NsvBluespaceSectorSystemTest`、`NsvBluespaceEncounterSystemTest` 全量保持通过;测试结尾维持"返航 + TryDispose"惯例。

## 验证

```powershell
dotnet build .\Content.Server\Content.Server.csproj -c Debug --no-restore /clp:ErrorsOnly

dotnet test .\Content.IntegrationTests\Content.IntegrationTests.csproj -c Debug --no-restore `
  --filter "FullyQualifiedName~NsvShipAiCoreState|FullyQualifiedName~NsvShipTargetQueryTest|FullyQualifiedName~NsvBluespaceEncounterSystemTest" -v:q
```

游戏内 smoke test:进入 PiratePatrol 节点,GUST 正常接近至 ~500 开始绕圈侧舷;打掉其供电 ≥5 秒后恢复,观察其转向拉开距离并低精度还击;击毁 core 确认 Objective 正常完成。参数(阈值、各 range、accuracy)按实测手感调整。

## 待调参项(游戏内确认)

- 断电阈值 5 秒是否过长/过短(RTG 恢复速率决定残废是否可观察);
- 接近距离从 750 收到 500 后 GUST 接敌压力变化;
- 缠斗轨道 450 与 `WeaponTurretShard` 实际有效射程的匹配(当前未按武器射程自适应,层 2 候选);
- 撤退 1500 距离在测试扇区尺度下是否等于"脱离战斗";
- patrol 0.6 / 缠斗 0.5 / broadside 0.4 三档 `leadingAccuracy` 是否需要按距离统一成一套规则。

## 后续方向(层 2/3,未排期)

- **层 2 智能目标选择**:为 `NsvBluespacePatrolTargets` 增加 `TargetHealthCon`(补刀残件)与新的 `_NSV` 优先级 consideration(火控台/驾驶台 > 普通目标),使 GUST 学会打瘫指挥;
- **层 3 内容与协同**:多舰变体组合(缠斗 + 侧舷)、focus-fire(借 faction system 既有 replan 钩子)、按武器射程自适应交战距离;
- 交战距离与武器解耦后,可为不同舰种 YAML 化整套战术参数。

## 相关文档

- Mono 舰船 HTN 结构与执行层:`docs/feature_monoshiphtnai.md`
- 扇区、faction 与 GUST 生成:`docs/nsv_tech_design.md`
- 路线图与里程碑:`docs/nsv_game_loop_design.md`
