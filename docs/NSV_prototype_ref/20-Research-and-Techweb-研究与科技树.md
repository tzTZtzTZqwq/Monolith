> 本文为 research/evidence/research_techweb.md 的中文翻译。

# 研究与科技树

## 系统概览

在 NSV 舰船上，"Science"/R&D 通过由 `SSresearch`（`code/controllers/subsystem/research.dm`）持有的**全局科技树（techweb）**运作。存在一个共享的 `/datum/techweb/science`（`SSresearch.science_tech`），每个 R&D 控制台、天体测量计算机、发现扫描器和制造机都从它读取。船员获得的研究点存储在那单个 datum 上；解锁一个节点会消耗点数，并使其 `design_ids` 可在车床/铭印机上打印。

NSV 在继承的 `/tg/` 风格节点树（`code/modules/research/techweb/all_nodes.dm`）之上添加了自己的节点（`nsv13/.../techweb/all_nsv_nodes.dm` + 分散的文件）。NSV 节点门控：舰船/战斗机部件、弹药与鱼雷部件、弹药与 CIC 电路板、护盾发生器部件、小行星（deepcore）升级，以及少数工程机器（阻尼器、stormdrive、控制棒）。

存在两种门控方式：
1. **研究门控** —— 一个节点消耗点数；一旦研究，其设计即可打印。
2. **磁盘门控 / 预装** —— 某些昂贵物品（舰船护盾、船体武器）**不**通过普通研究获得。要么它们已经映射到舰船上，要么它们以一张物理设计磁盘到来，该磁盘直接将设计注入科技树，绕过节点。

## 核心玩法循环

1. 起始：`science_tech` 在子系统初始化时被注入 **2500 Discovery Research** 点（`research.dm:55`）。舰船地图上的每台已制造机器也都预装好；回合开始时无需研究即可运作。
2. 被动收入：舰船上的 `/obj/machinery/rnd/server` 持续产生 General Research（`research.dm:62-82`）。舰船地图携带 1–2 台 RND 服务器（例如 `Galactica2.dmm`、`atlas.dmm` = 1；`Aetherwhisp2.dmm`、`vnmk3.dmm` = 2），因此一艘战舰即便什么都不做也有一条缓慢的涓流。
3. 主动收入：
   - **天体测量（Astrometrics）** 对星系/异常体的扫描（Discovery），以及将探针鱼雷射入异常体（General + 专家类型）。
   - `/datum/component/discoverable` 对象上的**发现扫描器**（Discovery）。
   - 拆解 / 破坏性分析仪加成、实验器、特斯拉线圈、气体反应等（继承来的来源）。
4. 在 R&D 控制台花费点数解锁节点 → 设计可在部门 protolathe/techfab、电路铭印机、exofab、复制器、肢体培育器或熔炉中打印。
5. 船员将这些设计转化为舰船/战斗机/弹药部件，以维持战舰作战。在硬核模式（hardmode）下，会额外增加一套*可建造的舰炮*。

## 机制

### RES-001
断言：舰船上的所有 R&D 共享一个科技树；点数是全局的，而非每个控制台各自独立。
面向玩家的后果：科学家和天体测量操作员都向同一个池子供养；任何控制台都可以花费任何人赚取的点数。
证据：`ssresearch.science_tech = new /datum/techweb/science`（`research.dm:46`）；
天体测量 `linked_techweb = SSresearch.science_tech`（`astrometrics.dm:34`）；
`/datum/techweb/science` 是全局 R&D 树（`_techweb.dm:65-67`）。
条件：始终（单分片）。
置信度：HIGH（高）

### RES-002
断言：节点研究成本 = 其列出的 `research_costs` 加上一笔额外的 **Discovery Research** 成本，该成本随节点高出科技树当前层级（tier）的程度而增加。
面向玩家的后果：你不能便宜地跳到高层级节点；必须逐层攀升（通过研究足够多的节点），且 Discovery 点是与 General 不同的独立资源。
证据：`get_price()` 添加 `actual_costs[TECHWEB_POINT_TYPE_DISCOVERY] =
calculate_discovery_cost(host.current_tier)`（`_techweb_node.dm:85-110`）；
差值 0/负 → 0，1 → 1000，2 → 2500，3 → 5000，≥4 → 10000。
置信度：HIGH（高）

