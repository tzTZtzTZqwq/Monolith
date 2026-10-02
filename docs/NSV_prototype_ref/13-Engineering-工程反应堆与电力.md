> 本文为 research/evidence/engineering_power.md 的中文翻译。

# 工程 — 反应堆与电力

范围：NSV13 舰船如何产生并分配电力，以及它们如何以危险的方式失效。
事实来源：在 D:\code\NSV13 处阅读的代码（包括 `nsv13/code/...` 与基础 `code/...`）。
约定：(a) 已证实机制 = 在代码中追踪得到；(b) 解读；(c) 策略。

## 系统概述

NSV13 舰船就是普通的 /tg/station z 层：它们使用现成的电力机械
（`/obj/machinery/power/smes`、`/obj/machinery/power/apc`、`/obj/structure/cable`、
区域 `requires_power`），再加上 NSV13 专属的**发电机**与**直连电缆消费者**。
**没有 NSV13 版 APC/SMES 替代品**（grep：NSV13 仅因以太充能而改动 `apc.dm`，
`apc.dm:826,858`）；停电行为与 /tg/ 原版一致。

发电链：`reactor` 直接写入电缆电网
（`C.powernet.newavail += last_power_produced`，rbmk.dm:318，stormdrive.dm:719）；
SMES 缓冲；APC 按区域通道（设备/照明/环境）分配。

存在两种 NSV13 "舰载反应堆"：
- **RBMK / AGCNR** — `Advanced Gas-Cooled Nuclear Reactor`（先进气冷核反应堆）
  （`/obj/machinery/atmospherics/components/trinary/nuclear_reactor`，rbmk.dm）。
- **Stormdrive** — `class IV nuclear storm drive`（IV 级核风暴引擎）
  （`/obj/machinery/atmospherics/components/binary/stormdrive_reactor`，stormdrive.dm），
  另有一个 `/solgov` 子类型 `class V ionic storm drive`（V 级离子风暴引擎）（stormdrive.dm:178）。

部分舰船也使用现成 /tg/ 电力：**特斯拉引擎**（Serendipity）以及
**仅便携式 PACMAN 发电机**（Eclipse，"No complex reactor setup"，
`_maps/eclipse.json`）；仓库中同样存在基础的奇点/超物质机械。

### 每张玩家地图使用哪个反应堆
通过统计地图 `.dmm` 文件中的反应堆原子，并交叉比对
`_maps/*.json` 的 `ship_type` 来确定：

| 地图（舰船） | ship_type | Stormdrive | RBMK | 其他 |
|---|---|---|---|---|
| Vago（护卫舰） | `frigate/starter` | 1 | 0 | — |
| Atlas（护卫舰） | `frigate/starter` | 1 | 0 | — |
| Shrike（护卫舰） | `frigate/starter/shrike` | 1 | 0 | — |
| Snake（巡逻巡洋舰） | `patrol_cruiser/starter` | 2 | 0 | — |
| Hammerhead（重型巡洋舰） | `heavy_cruiser/starter` | 1 | 0 | — |
| Tycoon（战列巡洋舰） | `battlecruiser/starter` | 1 | 1 | — |
| Gladius（战列巡洋舰） | `battlecruiser/starter` | 1 | 1 | — |
| Galactica（战列舰） | `battleship/starter` | 1 | 2 | — |
| Aetherwhisp（SGV） | `solgov/aetherwhisp/starter` | 1 | 1 | — |
| Von Neumann（SGV） | `solgov/vnc/starter` | 0 | 1 | — |
| Serendipity（DLV） | `serendipity/starter` | 0 | 0 | 特斯拉引擎 |
| Eclipse（轻型巡洋舰） | `light_cruiser/starter` | 0 | 0 | PACMAN 便携式 |

解读：**Stormdrive 是默认/主要的玩家舰引擎**（12 张地图中的 9 张，
全部纯护卫舰/巡洋舰舰体）。RBMK 出现在更大或 SolGov 舰体上，
通常作为 Stormdrive 旁边的*第二*套动力装置（Tycoon、Gladius、
Galactica、Aetherwhisp）。Von Neumann 仅有 RBMK。Eclipse/Serendipity 两者皆无
舰载反应堆。以原子计数验证，而非依名称/注释。

## 核心玩法循环

- 反应堆馈入电缆 → SMES → APC。绕过 APC 缓冲、直接取用 `cable.surplus()`
  的舰船关键系统，会在发电量 < 负载时最先降压。
- 工程师必须将反应堆保持在**安全温度/压力**之内，为其送入
  燃料+慢化剂（RBMK）或燃料气体（Stormdrive），并且（Stormdrive）要管理物理
  控制棒的磨损。失效 = 损伤舰船熔毁/爆泄。
- 两种反应堆还会产生 `GAS_NUCLEIUM`——它是 FTL 驱动塔架以及
  PDSR 护盾反应堆的燃料——因此反应堆是一项*战略性*依赖，而不仅仅是电力。
