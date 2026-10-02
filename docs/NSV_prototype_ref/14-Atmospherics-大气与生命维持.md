> 本文为 research/evidence/atmospherics.md 的中文翻译。

# 大气与生命维持（舰船）

事实来源：`D:\code\NSV13` 处的当前 NSV13 检出。所有玩法断言均沿代码
路径追踪；行号引用所检出文件的对应行。范围为*舰船侧*大气及其与空间站
的差异。重叠系统仅交叉引用，不重新推导：
- `research/evidence/damage_control.md` — 内部火灾、破口、hullburn、中继弹丸。
- `research/evidence/ftl.md` — 驱动塔架上的 GAS_NUCLEIUM 燃料循环。

## 系统概述

NSV13 舰船存在于两个彼此独立的大气世界中：

1. **内部 z 层（大舰）。** 玩家舰的内部是一张普通的 SS13 地图。舰船内部
   *没有*任何 NSV 专属的区域、地块或 LINDA 覆写——它们使用与任何空间站
   相同的管路网 / LINDA / auxgm 机械。`nsv13/code/game/area/areas.dm` 仅定义
   区域（名称/图标），没有大气规则。因此舰体上的破口、火灾或通风口的表现
   与空间站对应物完全相同（交叉引用系统 12）。
2. **星图对象（战斗机 / 小型飞行器）。** 一个 `/obj/structure/overmap` 是*承载
   生物的对象*，而非一张地图。小型飞行器携带自己的私有 `cabin_air` 气体混合，
   并实现 `return_air()`，因此乘员的肺从座舱混合气而非某个地块抽取
   （`overmap.dm:45`，`_fighters.dm:1979-2000`）。这是"舰船大气"唯一真正
   自定义的地方。

该系统建立在 **auxgm** 气体层之上（14 种气体类型，每种气体的 TLV/呼吸数据位于
`code/modules/atmospherics/auxgm/gas_types.dm`）。NSV 恰好为其添加了两种气体——
**受限等离子**与 **nucleium**——以及搬运它们的机械，并将两者贯穿于
现有的空气警报 / 火灾 / 洗涤器 / 肺检查。两者都是 `GAS_FLAG_DANGEROUS`。

## 核心玩法循环

小型飞行器：
1. 一架战斗机出生时 `canopy_open = TRUE`（`_fighters.dm:45`）；你必须打开座舱盖才能
   爬入（`_fighters.dm:637-647`，`569-572`）。打开时你呼吸的是*周围*的空气。
2. 安装/保留座舱盖组件并将其关闭（`canopy_lock` UI 动作，`_fighters.dm:307-312`）；
   一旦密封且完好，你呼吸的就是座舱的 `cabin_air`。
3. 一个**大气调节器**挂点持续补充座舱 O2/N2，并以电池电力洗涤 CO2
   （`_fighters.dm:1478-1532`）。它会把座舱轻松加压到约 300 kPa。
4. 战斗可以摧毁座舱盖 → `canopy_breach()` 将其弹掉（`_fighters.dm:839-846`）。此后
   `return_air()` 交给乘员的是*外部*大气：在太空中即为真空 → 窒息。没有
   脚本化的"座舱排空到太空"——见 ATMOS-004。

大舰：与空间站生命维持完全相同；NSV 的有趣内容在于新增的反应堆气体
（压缩器 → 受限等离子；反应堆冷却产出 nucleium），它们泄漏进并被与
其他一切相同的警报/通风口/洗涤器处理。

## 机制

### ATMOS-001
断言：小型飞行器的座舱持有私有气体混合，`cabin_air`，在
`/obj/structure/overmap/small_craft/Initialize` 中为每架可战斗飞行器创建。
体积 200 L，温度 `T20C`，使用理想气体定律充至 1 个大气压的
标准空气（21% O2 / 79% N2）。
面向玩家的后果：密封的战斗机座舱从一开始就是安全、可呼吸的约 101 kPa，完全
独立于战斗机停靠处的任何大气。
证据：`overmap.dm:45`（变量），`overmap.dm:409-416`（初始化：`set_volume(200)`、`T20C`、
`GAS_O2 = O2STANDARD*ONE_ATMOSPHERE*V/(R*T)`、`GAS_N2 = N2STANDARD*...`）。
条件：`O2STANDARD=0.21`、`N2STANDARD=0.79`、`R_IDEAL_GAS_EQUATION=8.31`、`ONE_ATMOSPHERE=101.325`
（`code/__DEFINES/atmospherics.dm:3-13`）。总计约 8.3 mol → 约 101.3 kPa。
例外 / 覆写：`cabin_air` 在舰船毁灭时被 `QDEL_NULL`（`overmap.dm:552-553`）。
汽车（`/obj/vehicle/sealed/car/realistic/_vehicle.dm:389-396`）构建相同的座舱。一个
未密封的 `small_craft` 仍拥有一个 `cabin_air`，只是在座舱盖打开时不被使用。
置信度：HIGH（高）