### RES-003
断言：当某一层级 ≥20% 的节点已被研究时，当前层级推进。
面向玩家的后果：磨完每一层级节点的一小部分即可稳定解锁下一层级的 Discovery 折扣；稀疏的科技树会拖慢进程。
证据：`TIER_PROPORTATION_TO_UNLOCK 0.2`；`calculate_current_tier()` 在
`researched_amount >= total_amount * 0.2` 时设置 `current_tier = tier`
（`_techweb.dm:2-3, 116-124`）。
置信度：HIGH（高）

### RES-004
断言：节点仅在其所有 `prereq_ids` 都被研究后才可研究；隐藏节点在正常游玩中永远不会变得可用。
面向玩家的后果：前置链必须按顺序完成；一个 `hidden` 节点（如护盾节点）无法从树中点击进入。
证据：`update_node_status()` 仅在 `needed == 0` 时设置为可用，并在
`hidden_nodes[node.id]` 时提前返回（`_techweb.dm:313-345`）；
`research_node()` 除非 `available_nodes[node.id]` 否则拒绝（`_techweb.dm:256-258`）。
置信度：HIGH（高）

### RES-005
断言：节点上的 `export_price` 是**货物出口信用点价值**，而非玩家研究收入。
面向玩家的后果：向货物出售科技产出信用点，而不是研究点。
证据：该字段被文档标注为 "Cargo export price"（`_techweb_node.dm:17`）；NSV
节点设置了它（例如 `all_nsv_nodes.dm`）。没有代码路径将其加入 `research_points`。
置信度：MEDIUM（中）（字段用途已说明；此处未追踪完整的货物出售路径）

### RES-006
断言：天体测量可以扫描一个**星系**（15 秒）以揭示其异常体，然后扫描特定的**异常体**（2 分钟）以获得部分研究收益。
面向玩家的后果：控制台列出异常体名称 / 描述 / 点数值 / 可扫描标志；星系扫描是廉价的侦察步骤，异常体扫描则存入 Discovery 并将其标记为已扫描。
证据：`scan_goal_system = 15 SECONDS`、`scan_goal_anomaly = 2 MINUTES`
（`astrometrics.dm:18-20`）；范围 40 光年且同一星区（`astrometrics.dm:16,81-82`）；
`finish_scan()` 以 `TECHWEB_POINT_TYPE_DISCOVERY` 形式奖励 `research_points * 0.5`
并减半异常体的剩余点数（`astrometrics.dm:137-151`）；
`get_info()` 暴露名称/描述/点数/可扫描（`starsystem.dm:606-617`）。
置信度：HIGH（高）

### RES-007
断言：用**探针鱼雷**击中异常体所获得的收益远超天体扫描：将剩余点数的 150% 作为 General，外加该异常体的专家类型。
面向玩家的后果：虫洞支付 Wormhole Research（FTL 滑流节点所需）；黑洞/恒星支付 General。探测会发出全船范围的响亮公告（"WAYFARER subsystem"）。
证据：`/obj/item/projectile/bullet/torpedo/probe` 上的 `on_entered()` 添加
`research_points*1.5` General + `specialist_research_type` 数量，然后清零
点数（`starsystem.dm:641-654`）；虫洞 `specialist_research_type =
TECHWEB_POINT_TYPE_WORMHOLE`（`starsystem.dm:666`）。
条件：需要一枚探针弹头鱼雷（见 RES-011）。在天体扫描之后探测将获得*减半后*剩余量的 150%。
置信度：HIGH（高）

### RES-008
断言：异常体扫描事件还通过信号驱动跨图层 "Scan anomalies" 任务目标，独立于研究收益。
面向玩家的后果：扫描所需的异常体类型会推进目标进度；探测/扫描都会计入 `ship.scanned`。
证据：`finish_scan()` 执行 `linked.scanned += scan_target` 和
`SEND_SIGNAL(linked, COMSIG_ANOMALY_SCANNED)`（`astrometrics.dm:141-142`）；
`/datum/overmap_objective/scan` 监听 `COMSIG_ANOMALY_SCANNED` 并统计
`count_by_type(...scanned, anomaly_type)`（`objectives/scan.dm:35-44`）。
置信度：HIGH（高）