- 回合开始时状态：舰船出生时反应堆为怠速/关闭状态，必须被启动。
  Serendipity 额外以**重力发生器关闭**的状态出生
  （`_maps/map_files/Serendipity/Serendipity2.dmm:6016-6019` `on = 0, charge_count = 0`）。

## 机制

### ENG-001
断言：RBMK 通过插入第一根燃料棒启动；无需控制台即可启动。
面向玩家的后果：引擎可以物理方式"预启"；控制台只是对它进行调校。
证据：`/obj/item/fuel_rod` 上的 `attackby` 会在第一根燃料棒上调用 `start_up()`
（rbmk.dm:167-183）；`start_up()` 设置 `desired_k = 1`、开始处理并播放
启动音效（rbmk.dm:533-544）。
条件：燃料棒必须双手持握；插入时触发 `radiation_pulse`（rbmk.dm:176,182）。
例外 / 覆写：`/preset` id 为 `default_reactor_for_lazy_mappers`；`/destroyed`
子类型出生即为熔渣态；`lazy_startup()` 管理员 proc 插入 5 根燃料棒（rbmk.dm:225-229）。
置信度：HIGH（高）

### ENG-002
断言：RBMK 输出功率随温度、慢化剂混合气中燃料气体的比例、
慢化剂总摩尔数以及氧气比例而缩放——并以温度为上限。
面向玩家的后果：更多等离子 + 氧气 + 高温冷却剂 = 更多 MW，但热量是限制因素；
冷反应堆几乎不产出任何东西。
证据（rbmk.dm:297-309）：
`power = temperature / RBMK_TEMPERATURE_CRITICAL * 100`；
`power_produced = max(total_fuel_moles/total_moles * 10, 1)`；
`power_modifier = max(O2_moles/total_moles * 10, 1)`；
`last_power_produced = (power_produced*power_modifier)*total_moles`
`* (max(0,power)/100) * base_power_modifier`，`base_power_modifier = RBMK_POWER_FLAVOURISER = 8000`。
条件：`total_fuel_moles = plasma + constricted_plasma*2 + tritium*10`（rbmk.dm:303）；
需要 `moderator_input.total_moles() >= minimum_coolant_level (5)`（rbmk.dm:302）。
例外 / 覆写：仅当其所在格存在电缆节点时才会输出（rbmk.dm:314-318）。
置信度：HIGH（高）

### ENG-003
断言：RBMK 的"控制棒"是抽象的：控制台设定目标临界度 K 0–3，
而非物理棒。物理的 `/obj/item/control_rod` 物品仅属于 Stormdrive。
面向玩家的后果：RBMK 控制是一根滑条（desired K）；没有棒磨损/打印。
证据：`/obj/machinery/computer/reactor/control_rods` 的 `ui_act` 将
`reactor.desired_k` 夹取到 0–3（rbmk.dm:605-612）；K 每 tick 以最高
`control_rod_effectiveness` 的速度向 `desired_k` 收敛，并被夹取到
`RBMK_MAX_CRITICALITY = 3`（rbmk.dm:344-356）。
控制棒插入显示 = `100 - (desired_k/3*100)`（rbmk.dm:622）。
条件：`control_rod_effectiveness` 初始为 0.65，并可被 N2/CO2/pluoxium
慢化剂*提升*；等离子燃料会降低你维持 K 的能力（注释 rbmk.dm:116,319-322）。
例外 / 覆写：无。
置信度：HIGH（高）

### ENG-004
断言：RBMK 温度阈值：工作温度 640°C，临界（全舰警报）800°C，
熔毁损伤始于 900°C。
面向玩家的后果：800°C 是采取行动的点；900°C 开始摧毁舰船。
证据：定义 rbmk.dm:7-9；过热损伤在 `>= RBMK_TEMPERATURE_MELTDOWN (900)`，
警报在 `>= CRITICAL (800)`（rbmk.dm:407-414）。
条件：反应堆被冷却至 -200°C 的下限（rbmk.dm:417-419）。
例外 / 覆写：图标状态按区间变化（rbmk.dm:512-528）。
置信度：HIGH（高）

### ENG-005
断言：RBMK 必须持续接收冷却剂；若冷却剂输入 < 5 摩尔持续 >5 tick，
它将受到逐步升级的损伤。
面向玩家的后果：切断冷却剂（或失去泵）即启动熔毁倒计时，即使控制棒已插入。
证据：`minimum_coolant_level = 5`；若 `input_moles < 5` 且已加燃料：
`no_coolant_ticks++`；超过 `RBMK_NO_COOLANT_TOLERANCE = 5` 后，`temperature += temperature/500`，
`vessel_integrity -= temperature/200`，`take_damage(10)`（rbmk.dm:273-291）。
恢复 tick 的衰减速度是两倍（`no_coolant_ticks = max(0, no_coolant_ticks-2)`，rbmk.dm:283）。
条件：冷却剂 = `COOLANT_INPUT_GATE`（airs[1]）管路网；每 tick 消耗气体并
推送到输出闸门（rbmk.dm:280-281）。
例外 / 覆写：反应堆若无通电的泵则"无法推入冷却剂"
（注释 rbmk.dm:64）——即电网断电可间接使冷却剂断供（解读）。
置信度：HIGH（高）（机制），MEDIUM（中）（泵-电力关联源自注释，而非直接代码）。