### ATMOS-002
断言：**大气调节器**（`/obj/item/fighter_component/oxygenator`，挂点槽位
`HARDPOINT_SLOT_OXYGENATOR`）是战斗机的生命维持单元。每 tick 它将座舱设为 `T20C`，
添加 `refill_amount*O2STANDARD` 的 O2 与 `refill_amount*N2STANDARD` 的 N2，同时移除 `refill_amount`
的 CO2——它*从无到有制造*空气（无消耗品），仅由电池电力门控。
面向玩家的后果：一个可用调节器能让密封座舱无限期保持可呼吸；失去
调节器（且不撤离飞行器）就是在密封座舱中缓慢死亡。它还会过压
到约 3 个大气压，这无害，且在低于 325 kPa 时不触发任何警报。
证据：`_fighters.dm:1508-1532`（`process` 守卫 `pressure + refill_amount >= WARNING_HIGH_PRESSURE
- 2*refill_amount` → return；`refill()` 添加 O2/N2，减去 CO2）。`WARNING_HIGH_PRESSURE = 325`
（`code/__DEFINES/atmospherics.dm:66`）。`refill_amount`：tier1 = 1，tier2 = 3，tier3 = 10
（`_fighters.dm:1482,1491,1499`）；`power_usage` 200/300/400。
条件：`process()` 首先调用 `..()` = `power_tick(delta_time)`，它从
战斗机的**电池**抽取 `power_usage`；若电池无法支付，它返回 FALSE 且不添加空气
（`_fighters.dm:1043-1051,1513-1514`）。电池耗尽时座舱停止被补充。
例外 / 覆写：`oxygenator/plasmaman`（tier 4）反转混合——添加 `GAS_PLASMA`，移除 O2 与
N2（`_fighters.dm:1502-1506,1529-1532`）——供等离子人驾驶员使用，他们的肺会灼烧氧气。
置信度：HIGH（高）

### ATMOS-003
断言：战斗机乘员呼吸哪种混合气由 `return_air()` 决定：它**仅当**安装了座舱盖
组件、座舱盖处于*关闭*状态（`canopy_open == FALSE`）、且座舱盖的
`obj_integrity > 0` 时才返回 `cabin_air`。否则它返回 `loc.return_air()`——周围的
星图/地块空气。
面向玩家的后果：关闭座舱盖才是"密封"座舱的操作；座舱盖打开（或
不存在，或被摧毁）时，驾驶员呼吸的是环境。一架停在加压机库中且
座舱盖打开的飞行器可呼吸；同一架飞行器在太空中就是真空。
证据：`_fighters.dm:1979-1983`（`if(canopy_open || !C || C.obj_integrity <= 0) return loc.return_air()`）。
内部生物经 `forceMove` 移入战斗机（`_fighters.dm:661`），因此 `breathe()` 读取
`loc.return_air()` = 该战斗机（`code/modules/mob/living/carbon/life.dm:103,128-141`；对象路径
`code/game/objects/objs.dm:167-177`）。
条件：需要一个 `HARDPOINT_SLOT_CANOPY` 组件。切换座舱盖需要组件
存在（`_fighters.dm:307-312`），且大多数座舱还需要获授权用户。
例外 / 覆写：逃生舱出生时 `canopy_open = FALSE`（`_fighters.dm:469`），即预先密封。
置信度：HIGH（高）