### RES-009
断言：在 R&D 控制台上传的**设计磁盘**会将其设计作为*自定义设计*直接注入 —— 它**不**经过节点研究，也不会解锁节点。
面向玩家的后果：SolGov Shielding 磁盘零研究即交付全部 5 个护盾设计，尽管对应节点是隐藏/不可达的。
证据：`uploadDisk` → `stored_research.add_design(D, TRUE)`（custom=TRUE）
（`rdconsole.dm:449-456`）；`add_design(...,custom)` 将 id 放入 `researched_designs`
和 `custom_designs`（`_techweb.dm:215-221`）；磁盘内容在
`/obj/item/disk/design_disk/overmap_shields/Initialize` 中构建（`shieldgen.dm:25-42`）。
置信度：HIGH（高）

### RES-010
断言：NSV 护盾发生器节点 `ship_shield_tech` 是 `hidden = TRUE` 且没有 boost 路径，因此实际上无法通过普通研究触达；预期路径是获取 SolGov 磁盘。
面向玩家的后果：船员无法从树中 "研究护盾"；他们必须购买/找到磁盘（商人：100000 信用点，库存 1）或拾取地图副本。
证据：`ship_shield_tech` 为 hidden（`all_nsv_nodes.dm:2-10`）；无
`boost_item_paths`；商人条目价格 100000（`traders_items.dm:362-367`）；
磁盘预置于 `vonneumann/vnmk3.dmm`、`aetherwhisp/Aetherwhisp2.dmm`。
置信度：MEDIUM（中）（未找到其他揭示路径，但未穷尽排除）

### RES-011
断言：探针鱼雷需要 "Guided Munitions II" 中的 `probe_warhead` 设计（标记为 Science）。
面向玩家的后果：完整的异常体收益被锁定在鱼雷研究 + 将探针弹头构建进鱼雷外壳之后。
证据：`probe_warhead` 位于节点 `advanced_torpedo_components` =
"Guided Munitions II"，`prereq_ids = basic_torpedo_components, exotic_ammo`，
5000 点，tier 4（`all_nsv_nodes.dm:43-51`）；`probe_warhead` 设计 build_path
为 `/obj/item/ship_weapon/parts/missile/warhead/probe`，`departmental_flags = SCIENCE`
（`munitions_designs.dm:159-167`）。
置信度：HIGH（高）

### RES-012
断言：在正常游玩中，**可建造的**舰炮（甲板炮、高斯炮塔、VLS 发射管、发射电子设备、高斯分配器、三管升级）**不**在任何科技树节点中；它们仅在**硬核模式**被开启时才会被添加。
面向玩家的后果：在标准回合中你无法研究/打印这些舰炮 —— 舰船的武器是预装的，你负责修理/装填它们。硬核模式（"Dolos Assault"）使它们可在其节点上建造。
证据：`gun_techdesigns` 在 `_techweb.dm:7-15` 中映射这些 id → 节点；
`hardmode_tech_enable()` 调用 `on_design_addition`（`_techweb.dm:18-24`）；
由 `hardmode.dm:25` 中的 `toggle_hardmode()` 调用；默认
`hard_mode_enabled = FALSE`（`overmap_mode.dm:45`）。
条件：硬核模式由管理员/配置切换（`overmap_mode.dm:692-695`）。
置信度：HIGH（高）

### RES-013
断言：能量舰船武器（光子/相位炮、BSA、激光 AMS、激光 PD）**不**受研究门控 —— 它们没有设计；它们是预装的，且受反应堆功率而非科技树限制。
面向玩家的后果：你永远不会通过研究解锁 "能量武器"；你已经拥有它们（如果已映射）并必须为其供给电荷。
证据：`nsv13` 设计中不存在 phaser/bsa/ams/laser_pd 的 `/datum/design`；
武器是 `/obj/machinery/ship_weapon/energy/...` 结构
（`energy_weapons/*.dm`）；基础储物柜刻意移除了能量枪
（"NSV13 No energy weapons"）。发射取决于电荷/功率，而非研究。
置信度：MEDIUM（中）（基于设计的缺失；与工程笔记一致）

### RES-014
断言：军官餐厅可以用两个廉价的早期节点重建 CIC/弹药控制台（二者 `prereq = comptech`，tier 1，2000 点）。
面向玩家的后果：在一次轰炸之后，Science 无需深入研究即可恢复舵轮/导航/战术/天体测量/dradis 电路板（Ship Computer Circuitry）以及战斗机/军械/弹药/AMS 电路板（Munitions Computer Circuitry）。
证据：`ship_circuitry` 设计 helm/nav/tactical/astrometrics/dradis/cargo dradis
（`all_nsv_nodes.dm:12-21`）；`maa_circuits` 设计 fighter/ordnance/fighter-launcher/
ammo-sorter/munitions/AMS 控制台（`all_nsv_nodes.dm:23-31`）。
置信度：HIGH（高）