### ENG-006
断言：RBMK **熔毁**（超温）会使反应堆结渣，向工程区倾倒钚污泥，
引发 EMP，并有 70% 几率过载每台舰载 APC 的照明。
面向玩家的后果：工程区变得致命/辐射，引擎无法修复（"AddComponent radioactive 15000"，
结渣机体）。
证据：`meltdown()` rbmk.dm:458-488：`slagged = TRUE`、`STOP_PROCESSING`、
放射性组件 15000、`new /obj/effect/landmark/nuclear_waste_spawner/strong`
（范围 10，custom_landmarks.dm:68-70）、`explosion(turf,0,5,10,20)`、`empulse(25,15)`、
每台舰载 APC 70% `A.overload_lighting()`（rbmk.dm:471-473）、`fail_meltdown_objective()`。
条件：在 `handle_alerts` 中于 `temperature >= 900` 且
`vessel_integrity <= temp_damage` 时触发；伤害/tick = `min(temp/100, 400/40=10)` → 从满
完整度在 900°C 下大约 40–45 秒（rbmk.dm:409-413）。
例外 / 覆写：`/destroyed` 子类型预先结渣；`vessel_integrity = 400` 初始（rbmk.dm:112）。
置信度：HIGH（高）

### ENG-007
断言：RBMK **爆泄**（超压）是比熔毁更大的爆炸，并且还会在主舰上
触发熔毁 + 核沉降天气。
面向玩家的后果：爆泄是更严重的失效；需要蓄意泄压破坏才能引发。
证据：超压条件为 `pressure >= RBMK_PRESSURE_CRITICAL = 1469.59 PSI`，
`pressure_damage = min(pressure/100, 400/45≈8.9)`，从满完整度约 45 秒（rbmk.dm:423-433）。
`blowout()`（rbmk.dm:491-502）：`explosion(turf, MAX_EX_*)` 然后 `meltdown()`；
若 `OM.role == MAIN_OVERMAP` 则运行 `SSweather.run_weather("nuclear fallout")`。
条件：默认 `MAX_EX_DEVESTATION=3, HEAVY=7, LIGHT=14, FLASH=14`
（`code/_globalvars/configuration.dm:25-32`），可被 `bombcap` 配置覆写
（`game_options.dm:377-388`，默认 14）。
例外 / 覆写：`nuclear_fallout` 天气每 tick 对无防护的生物造成 `rad_act(100)`
（stormdrive.dm:1529-1530）。
置信度：HIGH（高）

### ENG-008
断言：RBMK 慢化剂气体有明确的玩法角色：等离子/氚 = 燃料，
氧 = 功率倍率，N2/CO2/pluoxium = 控制棒加成，BZ/水蒸气/hyper-nob
= 热传递，nitryl = 燃料耗竭；其中若干会增加辐射。
面向玩家的后果：反应堆是一道大气学谜题；错误的混合气可能使其锁定为
失控状态或使工程区受辐射。
证据（rbmk.dm:303-335）：`total_fuel_moles = plasma + constricted_plasma*2 + tritium*10`；
`radioactivity_spice_multiplier += tritium/5`；控制：`N2 + CO2*2 + pluoxium*3`，
`control_bonus = total_control_moles/250`，辐射惩罚 `N2/25 + CO2/12.5`；
渗透率 `BZ + H2O*2 + hypernob*10`，`permeability_bonus/500`；
耗竭 `nitryl/15`（加到 `depletion_modifier = 0.035`）。
条件：每个分支都需要足够的摩尔数（`minimum_coolant_level`，nitryl 为其一半）。
例外 / 覆写：`K += total_fuel_moles/1000` 每 tick 直接增加临界度。
置信度：HIGH（高）

### ENG-009
断言：已加燃料且功率高于 20% 的 RBMK 会向其冷却剂输出管线
排放 `GAS_NUCLEIUM`。
面向玩家的后果：RBMK 也是一种 FTL/PDSR 燃料来源；必须将其过滤出去。
证据：当 `power >= 20` 时 `coolant_output.adjust_moles(GAS_NUCLEIUM, total_fuel_moles/20)`
（rbmk.dm:312-313）。
条件：nucleium 经由反应堆输出闸门管路网离开。
例外 / 覆写：Nucleium 是已定义的气体（`GAS_NUCLEIUM = "nucleium"`，
`code/__DEFINES/atmospherics.dm:301`）。
置信度：HIGH（高）