### ATMOS-004
断言：在飞行器处于真空中时，座舱盖破损/被移除**不会**排空 `cabin_air`。
`slowprocess` 中的"泄漏"路径仅当外部地块具有*非零*压力*且*座舱
压力高于它时才移除空气——因此它在战斗机位于加压房间时触发，而非在太空中。
面向玩家的后果：座舱盖有洞的战斗机在太空中杀死驾驶员的方式是让他们
*呼吸真空*（ATMOS-003），而非排空座舱；座舱混合气只是闲置无用。反之，
一架有洞的战斗机停在加压舰船内会缓慢将其（过压的）座舱排放到
房间中。音/视提示（破口音效、气流呼啸消息）仍会播放。
证据：`_fighters.dm:1969-1976` — `if((!C || C.obj_integrity <= 0) && cabin_air.total_moles() > 0)`，
`outside_pressure = loc.return_air().return_pressure()`，由 `if(outside_pressure && cabin > outside)`
守卫，然后 `cabin_air.remove(min(total, 5))`。太空地块是真空（`space.dm:40-43` 赋值为共享的
不可变 `space_gas`），因此在太空/星图中 `outside_pressure` 为 0，该被守卫的行被跳过。
条件：星图 z 层地块是真空；守卫在词法上就是 `outside_pressure &&`。
例外 / 覆写：无论座舱盖状态如何，座舱每星图 tick 也会被基类
`/obj/structure/overmap/slowprocess` 冷却向 `T20C`（`physics.dm:139-141`），战斗机自身的
`slowprocess` 通过 `..()` 调用它（`_fighters.dm:1959-1960`）。泄漏行的守卫字面上就是
`outside_pressure &&`。
置信度：MEDIUM（中）（泄漏路径本身无歧义；"太空中无泄漏"是对
`outside_pressure &&` 守卫的直接解读——值得在游戏内确认）。

### ATMOS-005
断言：座舱盖损伤是战斗机破坏者 / 炮手的杠杆。每次对战斗机造成损伤的命中
有 50% 几率对座舱盖造成一半的来袭伤害；当 `obj_integrity <= 0` 时座舱盖被
`qdel`，且 `canopy_breach()` 播放破口音效与"空气呼啸而出"消息。完全无
座舱盖飞行也会让命中有时直接击中乘员。
面向玩家的后果：足够的擦弹把座舱盖撕碎，将座舱变成一个未密封的盒子；
忽视它的驾驶员随后在真空中窒息。
证据：`_fighters.dm:822-846`（伤害路径 + `canopy_breach`：`remove_hardpoint(canopy, TRUE)`、
`qdel`、中继破口 + 防毒面具音效），`_fighters.dm:826-831`（无座舱盖时的乘员伤害），`_fighters.dm:839`。
按等级的座舱盖完整度：玻璃 100，强化 200，纳米碳 350，等离子玻璃 450
（`_fighters.dm:1182-1213`）。
条件：仅在伤害事件时触发（邻接 `/obj/structure/overmap/small_craft/obj_break` 的
伤害 proc）。登舰/弹射也需要座舱盖打开（`_fighters.dm:637-647,676-684`）。
例外 / 覆写：贴图在 `obj_integrity <= 20` 时显示 `canopy_breach`（`_fighters.dm:1951-1952`），
但*可呼吸/泄漏*阈值是 `<= 0`——完整度在 1 到 20 之间时座舱盖看似开裂，
却仍然密封（ATMOS-003）。
置信度：HIGH（高）

### ATMOS-006
断言：星图基类声明了 `internal_tank`（一个 `portable_atmospherics/canister`），并注释说
你可以通过向小型舰船装入一个等离子罐来破坏它——但**没有任何小型飞行器会创建
或引用 `internal_tank`**。战斗机由调节器维持（ATMOS-002），而非可替换的
罐；没有任何玩家动作会将气罐插入战斗机。
面向玩家的后果："把等离子罐装入战斗机座舱"并*不是*一种可用的破坏手段。
真实的（且唯一被编码的）破坏途径是卸载调节器/座舱盖挂点，或
击穿座舱盖。
证据：`overmap.dm:46`（变量 + 注释）。对 `internal_tank` 的 grep 显示仅在
`_vehicle.dm:396`（汽车）与 `gauss_gun.dm:129`（炮塔）中有作为自包含 `canister/air` 的赋值；
在 `_fighters.dm` 中没有赋值或读取。战斗机组件经 `attackby`/`install_hardpoint`
安装（`_fighters.dm:612-619,934-942`），经维修 UI 卸载（`_fighters.dm:237-248`）。
条件：无。
例外 / 覆写：`physics.dm:142-163`（基础舰船 tick）将 `internal_tank → cabin_air` 输送，
并回排到地块；对战斗机而言此块是惰性的，因为 `internal_tank` 为 null。
置信度：HIGH（高）（战斗机不使用它 / MEDIUM（中）关于该注释的原意——
视为死/误导性注释，而非机制）。