### RES-015
断言：小行星（"deepcore"）升级受研究门控：两个节点门控采矿舰捕获更丰富小行星的能力。
面向玩家的后果：要获得钻石/铀/蓝空间，Cargo/Science 必须研究 `mineral_nonferrous`（7500）再研究 `mineral_exotic`（12500）以打印 arrestor/dradis 升级。
证据：`mineral_nonferrous`（prereq base）→ `deepcore1`、`asteroidscanner`，
7500（`asteroid.dm:5-13`）；`mineral_exotic`（prereq mineral_nonferrous）→
`deepcore2`、`asteroidscanner2`，12500（`asteroid.dm:15-22`）；设计 build path
`/obj/item/deepcore_upgrade`（+ `/max`）（`asteroid.dm:23-40`）。
置信度：HIGH（高）

### RES-016
断言：NSV 添加了一个 **Munitions** 部门 protolathe/techfab，其 ROM 标志允许它打印 MUNITIONS 标志的设计；原版 protolathe（`allowed =
DEPARTMENTAL_FLAG_ALL` = BYOND ALL）可打印任何东西。
面向玩家的后果：弹药人员可以在自己的车床上自助获取舰船弹药/舰炮部件；其他部门车床无法打印仅限 MUNITIONS 的设计，除非该设计同时也为其或 ALL 打了标志。
证据：`/protolathe/department/munitions` `allowed_department_flags =
DEPARTMENTAL_FLAG_ALL|DEPARTMENTAL_FLAG_MUNITIONS`（`departmental_protolathe.dm:1-11`）；
打印测试 `d.departmental_flags & allowed_department_flags`
（`machinery/_production.dm:87,96,317`）；许多弹药设计仅标记为
`DEPARTMENTAL_FLAG_MUNITIONS`（`munitions_designs.dm`）。
置信度：HIGH（高）

### RES-017
断言：复制器（食物）配方由其自身自动解锁的科技树解锁，按用生物质购买的 Pattern Upgrade 磁盘分层，而非由科学点解锁。
面向玩家的后果：厨师/服务独立于 R&D 推进复制器。
证据：`/datum/techweb/specialized/autounlocking/replicator ... buildtypes =
REPLICATOR`（nsv13 中的 `_techweb.dm:1-2`）；tier2/3/4 磁盘的 `cost` 为 2000/3000/4000
生物质（`replicator_designs.dm:69-76,168-175,242-249`）。
置信度：HIGH（高）

## 关键科技树节点