### ENG-010
断言：Stormdrive 是主要的玩家舰引擎；它燃烧受限等离子/
等离子/氚气体，通过粒子加速器加热启动，并物理上使用
在维修模式下安装的 `/obj/item/control_rod`。
面向玩家的后果：Stormdrive 回合需要一台 PA、控制棒、燃料气体以及一条
废气管线；其玩法与 RBMK 大相径庭。
证据：地图计数（见上表）；启动需要 PA 弹丸
（`bullet_act /obj/item/projectile/energy/accelerated_particle`：`heat += P.energy` 然后
`try_start()`，stormdrive.dm:511-516）；装棒需要 `REACTOR_STATE_MAINTENANCE`
（stormdrive.dm:190-247）。
条件：`MAX_CONTROL_RODS = 5`；舰船"设计为在回合开始时拥有 5 根可用棒和 5 根
备用棒"（头部注释 stormdrive.dm:44-45）。
例外 / 覆写：`/solgov` 子类型有 `base_power = 85000`（+26%）（stormdrive.dm:178-183）。
置信度：HIGH（高）

### ENG-011
断言：Stormdrive 点火需要既足够的燃料气体又足够的热量
（两者均 `>= start_threshold = 20`）。
面向玩家的后果：有气体但冷态的驱动毫无作用；你必须先用
PA 为其充能。
证据：`try_start()` 燃料检查 `= plasma*0.5 + constricted_plasma*1 + CO2*-0.5 +
H2O*-0.5 + tritium*1.5 + hypernob*-1`；仅当 `fuel_check >= 20 && heat >= 20` 时点火，
然后将 `heat = start_threshold + 10` 夹取以避免瞬间熔毁（stormdrive.dm:518-548）。
条件：每次 `bullet_act` 重新点火；状态必须为 IDLE。
例外 / 覆写：`lazy_startup()` 管理员 proc 安装控制棒与气体（stormdrive.dm:550-561）。
置信度：HIGH（高）

### ENG-012
断言：Stormdrive 控制棒位置由控制台按插入百分比（0–100）设定；
100% = SCRAM（紧急停堆）。
面向玩家的后果：一根滑条决定温度；"rods_5"按钮就是紧急停堆。
证据：控制台 `ui_act` 将 `reactor.control_rod_percent` 对
rods_1..rods_5 分别设为 0/5/16/33.6/100（stormdrive.dm:1366-1388）；rods_5 打印 "SCRAM protocols engaged"。
`handle_heat` 推导出 `target_heat = -1 + 2^(0.1*((100-percent)*control_rod_modifier))`
（stormdrive.dm:776-782）：percent 100 → 目标 0°C；percent 0 → 在 modifier 1 下约 1022°C。
条件：需要链接的 `reactor_id` 或多功能工具缓存传输（stormdrive.dm:1302-1359）。
例外 / 覆写：运行中物理插入/移除控制棒会造成辐射/手部断肢（见 ENG-019）。
置信度：HIGH（高）

### ENG-013
断言：Stormdrive 的反应速率与功率都随热量陡增；功率对热量是三次方关系。
面向玩家的后果：拔出控制棒会成倍提高输出，但会加速失控。
证据：`target_reaction_rate = 0.5 + 1e-3*((100-percent)*modifier)^2*rate_mod + 1e-5*heat^2`
（stormdrive.dm:785）；`input_power = (heat/150)^3 * input_power_modifier`
（stormdrive.dm:697）；`last_power_produced = base_power(67500)*input_power - nucleium_reduction`
（stormdrive.dm:698-699）。
条件：反应仅在燃料比 `>= 12.5%` 时每 tick 消耗 `reaction_rate` 摩尔；
理想为 `>= 25%` 以获得稳定性增益（stormdrive.dm:625-694）。
例外 / 覆写：`base_power` 默认 67500；`/solgov` 85000。
置信度：HIGH（高）

### ENG-014
断言：Stormdrive 温度阈值：标称 200°C，高温 400°C，临界 650°C，
熔毁 800°C（全部受混合气修正）。
面向玩家的后果：高于 650°C 时警报触发且辐射上升；800°C = 熔毁。
证据：默认值 stormdrive.dm:136-139；`check_meltdown_warning` 在 `>= critical` 时
（stormdrive.dm:1021-1035）；当 `heat > reactor_temperature_meltdown` 时熔毁
（stormdrive.dm:1151-1159）。
条件：混合气产生的 `reactor_temperature_modifier` 会移动全部四个阈值
（`handle_temperature_reinforcement`，stormdrive.dm:806-814）。
例外 / 覆写：无。
置信度：HIGH（高）

### ENG-015
断言：Stormdrive **稳定性**是与热量无关的第二条失效轴——低
稳定性会导致重力事件、热量/速率尖峰以及异常现象。
面向玩家的后果：即使在相对安全的温度下，你也可能熔毁或生成异常；
稳定性在地图上以倒计时形式显示。
证据：燃料比 < 12.5% 与超压会降低 `reactor_stability`
（stormdrive.dm:683-694）；`handle_reactor_stability` 以 `(100-stability)/3` 的几率
运行 `grav_pull()`（拉扯散落物品与生物，使其倒地），在低于 75 时轻推热量/速率，
并在 <15 时生成 `stormdrive` 异常（surge/sheer/squall）（stormdrive.dm:947-996）。
条件：`reactor_stability` 夹取在 0–100；由 `obj/effect/countdown/stormdrive` 可视化。
例外 / 覆写：子弹/blobs/异形也会降低稳定性（stormdrive.dm:314-343）。
置信度：HIGH（高）