### ATMOS-007
断言：舰船*内部*大气逐字节等同空间站大气。对于舰船自身的 z 层，没有
NSV 对 LINDA、地块、管路网、空气警报或区域级大气的覆写。NSV 对内部系统
的唯一改动是两种新增气体及其在共享机械中的注册。
面向玩家的后果：工程师完全按空间站的方式扑灭舰船火灾、破口或
过/欠压（通风口、洗涤器、空气警报、智能泡沫）；不适用任何特殊的"舰船"规则。
证据：`nsv13/code/game/area/areas.dm` 定义的区域没有大气字段；内部 z 层是
普通地图（`_maps/map_files/...`）。共享机械携带 NSV 编辑：空气警报具有
`GAS_CONSTRICTED_PLASMA` / `GAS_NUCLEIUM` 的 TLV（`airalarm.dm:105-106,144-145,548-549`）、便携式
洗涤器洗涤两者（`portable/scrubber.dm:21-22`）、LINDA 火灾将受限等离子视为燃料
（`environmental/LINDA_fire.dm:23,180`），且等离子装填器以"异端"非-phoron 为由拒绝它们
（`plasma_loader.dm:16-30`）。
条件：ZTRAIT_OVERMAP / 内部 z 层是普通的空间站 z 层（`code/__DEFINES/maps.dm:47,85`）。
例外 / 覆写：未发现——不要假设存在舰船专属的"舰体完整度门控大气"
规则；破口只是一个缺失的墙/地板地块。
置信度：HIGH（高）

### ATMOS-008
断言：NSV 向 auxgm 名册添加两种危险气体，两者皆可见且都关系到警报/呼吸：
**受限等离子**（`specific_heat 250`）与 **nucleium**（`specific_heat 450`，全游戏
最高）。空气警报在分压 ≥ 0.2（警告）与 ≥ 0.5（危险）时分别标记每一种。
面向玩家的后果：任何可察觉的受限等离子或 nucleium 泄漏几乎立即触发标准空气
警报（0.2 kPa 分压只是痕量），且两种气体都以自己的覆盖层渲染
（`moles_visible = MOLES_GAS_VISIBLE`），因此空间会充满可见气体。
证据：`code/modules/atmospherics/auxgm/gas_types.dm:185-199`；TLV `dangerous` = `max1 0.2 / max2 0.5`
（`airalarm.dm:30-34`）在 `airalarm.dm:105-106` 处应用。`GAS_CONSTRICTED_PLASMA` / `GAS_NUCLEIUM` 定义于
`code/__DEFINES/atmospherics.dm:300-301`。
条件：受限等离子还馈入所有等离子火灾逻辑（ATMOS-011）；nucleium 也是一种肺
毒素 + 辐射源（ATMOS-010）。
例外 / 覆写：`/obj/machinery/airalarm/server` 与 `/all` 变体对这些
气体使用 `no_checks`（`airalarm.dm:109-126`）——服务器机房空气警报*不会*对其报警。
置信度：HIGH（高）