| 节点（id） | 显示名 | 层级 | 成本（点） | 前置 | 门控内容 |
|---|---|---|---|---|---|
| ship_circuitry | Ship computer circuitry | 1 | 2000 G | comptech | helm、nav、tactical、astrometrics、dradis、cargo-dradis 电路板 |
| maa_circuitry | Munitions computer circuitry | 1 | 2000 G | comptech | fighter/ordnance/launcher/ammo-sorter/munitions/AMS 控制台电路板 |
| ship_shield_tech | Experimental Shield Technology | 5 | 1000 G | 无（隐藏） | shield fan/capacitor/modulator/interface/frame（经由磁盘触达） |
| fighter_fabrication | Fighter Construction | 1 | 1000 G | base | 轻型/重型/通用机身 |
| fighter_tier1 | Standard Fighter Parts | 2 | 2000 G | fighter_fabrication | 油箱、航电、apu、装甲、引擎、对接计算机、电池等 |
| fighter_tier2 | Advanced Fighter Parts | 3 | 5000 G | fighter_tier1, adv_engi | 升级版战斗机部件 |
| fighter_tier3 | Experimental Fighter Parts | 4 | 7500 G | fighter_tier2, bluespace_travel, bluespace_power | 实验性战斗机部件 |
| fightergun1 | Standard Fighter Weapons | 2 | 2000 G | fighter_tier1, weaponry | 鱼雷挂架、导弹挂架、轻型炮 |
| fightergun2 | Advanced Fighter Weapons | 3 | 5000 G | fightergun1, ballistic_weapons | 升级挂架、重型炮 |
| fightergun3 | Experimental Fighter Weapons | 4 | 5000 G | fightergun2, adv_weaponry | 实验性发射器 |
| fightermining | Aircraft Mining Equipment | 3 | 1500 G | fighter_tier1, adv_mining, adv_plasma | 机载等离子切割器（r_cutter） |
| countermeasure_charge | Countermeasure Charge Fab | 2 | 1500 G | fighter_tier1 | 对抗措施三装药 |
| basic_torpedo_components | Guided Munitions I | 3 | 1500 G | explosive_weapons | 弹头、导弹弹头、诱饵、货运、制导、推进、IFF |
| advanced_torpedo_components | Guided Munitions II | 4 | 5000 G | basic_torpedo_components, exotic_ammo | 穿甲爆破弹、地狱火、探针弹头 |
| prototype_disruption_warheads | Disruption Warhead Prototype | 4 | 6500 G | advanced_torpedo_components, emp_adv | 原型 EMP 干扰弹头 |
| adv_ballistics | Advanced Ballistics | 3 | 5000 G | ballistic_weapons | 海军炮弹、药包、甲板炮 core/powder/payload 电路板、舷侧部件、等离子核心、擦炮刷 |
| macro_ballistics | Macro-Ballistics | 4 | 7500 G | adv_ballistics, adv_plasma | AP 弹、等离子助燃剂、甲板炮 autorepair/autoelevator、高斯架、铀舷侧 |
| missile_automation | Automated Missile Construction | 3 | 2500 G | basic_torpedo_components, high_efficiency | 导弹 autowrencher/welder/screwer/wirer/assembler、慢速传送带 |
| cyborg_upg_muni | Cyborg Upgrades: Munitions | 3 | 5000 G | adv_robotics, adv_engi, adv_ballistics | 弹药机械臂博格升级 |
| vehicle_start/utility/tier1/2/3 | Mechanical/Vehicle Research | 1–3 | 1500/2000/2500/3500/5000 G | engineering → | 拖船/载具引擎、轮胎、货箱装载器、低温静滞 |
| rld (tool_designs.dm) | Advanced Lamp Construction | 0 | 1000 G | janitor | Rapid Light Dispenser |
| autoinjector | Autoinjector Medipens | 4 | 1500 G | adv_biotech, adv_surgery | 自动注射器打印板 |
| xenoorgan_bio | Xeno-organ Biology | 1 | 6500 G | adv_biotech | 猫人/蜥蜴人/等离子人/以太人/蛾人/蜂人肢体磁盘 |
| mineral_nonferrous | Polytrinic asteroid mining equipment | 0 | 7500 G | base | deepcore1 arrestor、asteroidscanner |
| mineral_exotic | Phasic asteroid mining equipment | 0 | 12500 G | mineral_nonferrous | deepcore2 arrestor、asteroidscanner2 |
| ftl_slipstream | Quantum slipstream technology | 0 | **5000 Wormhole** | comptech | 滑流芯片（需要来自虫洞探测的 Wormhole Research） |

G = `TECHWEB_POINT_TYPE_GENERIC`（"General Research"）。成本为所列的
`research_costs`；有效价格还会按 RES-002 加上一笔 Discovery 附加费。
层级为 0 的节点 = tech_tier 未设置（默认 0）。

### 仅硬核模式的舰炮设计（RES-012）
`gauss_dispenser_circuit`、`gauss_turret`、`ship_firing_electronics`、`deck_gun`
→ `advanced_ballistics`；`deck_gun_triple` → `macro_ballistics`；`vls_tube` →
`basic_torpedo_components`。仅由 `hardmode_tech_enable()` 添加。

## 设计分类 -> 生产机器

- `fighter_designs.dm` —— 机身 + 战斗机部件/武器。**Protolathe**
  （`build_type = PROTOLATHE`），分类 "Ship Components"；标志 CARGO/MUNITIONS/
  ENGINEERING（部分为仅 MUNITIONS 武器）。
- `munitions_designs.dm` —— 鱼雷/导弹部件、弹头、海军炮弹、火药、
  舷侧部件、等离子核心、导弹工厂机器。**Protolathe**，部分
  也 **Imprinter**（`PROTOLATHE|IMPRINTER`）/ **Autolathe**。标志 MUNITIONS。
- `shield_designs.dm` —— shield fan/capacitor/modulator/interface/frame。
  **Protolathe**，标志 ENGINEERING|SCIENCE。
- `ship_weapon_designs.dm` —— 舰炮/控制台电路板、AA/HAA 炮塔、甲板炮 core/
  gates、VLS、AMS、高斯。电路板类型 → **Imprinter**（许多覆写
  `PROTOLATHE|IMPRINTER`）。