### ENG-016
断言：Stormdrive **熔毁**有 18 秒倒计时，随后是中等爆炸、EMP、
巨大辐射脉冲、全舰击倒，以及遍布所有废气地标的污泥/异常。
面向玩家的后果：熔毁在舰体层面可幸存，但会使舰船瘫痪并
生成危害；修复需要多步骤的坑体翻新。
证据：`start_meltdown()` 烧毁控制棒、报警、`addtimer(meltdown, 18 SECONDS)`、
`reactor_end_times = TRUE`（stormdrive.dm:1191-1206）。`do_meltdown_effects()`：
`explosion(turf,0,0,10,20)`、`empulse(25,50)`、`radiation_pulse(10000,1)`、
`atmos_spawn_air("o2=750;plasma=200;nucleium=100;TEMP=5000")`、击倒全舰生物，
并对每个废气地标施加 EMP/辐射 + 异常（stormdrive.dm:1247-1287）。
条件：`meltdown()` 仅在计时结束时 `heat >= reactor_temperature_meltdown` 才触发
（因此在 18 秒内冷却可避免，stormdrive.dm:1219-1236）。
例外 / 覆写：修复循环为 MAINTENANCE→（铲除污泥）REPAIR→（25 铀合金）
REINFORCE→（焊接）REFIT→（安装 `/obj/item/stormdrive_core`）reset（stormdrive.dm:256-311）。
置信度：HIGH（高）

### ENG-017
断言：运行中/高温的 Stormdrive 会在 `heat > 400` 之后
向其废气管线产生 `GAS_NUCLEIUM`，供 FTL 塔架与 PDSR 使用。
面向玩家的后果：除非驱动确实高温，否则你无法为 FTL/护盾加注燃料。
证据：`handle_ftl_fuel_production()` 以 `heat > initial(reactor_temperature_hot)`
（400）为门控，向输出添加 `nucleium = (reaction_rate/10)*input_power_modifier`
（若废气管线加压则回流到输入端）（stormdrive.dm:789-804）。
条件：输出管线压力上限 4500 kPa；若超过，nucleium 返回输入端。
例外 / 覆写：高温 nucleium 还会损伤 FTL 废气管线（ENG-021）。
置信度：HIGH（高）

### ENG-018
断言：Stormdrive 控制棒会磨损；失效的棒会被替换为无控制作用的
惰性"受辐射"棒。
面向玩家的后果：长班次需要备用棒，并需降低控制棒插入速度以
减缓磨损；存在备用/劣质/优质控制棒。
证据：`can_cool()` 以 80% 几率按 `(input_power/75000)*deg_mod*rod_percent`
使控制棒退化；完整度 <=0 的棒被 qdel 并替换为
`/obj/item/control_rod/irradiated`（效能 0，放射性）（stormdrive.dm:735-756，
control_rods.dm:33-42）。聚合完整度低于 35% 时，会有无线电警告
（stormdrive.dm:750-752）；到 0 时反应堆"无法冷却"（update_icon 隐藏控制棒）。
条件：聚合的 `control_rod_integrity` 是已安装控制棒 `rod_integrity` 的均值。
例外 / 覆写：存在 `inferior` 棒（techfab，效能 0.8/80 耐久）、`superior`（200 耐久）、
`plasma`（效能 -0.5）（control_rods.dm:14-38）。科技网节点 `reactor_control_rods`
解锁打印劣质控制棒（2500 点，control_rods.dm:44-61）。
置信度：HIGH（高）

### ENG-019
断言：在反应堆处于 RUNNING（运行）状态时插入/移除 Stormdrive 控制棒
会使操作者受辐射，并可能使其手部断肢。
面向玩家的后果：热插拔控制棒是最后手段；戴手套或别碰。
证据：RUNNING 状态下的 `ui_act` 需要确认弹窗，造成灼伤伤害并施加
`H.radiation += (heat/2)*rad_mod` + `radiation_pulse`，且失败时断肢
（`prob(25)`，75 灼伤）（stormdrive.dm:393-443）；RUNNING 期间 `attackby` 施加
`user.radiation += 250*rad_mod` 与 EMP（stormdrive.dm:221-247）。
条件：高于手套防护的热量会完全移除减伤分支。
例外 / 覆写：维修模式（ENG-012）下更换是安全的。
置信度：HIGH（高）