### ATMOS-009
断言：**磁性压缩器**（`/obj/machinery/atmospherics/components/binary/magnetic_constrictor`）
是一个管路配件 `binary` 大气设备，在其输出管上将输入的 `GAS_PLASMA` 转化为
`GAS_CONSTRICTED_PLASMA`，并自我调节其输入阀。受限等离子是反应堆的优质燃料。
面向玩家的后果：它是让等离子在进入反应堆之前被"赋能"的舰船工程设备；
在其处（或下游发射器处）或其周围发生的火灾是受限等离子火灾，而不是
普通等离子火灾。对其 emag 会使其将受限等离子泄漏到其自身所在的
地块。
证据：`nsv13/.../constrictor.dm:42-77`（`process_atmos` 将 `min(constriction_rate, plasma_moles)`
作为 `GAS_CONSTRICTED_PLASMA` 从 `air1`→`air2` 移动，受 `max_output_pressure` 上限约束）；emag 分支
`constrictor.dm:58-74,135-141`。零件缩放：`constriction_rate = 0.9 + 0.1*capacitor`、
`max_output_pressure = 100 + 100*manipulator`（`constrictor.dm:25-33`）。反应堆价值：RBMK 将
受限等离子视为 2×、氚视为 10× 等离子燃料（`nsv13/.../reactor/rbmk.dm:303`）。
条件：需要 `on` 且存在等离子；`emagged` 还需要设备位于开阔地块上才能泄漏
（`isopenturf`）。
例外 / 覆写：`PIPING_ONE_PER_TURF`；经撬棍拆解，经螺丝刀打开面板
（`constrictor.dm:79-121`）。
置信度：HIGH（高）

### ATMOS-010
断言：**Nucleium** 是一种定制的 NSV 气体，具有专用的存储/监视循环与严苛的
生物学效应。NSV 添加了一个 nucleium 压力罐、一个 nucleium 输入出口-注射器
和一个 nucleium 输出虹吸器（专用于 `GAS_NUCLEIUM` 的标准大气罐机械），
接入一台"Nucleium Supply Monitor"罐控制台。呼吸 nucleium 会造成灼伤伤害并
致辐射，且在分压 > 15 时可直接摧毁肺。
面向玩家的后果：排放的反应堆 nucleium 既是灼伤/辐射危害又是肺杀手，
因此 nucleium 泄漏（例如来自反应堆冷却剂输出）对任何没有内呼吸装置的人来说
是一起严重的、近似失压的事件。
证据：罐 `nsv13/.../unary_devices/tank.dm`（gas_type GAS_NUCLEIUM），注射器
`outlet_injector.dm`，虹吸器 `vent_pump.dm`，控制台 `atmos_control.dm`（罐控制台 + 传感器）。呼吸
伤害 `code/modules/surgery/organs/lungs.dm:274-301`（分压区间：0.1–5 → 1 灼伤 +1 辐射；5–15 → 3/3；
15–30 → 5/5；30+ → 10/10；`pp>15` 有小几率 `Remove`/`qdel` 肺）。nucleium 还是一种
`GAS_FLAG_DANGEROUS` 气体，具有 TLV（ATMOS-008）。
条件：暴露经由呼吸（分压），因此内呼吸装置/关闭的战斗机座舱盖可防护。
例外 / 覆写：气罐包含 nucleium 与受限等离子（`portable/canister.dm:57-58`）；
nucleium 具有出口价值（`cargo/exports/large_objects.dm:150`）。Nucleium 由反应堆*产生*
（RBMK 冷却剂输出 `rbmk.dm:313`；stormdrive `stormdrive.dm:798-802`），并由 FTL 驱动
塔架*消耗*（交叉引用 `research/evidence/ftl.md`）。
置信度：HIGH（高）

### ATMOS-011
断言：受限等离子以自己的气体反应燃烧，功能上是等离子燃烧的克隆。
NSV 添加了 `/datum/gas_reaction/constricted_plasmafire`（优先级 **-3**），并将 `genericfire` 从
-3 推到 **-4** 以腾出位置。因此火灾链为 tritfire(-1) → plasmafire(-2) →
constricted_plasmafire(-3) → genericfire(-4)。
面向玩家的后果：受限等离子是一种完整合格的火灾燃料。受限等离子火灾
像等离子火灾一样蔓延热量/造成伤害（它在自身地块上调用 `hotspot_expose` / `temperature_expose`），
因此反应堆供料等离子事故是一场真实的火灾，而非无害的加压气体。
证据：`nsv13/.../gasmixtures/reactions.dm:1-71`（反应体镜像 `plasmafire`，使用
`GAS_CONSTRICTED_PLASMA`，优先级 -3）；基础 `reactions.dm:181-184` plasmafire=-2，`:252-253`
genericfire=-4（NSV 注释）。火灾抑制/燃料检查包含受限等离子
（`environmental/LINDA_fire.dm:23` `has_fuel = plasma + constricted_plasma > 0.5`，`:180`）。
条件：与 plasmafire 相同的最低要求（`TEMP = FIRE_MINIMUM_TEMPERATURE_TO_EXIST`，受限等离子
与 O2 各一摩尔）。
例外 / 覆写：过饱和会像等离子火灾一样形成氚（`reactions.dm:36-51`）。
置信度：HIGH（高）