- `nsv_circuitboard_designs.dm` —— fighter/munitions/nav/dradis/helm/tactical/
  astrometrics 电路板。**Imprinter**（父类 `/datum/design/board`，`imprinter`
  `comp_board_designs.dm:3-6`），少数为 PROTOLATHE。
- `tool_designs.dm` —— RLD。**Protolathe**。
- `smelting_designs.dm` —— 纳米碳玻璃、德律钢、德兰石合金。
  **SMELTER | PROTOLATHE**，分类 "Stock Parts"。
- `mechfabricator_designs.dm` —— 博格升级（弹药）、机甲泡沫船体修复
  设备。**MECHFAB**（外骨骼制造机）。
- `limbgrower_designs.dm` —— 肢体/器官 + `Limb Design Disk`。**LIMBGROWER**。
- `replicator_designs.dm` —— 食物配方 + Pattern Upgrade 磁盘。**REPLICATOR**。
- `misc_designs.dm` —— 咖啡机物品，标志 SERVICE。

## 天体测量细节

`/obj/machinery/computer/ship/navigation/astrometrics`（`astrometrics.dm`）：
- 需要 `ACCESS_RESEARCH`；电路 `astrometrics_console`（来自 Ship Computer
  Circuitry）。
- 仅在 40 光年内且**同一星区**扫描（`is_in_range`）。
- 动作：`scan`（星系，15 秒）、`scan_anomaly`（2 分钟）、`cancel_scan`、`info`
  （将异常体描述打印到聊天）、`broadcast` 切换（还会在
  Science 无线电耳机频道上公告）。
- `finish_scan()` 将目标加入 `linked.scanned`，发出
  `COMSIG_ANOMALY_SCANNED`，并对异常体以 Discovery 形式支付 `research_points` 的 50%。
- 探针载荷是一个独立的弹药物品（RES-007/011）。

## 跨系统依赖 / Cross-System Dependencies

- **护盾（系统 12）。** 实践中不受研究门控：`ship_shield_tech` 是
  隐藏的；护盾部件来自 SolGov Shielding 磁盘（商人 100000
  信用点，或地图拾取）。建造发生器随后要争夺反应堆功率
  （`shieldgen.dm`，工程笔记）。RES-009/010。
- **弹药 / 舰船武器（系统 10）。** 研究门控弹药、弹头、
  鱼雷部件、导弹工厂机器，以及（仅硬核模式）实际的舰炮框架。
  依赖 Cargo（来自小行星/贸易的材料）和 Engineering（电力）。
  RES-011/012。
- **战斗机（系统 ~）。** 每个战斗机层级是一个研究节点；战斗机控制台
  来自 `maa_circuits`。RES-014。
- **Deepcore 采矿（系统 18）。** `mineral_nonferrous` → `mineral_exotic` 门控
  小行星捕获升级；将材料回馈到车床。RES-015。
- **FTL（系统）。** `ftl_slipstream` 需要 Wormhole Research，只能
  通过探测虫洞异常体（天体测量 + 探针鱼雷）获得。RES-007。
- **任务。** 天体测量异常体扫描满足 "Scan anomalies" 目标。
  RES-008。
- **货物/贸易。** 设计磁盘（护盾、甲板炮 autorepair/autoelevator）是
  商人库存；`export_price` 将科技与货物价值绑定。RES-005/009/010。

## 未解问题 / Open Questions

- 除了磁盘的自定义设计注入之外，是否还有任何路径（事件、管理员、其他磁盘）解除 `ship_shield_tech` 的隐藏状态？未找到；假定没有。
- NSV 上 RND 服务器每秒的确切收入（基础公式已追踪，但未追踪
  服务器 `mine()` 的数值产出；舰船携带 1–2 台服务器）。
- NSV 舰船地图是否包含供发现扫描器使用的 `/datum/component/discoverable` 对象，
  还是该扫描器仅在船外/遗迹中有用？
- `export_price` 是否已接入 NSV 节点的实时货物出售交易
  （字段已定义并设置；出售消费方未追踪）。
- 位于 `all_nsv_nodes.dm` 之外的全部 NSV 节点列表（asteroid.dm、
  ftl_jump.dm、stormdrive.dm、control_rods.dm、inertital_dampener.dm）—— 已
  捕获与研究相关的那些；更深的工程节点留待工程部分处理。