### ENG-020
断言：舰船电力分配遵循 /tg/ 原版：发电机 → 电缆网 → SMES → APC →
区域通道；NSV13 增加了取用 `cable.surplus()` 且在盈余耗尽时
直接失效的**直连电缆**消费者。
面向玩家的后果：当发电量下降时，这些消费者会在其余灯光熄灭之前
无声地停止工作；它们是电网失效最先可见的迹象。
证据：`use_power_from_net()` 对电池充电器使用 `local_apc.surplus()`
（power.dm:22-33）；直连电缆消费者：shield_generator `try_use_power`（shieldgen.dm:277-283）、
drive_pylon `power_drain`（drive_pylon.dm:131-151）、能量武器 `try_use_power`
（phaser.dm:137+）、nanorepair well（nano_well.dm:117-121）、PDSR（pdsr.dm:108-120）、
电磁炮充电器（`powernet.avail - powernet.load`，railgun_forge.dm:961-971）。
条件：`cable.surplus()` / `powernet.avail - load` 必须超过抽取量。
例外 / 覆写：穿梭机获得免费电力（`!home.requires_power → return amount`，
power.dm:19-20）。
置信度：HIGH（高）

### ENG-021
断言：舰船上的停电会逐步削减 APC 通道：在无电网充电时，
电量低于 30% 时设备关闭；低于 15% 时照明+设备关闭且环境受限；
电量 0 时一切关闭。
面向玩家的后果：反应堆死亡（且 SMES 死亡）意味着没有舰载武器、没有
护盾、没有 FTL、漆黑走廊，并且（经由重力发生器）没有重力。
证据：`code/modules/power/apc.dm:1330-1348`（电量阈值 + `longtermpower` 计时器）：
`cell.charge <= 0` → 全部关闭；`<15%` → `equipment=off, lighting=off, environ` 受限；
`<30%` → `equipment=off`。APS 基础 `power_equip/power_light/power_environ` 会重置
（apc.dm:987-993）。
条件：`longtermpower` 必须已变为负值（电网未充电）。
例外 / 覆写：舰船上的 SMES 工程电池组（例如 Vago `vagodeck2.dmm:1314,11219`）
会推迟断电。
置信度：HIGH（高）

### ENG-022
断言：**护盾**是一项沉重且受限的电力负载。`shield_generator`（SolGov
实验科技）将最多 15 MW 转化为护盾 HP/通量；而 `PDSR` 护盾反应堆
则同时消耗 nucleium*与*电力。
面向玩家的后果：护盾是一项工程电力投入，而非自动生效；若电网
降压它们就会崩溃。
证据：`max_power_input = 1.5e7`（15 MW），`power_input` 由玩家设定，`flux_rate =
round((power_input/1e6)*2.5)`；失去电力会使完整度每 tick 流失 -2，并最终
`depower_shield()`（shieldgen.dm:199-311）。PDSR：`try_use_power(power_input)`，
消耗 `GAS_NUCLEIUM` `reaction_injection_rate`，需要 `min_power_input` 才能启动
（pdsr.dm:108-199）。吸收逻辑：`absorb_hit` 仅在完整度 >= 伤害且
`active` 时返回 SHIELD_ABSORB（shieldgen.dm:210-219，weapon damage.dm:24）。
条件：护盾仅覆盖链接的星图（`get_overmap()`），在 Initialize 中设定。
例外 / 覆写：AI 舰船使用轻量级 `/datum/component/overmap_shields`
组件（随时间充能，无需电力）（shieldgen.dm:402-443）。
置信度：HIGH（高）

### ENG-023
断言：**FTL** 同时需要 nucleium 气体与大量电力；驱动塔架在激活时
抽取呈指数增长的电力，并消耗 nucleium 以构建跃迁电容器。
面向玩家的后果：FTL 受反应堆限制；让塔架保持开启会浪费燃料并可能
使电网降压。
证据：塔架在激活时 `power_draw = round(power_draw*1.01 + 300, 1)`（指数式），
蓄能时 + 50 kW（drive_pylon.dm:74-127）；需要在 airs[1] 中有 `get_moles(GAS_NUCLEIUM)`；
核心 `spoolup()` 需要激活的塔架与 `use_power = 500`（drive.dm:111-139），进度来自
每个激活塔架的 `charge_rate`（drive.dm:150-162）。错过的电力需求会使
后续抽取量成倍增加，并在 `POWER_FAIL_TOLERANCE = 3` 后关机（drive_pylon.dm:4,131-151）。
条件：最多在 10 格内链接 4 个塔架；`min_pylons = 1`。
例外 / 覆写：`hugbox` 调试塔架忽略电力/气体。
置信度：HIGH（高）

### ENG-024
断言：**能量舰载武器**在充能时从电网抽取电力，并每次射击消耗一大块；
充能速率与伤害随玩家设定的功率修正而缩放（有上限）。
面向玩家的后果：发射能量武器会与护盾/FTL 争夺同一 MW 预算；低功率
= 充能缓慢/无法充能。
证据：每武器 `charge_rate` 每 tick 通过 `try_use_power(charge_rate)` 然后
`charge += charge_rate`（phaser.dm:118-134）；开火时 `charge -= charge_per_shot`
（_ship_weapon.dm:536）。数值：burst phaser 充能 330 kW / 每次射击 660 kW；beam cannon
600 kW / 4 MW，`power_modifier_cap = 5`；AMS 激光 1 MW / 3 MW；BSA 开火需 1 MW / 75 MW
（phaser.dm:16-37，laser_ams.dm:18-19，bsa.dm:7-8）。
条件：充满电或未激活时 `idle_power_usage` 强制为 0；武器还需
`powered()` 且不得处于维修状态。
例外 / 覆写：等离子发射器是气体驱动（非充能式），但操作仍需电力
（plasma_gun.dm:78-93）。
置信度：HIGH（高）