### ATMOS-012
断言：当战斗机座舱破口时，驾驶员被视作呼吸外部空气，座舱的保护性气体
（包括受限等离子火灾混合气，若其以某种方式存在）被绕过。
各种毒素从任何环境渗入生物。
面向玩家的后果：破口的战斗机完全不提供大气防护；座舱盖——而非
舰体——才是生命维持边界。
证据：`_fighters.dm:1979-1983`（return_air）；通用毒性经由
`code/modules/mob/living/carbon/life.dm:186,231` 应用（等离子 + 受限等离子合并计为
毒素；安全毒性阈值）以及 auxgm 肺路径（`code/modules/surgery/organs/lungs.dm`）。
条件：仅当安装了座舱盖、关闭且完整度 >0 时才密封。
例外 / 覆写：无。
置信度：HIGH（高）

## 跨系统依赖

- **系统 12（损管 / 火灾与破口）。** 舰船内部像空间站一样破口/燃烧（ATMOS-007）。
  中继弹丸可引发内部等离子火灾并生成辐射/等离子空气（见该文件的
  注入通道）。战斗机的 `relay_damage` 是空操作，因此战斗机命中从不创建内部。
- **系统 15 / FTL。** FTL 驱动塔架循环是一个 `GAS_NUCLEIUM` 进 → 废气出的大气设备；
  驱动消耗 nucleium 并将其排放到废气（`drive_pylon.dm:81-118,235-240`，交叉引用 ftl.md）。
- **电力（反应堆）。** RBMK 冷却剂输出排放 nucleium，必须被过滤/存储，否则它会成为
  肺/辐射危害（`rbmk.dm:313` 注释 "filter this off, or you're gonna have a bad time"）。
  Stormdrive 也搬运 nucleium/受限等离子（`stormdrive.dm:525,558,796-802`）。
- **事件。** 放射性星系在其事件池中列出"致命"辐射风暴与放射性污泥
  （`starsystem.dm:838-841`）。致命风暴（普通辐射风暴的子类）设置
  `weight = 0`；普通的辐射风暴天气使保护区域外的任何人也受辐射，且 NSV 新增的
  IPC 获得脑创伤而非突变（`code/datums/weather/weather_types/radiation_storm.dm:36-68`）。
  放射性污泥在随机空间站区域投放 `nuclear_waste` 贴花
  （`nsv13/.../events/weather/radioactive_sludge.dm`）。仅作说明——完整事件机制位于
  事件/星系研究中。

## 开放问题

1. ATMOS-002：调节器的生命维持是否真正独立于引擎状态？`ship_loadout`
   `START_PROCESSING(SSobj, src)`（`_fighters.dm:925`）意味着该组件每 2 秒自我处理一次
   （`SSobj` 等待 = 2 秒，`processing/obj.dm:5`），*而且*战斗机在 `engines_active()` 时
   从 `slowprocess` 再次调用 `loadout.process`（`_fighters.dm:1961-1963`）。净效果看起来是
   "电池有电时始终开启，引擎运行时双 tick"。需要实机检查；两种情况下
   补充数学（ATMOS-002）都不受影响。
2. ATMOS-004：在游戏内确认座舱盖**在太空中**破口不会产生座舱压力
   下降（只有窒息）。守卫 `outside_pressure &&` 在代码中无歧义，但反直觉。
3. "把等离子罐放进战斗机"这一伎俩是否存在于任何地图内容中（例如一个接线到
   星图的气罐），还是 `overmap.dm:46` 的注释纯属残留？未发现代码路径。
4. 放射性污泥的 `possible_events` 使用 `list(typeA, typeB = 5)`（`starsystem.dm:840`）—— `= 5`
   作为关联值附加，而非权重，对照 `events.dm:76-96` 中的选择循环
   看起来是畸形的。核实放射性系统事件池是否真正触发。