### ENG-025
断言：**重力发生器**提供全舰人造重力；它在环境通道上消耗 3000 W，
在失去电力/损坏时关闭，并可被 emag 到 2G。
面向玩家的后果：无电力/被 emag 关闭的重力发生器 = 船员漂浮、移动困难；
emag 使重力加倍并发出辐射脉冲。
证据：`active_power_usage = 3000`、`power_channel = AREA_USAGE_ENVIRON`、
`use_power = IDLE_POWER_USE` → 开启时 ACTIVE（`gravitygenerator.dm:118-122,297`）；
`set_power()` 在 `NOPOWER|BROKEN` 或断路器关闭时关闭重力（gravitygenerator.dm:282-291）；
`update_list()` 按 z 层驱动 `GLOB.gravity_generators`（gravitygenerator.dm:385-401）。
Emag：`change_setting(2)`、`radiation_pulse(src, 1800)`，必须通过多功能工具重置
（gravitygenerator_modular.dm:39-60, 4-25）。
条件：舰船地图使用 `/main/station`（`ztrait = ZTRAIT_STATION`）；Serendipity 出生时
它是关闭的（见系统概述）。
例外 / 覆写：重置需要 `can_reset_generator()`（关闭、未充能、EMAGGED）。
置信度：HIGH（高）

### ENG-026
断言：**多格电池充电器**直接从电网抽取电力（绕过 APC 电池
缓冲）为最多 4 个电池充能，速率由电容器等级决定（250 W × 额定值，上限
4000 W）。
面向玩家的后果：为成组的武器电池充电是真实的电网负载，会在
盈余耗尽时停滞。
证据：`max_batteries = 4`、`charge_rate_base = 250`、`charge_rate_max = 4000`、
`RefreshParts` 累加电容器额定值（multi_cell_charger.dm:12-16,104-112）；
`process` 对每个电池调用 `use_power_from_net(charge_rate*delta_time, take_any=TRUE)`，
并为自身调用 `use_power(charge_rate/100)`（multi_cell_charger.dm:86-99）。
条件：需要区域内有 APC（`a.power_equip != 0`）、已锚定、非 BROKEN/NOPOWER。
例外 / 覆写：`take_any = TRUE` 意味着接受部分抽取（power.dm:28-31）。
置信度：HIGH（高）

### ENG-027
断言：操作者反馈主要是警报/声音/控制台，而非反应堆本体。
面向玩家的后果：工程师依赖无线电、星图警报中继与监视器——
一个"沉默"的反应堆很容易被忽视，直到灯光闪烁。
证据：RBMK `handle_alerts` 在 `CHANNEL_REACTOR_ALERT` 循环播放 `alarm.ogg`、
将其灯变为红色、最多每 30 秒重新报警一次（rbmk.dm:434-454）；`power>=90` 每 1.5 分钟
使舰灯闪烁一次（rbmk.dm:364-368）；熔毁播放 `meltdown.ogg`。Stormdrive
`send_alert` 以 20 秒冷却向工程频道发送无线电（stormdrive.dm:1008-1019）；
`start_meltdown` 中继 `meltdown.ogg`。两者都有 NTOS 监视程序，带分级的
`smmon_[0-6]` 状态图标（rbmk.dm:810-845；stormdrive.dm:1810-1834）。
条件：RBMK 警报需要链接的星图（`get_overmap()`），否则无中继。
例外 / 覆写：`CONFIG_GET(flag/allow_crew_objectives)` 门控船员熔毁
目标失败（rbmk.dm:504-510；stormdrive.dm:1238-1245）。
置信度：HIGH（高）

### ENG-028
断言：两种反应堆都是持续辐射源，随温度/燃料以及气体
混合而缩放；失效的控制棒与废气会添加更多辐射。
面向玩家的后果：工程区是辐射区；即使标称状态下 PPE 也很重要。
证据：RBMK 每 tick `radiation_pulse(src, temperature*radioactivity_spice_multiplier)`
（rbmk.dm:363）；spice 倍率由氚/N2/CO2 提高（rbmk.dm:310,323-324）。
Stormdrive `radiation_pulse(src, heat*radiation_modifier, 2)`（stormdrive.dm:708）；
熔毁脉冲 10000（ENG-016）。`/obj/item/fuel_rod` 携带放射性组件
350（rbmk.dm:560）；`/control_rod/irradiated` 500（control_rods.dm:41-42）。
条件：站在反应堆上/旁边会加热身体（rbmk.dm:246-251,369-373）。
例外 / 覆写：`nuclear fallout` 天气每 tick `rad_act(100)`（stormdrive.dm:1529）。
置信度：HIGH（高）

## 反应堆对比

| 反应堆 | 燃料 | 功率输出 | 失效模式 | 操作者动作 |
|---|---|---|---|---|
| **RBMK / AGCNR**（三元气冷） | 铀/钚/telecrystal/bananium 燃料棒（最多 5）+ 等离子/氚慢化剂；O2 功率增强剂 | `(fuel_frac*O2_frac*total_moles)*(temp/800)*(8000)`；需要电缆节点 | 熔毁 ≥900°C → 结渣 + 钚污泥 + EMP + 70% APC 照明过载；爆泄 ≥1469.59 psi → MAX_EX 爆炸 + 沉降天气 + 熔毁 | 插入燃料棒（≤20% 功率）、设定目标 K 0–3、送入冷却剂（≥5 mol）+ 慢化剂混合气、过滤 nucleium、控制棒/泵/状态控制台 |
| **Stormdrive**（二元） | 受限等离子（最佳）/等离子/氚气体；O2/nucleium 增强 | `67500*((heat/150)^3)*input_power_mod` | 熔毁 ≥800°C（18 秒倒计时）→ 爆炸 + EMP + 辐射 + 污泥/异常；低**稳定性** → 重力事件/异常；控制棒磨损 | 安装/移除控制棒（仅维修时）、设定棒 %（0–100，100=SCRAM）、送入燃料+废气管线、用 PA 加热启动（燃料 ≥20 且热量 ≥20）、冷却控制棒 |
| **特斯拉**（仅 Serendipity） | 特斯拉发生器 + 线圈（/tg/ 原版） | 线圈发电 | 特斯拉分层崩解（/tg/ 原版） | 标准 /tg/ 特斯拉布置 |
| **无（PACMAN 便携式）** | 焊接燃料/等离子便携机 | 小 | 无引擎风险 | 加注燃料并开关便携机（Eclipse） |

解读：RBMK 是一台缓慢、连续的"保持在范围内"装置（温度是通过气体冷却实现的
节流阀），而 Stormdrive 是一台易产生尖峰的装置，其控制棒插入直接设定
目标温度并产出 FTL 燃料。

## 跨系统依赖

- **反应堆 → FTL（系统 4）。** 两种反应堆都排放 `GAS_NUCLEIUM`；FTL 驱动塔架
  消耗 nucleium + 电力（ENG-009，ENG-017，ENG-023）。没有高温反应堆 = 没有 FTL 燃料。
- **反应堆 → 护盾（系统 12）。** `shield_generator` 与 `PDSR` 是直连电缆 /
  nucleium 消费者（ENG-022）；两者也为星图设定 `OM.shields`。
- **反应堆 → 武器（系统 9/10）。** 能量武器（phaser、AMS、BSA）从
  电网充能（ENG-024）；混合电磁炮电容器与电磁炮锻炉也抽取电力
  （`railgun_forge.dm:961-971`，`hybrid_railgun.dm:55-59`）。
- **反应堆 → 装甲修复。** `armour_plating_nanorepair_well` 从电缆抽取最多 3 MW
  （超频至 30 MW）以修复舰体装甲板（nano_well.dm:32-67,117-128）。
- **反应堆 → 重力/火灾/生命维持。** 重力（ENG-025）以及所有 APC 供电子系统
  （门、空气警报器等）随电网一同死亡（ENG-021）。
- **大气 ↔ 反应堆。** 两种反应堆都是大气机械；冷却剂/慢化剂/废气
  气体路由、过滤器（`constricted_plasma` 过滤器）以及磁性压缩器
  （`constrictor.dm`：等离子 → 受限等离子；emag 将受限等离子倾泻到
  所在格，抽取 200 W）都是大气工程（系统 14）。
- **熔毁 → 损管（系统 15）。** 钚污泥贴花 + `radiation_pulse`
  生成工程师必须清理/躲避的危害。
- **船员目标（系统 6）。** 若引擎熔毁，`datum/objective/crew/meltdown` 失败；
  `power_generation` 需要持续输出（engineering_objectives.dm:36-77）。
  （注：`power_generation` 的目标功率使用 `base_target_power * rand(60,90)`——
  可能是个 bug，但这就是代码所写。）

## 开放问题

- 任一时刻哪艘舰船"在轮换中"由服务器设定（地图投票读取 `engine_stats`
  黑匣子，`map_vote.dm`）；反应堆-舰船对照表是按地图而非按回合的。
- RBMK 的"无电力 ⇒ 无冷却剂"关联是真的吗？注释（rbmk.dm:64）暗示
  泵的电力有关系，但反应堆本身没有 `use_power`；只有管路网泵有。
  未直接追踪舰船泵是否位于通电的 APC 电路上。
- 特斯拉/奇点/超物质（/tg/ 原版）的完整路径此处未追踪——超出反应堆范围，
  仅注明 Serendipity 使用特斯拉引擎。
- `power_generation` 目标的算术似乎不正确（见跨系统）；需在专项梳理中
  确认意图与行为。
- Eclipse 的确切便携式发电机布局是从其地图 JSON 的
  `equipment` 列表推断的，而非从 `.dmm` 中枚举。
