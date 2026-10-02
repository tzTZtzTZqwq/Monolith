> 本文为 research/evidence/medical.md 的中文翻译。

# 医疗、外科与船员生存

事实来源：`D:\code\NSV13` 当前的 NSV13 检出。所有行引用均直接追踪。
范围：让战舰船员存活 —— NSV 特有的医疗舱硬件（autodoc、器官培育器、
可再填充化学分配器、自动注射器打印机、自动注射器）、NSV 试剂、物种生存
特性，以及舰船战斗伤员如何到达医疗舱。基础 `/tg/` 医疗（克隆、除颤、合成
肉、合成修复化学品）为交叉引用，不重新推导。
交叉引用：
- `research/evidence/damage_control.md` —— 跨图层命中如何将伤害注入舰船内部（DAMCTRL-002 载荷）。
- `research/evidence/ship_combat.md` / `hull_shields.md` —— 武器命中如何结算（COMBAT-024、HULL-*）。
- `research/evidence/boarding.md` —— 陆战队/登舰者及其武器（系统 17）。
- `research/evidence/engineering_power.md` —— 反应堆/PDSR/惯性阻尼器作为辐射源（系统 13）。

## 系统概览

NSV13 在未改动的 `/tg/` 医学之上添加了一套小巧的、硬点风格的医疗套件。设计意图
是一艘*战舰*：补给有限、空间狭小，且船员包含合成体（IPC/登舰机器人），
他们需要的 "药物" 与有机体不同。

三个理念驱动了 NSV 的增补：
1. **替换部件，而非人。** 器官培育器用合成肉制造肢体/器官；
   autodoc 负责安装它们。二者合力在小医疗舱中替代了完整的外科手术套件。
2. **有限的化学。** 可再填充化学分配器*储存*化学品（不像基础
   分配器那般凭空变出）；自动注射器打印机把储存的化学品变为单次使用的自动注射器。
3. **合成体也是病人。** 一条并行的化学品线（system cleaner、liquid solder、放射性
   消毒剂、Highjack）治疗机器船员，且 IPC 的辐射伤害被重写为脑创伤。

其他一切 —— 克隆、除颤、钝击/灼伤/毒素医学、手术步骤 —— 都是基础内容，NSV 不
覆写它，除了两处单行的玩法差异（除颤失忆消息；IPC 辐射处理）。

## 核心玩法循环

1. 跨图层战斗 / 登舰 / 反应堆事故使船员受伤（见下文 "致伤途径"）：爆炸 →
   钝击/灼伤，减压 → 缺氧，火灾/等离子 → 灼伤+中毒，辐射 → 毒素（有机体）或脑创伤（IPC）。
2. 医护兵用基础工具分诊：健康分析仪、瘀伤包/软膏、合成肉贴片、医疗笔、
   除颤器，以及（如果已映射）克隆器。
3. 被摧毁的身体部位/器官（断肢、衰竭/缺失的心/肺/肝、死亡的脑）
   被替换：调配合成肉 → 在**器官培育器**中培育替代品 → 安装它（现场手术
   "prosthetic replacement"/"organ manipulation"，或把它放入 **autodoc**）。
4. 化学品在**可再填充化学分配器**（有限库存）中混合，然后要么分配到烧杯、
   喂给器官培育器，要么在**自动注射器打印机**中变成**自动注射器**，用于一键
   战斗投递。合成体船员使用并行的合成化学品线。
5. 死亡船员：若在时限内则用除颤器；否则（若舰船有培养舱和合成肉）用克隆（基础）。

## 机制

### MED-001
断言：**autodoc** 是器官/植入体*插入器*，而非治疗器。在有乘员的情况下关门
会启动 `dosurgery()`；在没有储存任何东西（且未被 emag）时，它只是告诉乘员 "no implant stored"
并不做任何事。它从不治疗钝击/灼伤/毒素/缺氧伤害，也不施加麻醉剂。
面向玩家的后果：受伤的病人从 autodoc 得不到任何好处；它只换入一个身体部位/器官。
证据：`nsv13/code/game/machinery/autodoc.dm:47-50`（无器官 → 返回）、`:35-45`（关门 → `dosurgery`）、`:71-81`（插入）。
条件：仅在乘员为 `/mob/living/carbon` 时生效；非碳基乘员会被强制弹出（`:38-42`）。
例外 / 覆写：无。
置信度：HIGH（高）

### MED-002
断言：一台 autodoc 恰好存放**一个**储存的器官（`storedorgan`），接受任何 `/obj/item/organ`
（包括赛博植入体，因为 `organ_type = /obj/item/organ`），且安装是一次*槽位交换*：它移除
病人在该槽位中已有的器官（丢到地板上）并插入储存的那个，**无失败
几率**。
面向玩家的后果：预装正确的器官，爬进去，等待；你会得到一个
保证的替换品，而你的旧器官被丢在机器的地格上。
证据：`autodoc.dm:11`（`storedorgan`）、`:12`（`organ_type`）、`:75-80`（移除旧 → 插入新）、
`:107-118`（`attackby` 装载一个器官）、`:109-110`（拒绝第二个）。
条件：安装时长 = `surgerytime`（见 MED-003）；以 `use_power(5000)` 完成。
例外 / 覆写：活着的医生可以*移除*器官而不替换，并能执行多步
程序（截肢、器官操纵、植入体）；autodoc 只能插入覆盖。
置信度：HIGH（高）

### MED-003
断言：autodoc 安装时间为 `surgerytime`，默认 **300 ds（30 秒）**，按所安装
原装部件的评级缩减：`max_time = 350 − Σ(rating·10)`，下限为 **10 ds（1 秒）**。
面向玩家的后果：原装 autodoc 约需 30 秒；升级版（T4 部件）降至约 20-23 秒，
完全堆满则触及下限 1 秒。
证据：`autodoc.dm:14`（默认 300）、`:20-24`（RefreshParts 公式）。
条件：`RefreshParts` 在部件变化时运行；没有部件的机器保持 300。
例外 / 覆写：在 `processing` 期间 `open_machine()` 会中途取消程序（`:87-94`）。
置信度：HIGH（高）

### MED-004
断言：对 autodoc 使用 emag 会使其协议腐化：下一次关门时它会**肢解乘员的每一条肢体**
（或者，对于 `TRAIT_NODISMEMBER` 病人，每条肢体造成 40 钝击），每次都发出尖叫，
肢体之间约有 0.5 秒间隔，因此乘员理论上可以逃脱。
面向玩家的后果：被 emag 的 autodoc 是一台绞肢/处决装置，而非医疗工具。
证据：`autodoc.dm:26-29`（检查警告）、`:57-69`（循环：`BP.dismember()` / 40 BRUTE，`sleep(5)`）。
条件：仅在 `obj_flags & EMAGGED` 且内部有乘员时；与 MED-002 的正常路径相反。
例外 / 覆写：emag 一旦施加即永久生效（`:164-168`）。
置信度：HIGH（高）

### MED-005
断言：**器官培育器**是 NSV 对*基础 limbgrower 的重新定义*（`/obj/machinery/limbgrower`，
同一类型路径，NSV 文件在基础文件之后被 include，因此其 proc/var 胜出）。它从
已装载的烧杯中所持有的试剂培育**身体部位和器官**；烧杯体积设定其
试剂容量。
面向玩家的后果：战舰医疗舱可以从合成肉（外加物种试剂）就地制造替代肢体/器官，
而不依赖采集/克隆。
证据：`nsv13/code/game/machinery/organgrower.dm:8-32`（类型/名称/描述/分类）；`nsv13.dme:3207` 与
`:4113`（基础 limbgrower 设计/行为先加载，NSV 文件最后）；`organgrower.dm:34-37`（create_reagents 100）。
条件：机器不得处于 `busy`；需要 MED-006 清单中的试剂；功率 = `max(2000, Σ reagents)`。
例外 / 覆写：基础 `/obj/machinery/limbgrower` 仍存在（`code/game/machinery/limbgrower.dm`）
但其 HTML UI（`main_win`/`Topic`）已死 —— NSV 用 TGUI 覆写 `ui_interact`/`ui_data`/`ui_act`。
置信度：HIGH（高）

### MED-006
断言：**可生产的设计及其试剂。** 分类为 `human, lizard, plasmaman, ethereal, moth,
apid, other`。分类包含 `"initial"` 的设计在每台器官培育器上自动解锁；所有
物种特有的设计都需要一张肢体设计磁盘（见 MED-007）。完整清单（每件物品的试剂成本）：
- 自动解锁（"initial"）：左/右臂（25 合成肉）、左/右腿（25）、心脏（30）、肺（20）、
  肝脏（20）、胃（15）、阑尾（5）、眼睛（10）、耳朵（10）、舌头（10）。
- 磁盘门控：Digitigrade Leg ×2（30，蜥蜴人）、Cat Ears（10，human）/ Cat Tail（20，human）、
  蜥蜴尾（20）、分叉舌（20）、Lizard/P.m. Tongue（10+20 等离子体）、Plasmaman Lungs/Liver/Stomach
  （10 + 20 等离子体）、Apid Lungs（10+20 蜂蜜）、Apid Eyes（25）、Apid Wings（25）、Apid Proboscis（10+20 蜂蜜）、
  Moth Eyes（25）、Moth Wings（25）、Ethereal Electrical Discharger（10+20 liquidelectricity）、
  Ethereal Battery stomach（10+20 liquidelectricity）。
- 仅 emag：**Arm Blade**（`/obj/item/melee/synthetic_arm_blade`，75 合成肉，`"other"`+`"emagged"`）。
面向玩家的后果：培育器是舰船的肢体/器官工厂；合成肉是通用输入，
等离子体/蜂蜜/liquidelectricity 门控异域物种器官。臂刃需要 emag。
证据：`nsv13/code/modules/research/designs/limbgrower_designs.dm:5-266`（所有设计）、`:11`（分类
清单），自动解锁规则位于 `code/modules/research/techweb/_techweb.dm:387-405`（`design_autounlock_categories = list("initial")`）。
条件：`make_limb` 对每种试剂检查 `production_coefficient`；缺少试剂 → 蜂鸣 + 中止（`organgrower.dm:163-168`）。
例外 / 覆写：调试磁盘（`limbgrower_designs.dm:293-301`）包含每个设计，包括物种设计。
置信度：HIGH（高）

### MED-007
断言：物设计通过将一张**肢体设计磁盘**（一种 protolathe 物品）装入机器来解锁；
磁盘由 `xenoorgan_bio` 科技树节点或 felinid/lizard/等磁盘生产。培育的肢体携带
`limb_id = <selected category>` 和物种特有图标；安装一个有机肢体，其 `limb_id`
**不**匹配病人的 `dna.species.id`，会将器官排斥提高到 30（同物种为 10）。
面向玩家的后果：培育与病人物种匹配的肢体，否则要承受额外的排斥伤害；
需要一张 "felinid" 磁盘，培育器才能制造猫耳/猫尾。
证据：`organgrower.dm:220-242`（`build_limb` 设置图标 + `limb_id`）、`:117-128`（装载磁盘添加设计）、
`:273-282`（emag 解锁 `"emagged"` 设计）；`nsv13/code/modules/research/designs/limbgrower_designs.dm:303-361`（磁盘）；
排斥：`code/modules/surgery/prosthetic_replacement.dm:41-49`。
条件：磁盘的 `limb_designs` 清单在插入时被复制到机器的内部科技树；磁盘近乎一次性
（设计持续存在于机器上）。
例外 / 覆写：培育肢体的描述声称肢体 "morph on their first use in surgery" —— 这是
风味文本；手术身体部位代码中不存在 "morph" proc（grep `morph` 找不到任何东西）。视为装饰性。
置信度：HIGH（高）（机制）；"morph" 说法为 UNVERIFIED（未验证）的风味文本。

### MED-008
断言（疑似 bug）：器官培育器的部件升级效率是死代码。`RefreshParts()` 声明了一个
**局部** `var/production_coefficient = 1.25`，重新计算它，却从不写回同名的
实例变量。结果：`src.production_coefficient` 保持在其初始值 `1`，因此试剂消耗和
生产时间**从不**被机械臂减少（对比基础 limbgrower，它写入成员 `prod_coeff`）。
面向玩家的后果：向器官培育器安装机械臂不会带来试剂/时间节省；检查
行始终显示 "Reagent consumption rate at 100%"。烧杯升级（容量）仍然有效。
证据：`organgrower.dm:244-252`（局部遮蔽成员）与成员 `:24`；基础正确版本位于
`code/game/machinery/limbgrower.dm:153-161`（`prod_coeff = min(1,max(0,T))`），检查使用成员 `organgrower.dm:257`。
条件：取决于 DM 将 proc 中的 `var/production_coefficient` 视为遮蔽
对象 var 的 proc 局部变量（标准 DM 行为）—— 标记为解读。
例外 / 覆写：烧杯容量*确实*生效（`reagents.maximum_volume` 被写入，无遮蔽）。
置信度：MEDIUM（中）（代码事实明确；DM 作用域结论为推断）

### MED-009
断言：**可再填充化学分配器**是一个*有限的*化学品储存，不同于基础 `/obj/machinery/chem_dispenser`，
后者从空间站电网再生化学品。它持有 3000u 的基础容量；你通过装载
烧杯并将试剂*转移进*储存来补充它（`mode`/`transfer` 路径从持有的烧杯中将单一试剂移入
机器，若 `mode` 关闭则丢弃它）。`/full` 子类型生成时即已预填。
面向玩家的后果：战舰的化学品供应有限，必须手动补足；你无法
合成无限的药物，因此生产吞吐量是一个真实的资源约束。
证据：`nsv13/code/game/machinery/refillable_chem_dispenser.dm:13`（`base_capacity 3000`）、`:59-67`（create_reagents，
NO_REACT）、`:69-75`（`/full` 均匀填满基础化学品）、`:292-309`（`transfer` 进入储存）、`:338-341`
（RefreshParts 添加烧杯容量）；基础对比 `code/modules/reagents/chemistry/machinery/chem_dispenser.dm:29-31,106-118`
（电池充电 → 实际无限）。
条件：`dispense` 需要该试剂存在于储存中（"No [reagent] left in storage!"）`:215-219`。
例外 / 覆写：它带有 `INTERACT_MACHINE_OFFLINE` 以及防火/防酸（`:9-10`）；没有
添加异域试剂的 emag 路径。
置信度：HIGH（高）

### MED-010
断言：**自动注射器打印机**将储存的试剂（"buffer"）转化为 NSV **自动注射器**。喂入一个
烧杯 → buffer，然后以 `item_type="medipen"` 执行 `create` 会生成 `/obj/item/reagent_containers/hypospray/autoinjector`
物品，命名为 "<name> autoinjector ([Nu])"，每个填充至多 `vol_each` 单位。体积被钳制为
`min(10, buffer/amount)` 每支笔，且计数被钳制为每次操作 **0–10**。`max_create`（仅 UI 显示）
= 2 + Σ 机械臂评级。
面向玩家的后果：医护兵可以从一份 buffer 批量生产带标签的战斗注射器（例如一叠 10 支 × 10u
肾上腺素笔），把散装化学变为即用笔。
证据：`nsv13/code/modules/reagents/chemistry/machinery/AutoInjectorPrinter.dm:160-219`（`create`）、
`:177-178`（medipen 的 ≤10u 钳制）、`:170`（钳制到 10 计数）、`:206-218`（生成 + `trans_to`）、
`:27-34`（`max_create = 2 + Σ manipulator rating`）。
条件：需要非空 buffer（`:161-162`）；仅支持 `item_type == "medipen"`（否则返回）。
例外 / 覆写：产物是 **NSV 自动注射器**，而非基础医疗笔（见 MED-011）——
物品路径为 `/obj/item/reagent_containers/hypospray/autoinjector`。
置信度：HIGH（高）

### MED-011
断言：**NSV 自动注射器**是一种新物品类型（无基础对应物；基础只有 `hypospray/medipen`）。它
即时投递 `amount_per_transfer_from_this = 10` 单位，具有**可拆卸盖子**（Alt-点击以移除/装回；
笨拙使用者有 5% 几率失手并永久失去盖子），可穿透硬质服（`ignore_flags = 1`），
且若在未盖盖子的情况下被拿起，会刺伤其持有者（`pickup` 时 25% 几率，对一条手臂 1 钝击 + 自我注射）。
面向玩家的后果：战斗注射器是两步的（开盖，然后注射），不同于医疗笔的单动作；
盖子失手或在未盖盖子时抓取笔，可能浪费一剂 / 刺伤你。
证据：`nsv13/code/game/objects/items/autoinjectors.dm:1-108`；基础医疗笔（自我注射 `attack_self`，
使用后失效）位于 `code/modules/reagents/reagent_containers/hypospray.dm:115-157`。
条件：`attack` 在盖子盖上时拒绝注射（`:54-57`）；`afterattack` 在加盖时阻止补充/抽取（`:60-69`）。
例外 / 覆写：自动注射器在空之前可重复使用（`reagent_flags = DRAWABLE`，无 `attack_self`）；
医疗笔定义 `attack_self` 用于自我注射，并在一次使用后自毁。
置信度：HIGH（高）

### MED-012
断言：**NSV 试剂。** 真正由 NSV 编写的化学品是：
- **Radioactive Disinfectant**（medicine，仅 SYNTHETIC）：从合成体移除储存的辐射，
  每 tick −min(rad, 8)。配方：5 乙醇 + 1 苯酚 + 1 碘 + 1 水 → 5u。
  （`nsv13/code/modules/reagents/chemistry/reagents/nsv_medicine_reagents.dm:1-13`；配方 `.../recipes/nsv_medicine.dm:1-5`。）
- **Highjack**（drug，仅 SYNTHETIC，OD 阈值 30）：在处理期间设 drugginess 15 + 随机蜂鸣；
  在**过量**（每 tick 30% 几率）时它撕下 IPC 的头，或者若已无头，则渗出油（20 ≈ 2u 真实
  出血，因 IPC 的 0.1× 出血修正）。配方：1 liquid solder + 1 system cleaner + 1 radioactive disinfectant + 1 radium → 3u。
  （`drug_reagents.dm:1-25`；`recipes/drugs.dm:1-5`。）
- 其他 NSV 试剂文件并非药物：**Cryogenic Tyrosene**（战斗机燃料；≤40 K 的低温配方，以及
  130 K 以上的过热爆炸，`pyrotechnic_reagents.dm:1-14`；`recipes/pyrotechnics.dm`）和 **Methane**
  （`other_reagents.dm:1-6`、`recipes/others.dm:1-6`），外加风味饮品 Naval Coffee/Coffee Creamer（`other_reagents.dm:8-37`）。
- 两种基础 "合成药物" 试剂被引入 NSV 套件库存和 Highjack 配方：**System Cleaner**
  （基础；−2 毒素 + 剥离其他化学品，仅合成体）和 **Liquid Solder**（基础；修复合成体脑损伤，
  治愈脑创伤）。（`code/modules/reagents/chemistry/reagents/medicine_reagents.dm:475-509`。）
面向玩家的后果：合成体船员有一个专门的药房（cleaner/solder/disinfectant）和一种恶劣的
仅合成体 OD 药物；治疗机器人的混合方式不同于有机体药物。
证据：如上所列。
条件：Highjack/Disinfectant 仅在 `PROCESS_SYNTHETIC` mob 上处理。
例外 / 覆写：无。
置信度：HIGH（高）

### MED-013
断言：**辐射**对有机体和合成体的伤害方式不同。基础：储存的辐射稳定衰减，并且，
高于 `RAD_MOB_SAFE` 时造成毒素伤害 —— NSV13 对此打了补丁，对 `TRAIT_TOXIMMUNE` 小怪
（IPC）跳过毒素。高于阈值时它造成击倒、呕吐、负向突变和脱发。NSV13 添加：IPC
携带 `TRAIT_IPCRADBRAINDAMAGE`，且不进行突变，它们获得一个脑创伤（65% 轻度 / 30% 重度 /
5% 特殊）并伴随 "Your system produces an error!" 消息；合成体从不呕吐。
面向玩家的后果：向机器人注入辐射不会使其突变 —— 它会使其错乱/受损，这些通过 Liquid Solder 或脑移植治愈，
而非 mutadone。有机体船员需要碘化钾 / 戊乙酸。
证据：毒素：`code/modules/mob/living/carbon/life.dm:358-360`（NSV TOXIMMUNE 守卫）；
`species.dm:1374-1425`（击倒/呕吐/突变/脱发，IPC 分支 `:1400-1420`，不呕吐 `:1388-1391`）；
特性 `code/__DEFINES/nsv13.dm:7-8`；辐射风暴触发 `code/datums/weather/weather_types/radiation_storm.dm:37-67`。
条件：突变需要 `!TRAIT_MUTATEIMMUNE`；IPC 分支需要 `TRAIT_IPCRADBRAINDAMAGE`。
例外 / 覆写：`TRAIT_RADIMMUNE`（辐射免疫）将所有后果归零。
置信度：HIGH（高）

### MED-014
断言：**对治疗有影响的物种生存特性。**
- **蜥蜴人（Lizardpeople）** 被 NSV 重做为冷血：`coldmod = 1`、`TRAIT_COLDBLOODED`。冷血 mob
  获得**无自然温度稳定**；取而代之的是冷蜥蜴燃烧营养来生成热量
  （`ectotherm_thermogenesis`）。低于约最低寒冷值时它会发抖（心情减益）并自我取暖；若饥饿
  它无法自我取暖并受到寒冷伤害。舒适范围 30-60 °C 给予心情增益。
  证据：`nsv13/.../nsv_lizardpeople.dm:4-64`；`nsv13/.../human/nsv_species.dm:4-39`；
  `code/modules/mob/living/carbon/life.dm:548-552`（无稳定）。
- **IPC**（基础物种，NSV 修改）：`TRAIT_MUTATEIMMUNE` + `TRAIT_IPCRADBRAINDAMAGE`，无辐射免疫，
  burnmod 2 / heatmod 1.5，`clonemod = 0`（无法克隆），`bleed_mod ×0.1`（油，出血约少 10×），
  耗电（吃电，使用 `apc_powercord`/ethereal 抽取），需要一个器官培育器合法的脑
  （正电子）来复活；复活会运行一个脚本化的重启序列。
  证据：`code/modules/mob/living/carbon/human/species_types/IPC.dm:1-238`。
- **Felinid（"猫娘"）** 是基础 `/datum/species/human/felinid`；NSV 的 `catgirl.dm` 只添加了**女仆 NPC
  登舰者**及其装束（他们携带生存医疗笔）。身为 felinid 在装饰上就是人类 + 猫耳/猫尾/猫舌。
  证据：`nsv13/.../species_types/catgirl.dm:1-72`；基础 `code/.../species_types/felinid.dm`。
- KNPC 登舰者物种（`syndicate_knpc.dm`、`nanotrasen_knpc.dm`、`spacepirate_knpc.dm`、`other_knpc.dm`）
  是仅 NPC 的 mob 类型，自身没有医疗玩法；登舰机器人使用 `/datum/species/ipc`。
  证据：四个 KNPC 文件；机器人 `other_knpc.dm:59-61`。
- **IPC 语言**：NSV 赋予 IPC `species_language_holder = /datum/language_holder/ipc`，使他们能说
  并理解 **Common 和 Machine**，但没有其他语言。
  证据：`nsv13/code/modules/language/ipc_language_holder.dm:1-7`；钩子 `IPC.dm:38`。
面向玩家的后果：治疗非人类是物种特有的 —— 保持蜥蜴人温暖/吃饱，用
合成化学品治疗机器人（将其辐射当作脑损伤处理），以及 IPC 的语言障碍。
置信度：HIGH（高）

### MED-015
断言：**除颤是基础内容且基本未被修改。** 除颤器仅在以下情况复活一个死亡的碳基生物：
以胸部为目标、除颤板已握持、电池已充电（`revivecost 1000`）、病人未 husk/自杀/
hellbound、死亡在 `DEFIB_TIME_LIMIT×10 = 9000 ds (15 min)` 内、钝击和火焰损失各自 `< 180`、
心脏存在且未衰竭、脑存在且未死亡/衰竭/自杀。胸部的太空服会阻挡普通除颤器（只有*战斗*除颤器
可绕过它）。成功时救助者会得到一条 **NSV13 特有的失忆台词**，告诉被复活的玩家他们记不起自己是怎么死的。
面向玩家的后果：15 分钟窗口和 180 钝击/180 灼伤上限意味着被鱼雷/爆炸轰烂的尸体
无法除颤 —— 必须先克隆或替换其肢体/器官。
证据：`code/game/objects/items/defib.dm:479-492`（`can_defib`）、`:602-660`（成功/失败原因，复活）、
`:658`（NSV13 失忆消息），常量 `code/__DEFINES/misc.dm:214`（`DEFIB_TIME_LIMIT 900`）、
`code/__DEFINES/mobs.dm:409-410`（`MAX_REVIVE_*_DAMAGE 180`）。
条件：`H.revive()` 还需要有幽灵在场（`grab_ghost` 路径将其拉回）。
例外 / 覆写：IPC `clonemod = 0`（无法克隆）但*可以*经由其器官脑除颤；战斗除颤器
（`defibrillator/compact/combat`）没有安全保护，可穿过服装修复。
置信度：HIGH（高）

### MED-016
断言：**克隆是基础内容且完全未被 NSV 修改**（无 NSV 对 `clonepod`/克隆器的引用）。克隆舱
存在于若干舰船地图上（例如 Shrike、Galactica、Gladius、Hammerhead）。克隆舱的 `RefreshParts` 设定克隆
速度、治疗级别（细胞损伤；最低 `MINIMUM_HEAL_LEVEL 40`）以及合成肉消耗（`fleshamnt`，
否则 3× 血液），且 `clone_process` 若缺少合成肉则中止。
面向玩家的后果：在存在克隆舱的地方，死亡船员可以使用合成肉被重新克隆（因此器官
培育器/化学品线也供给克隆）；克隆会留下按克隆舱部件缩放的细胞损伤。
证据：`code/game/machinery/cloning.dm:7,21-90,185-186,356-362`；地图放置 `_maps/map_files/Shrike/Shrike1.dmm:12466`。
条件：需要一个已连接的 DNA 扫描仪 + 记录；`experimental_pod` 允许怪异脑克隆。
例外 / 覆写：无 NSV；IPC 的 `clonemod = 0` 意味着机器人完全无法使用此路径。
置信度：HIGH（高）

### MED-017
断言：**舰船战斗伤员如何到达医疗舱**（交叉引用，不重新推导）。跨图层武器命中将一枚
*真实*弹丸中继到舰船内部，弹丸引爆，因此医疗舱伤员是普通的 ss13 伤害：
- 燃烧/地狱火鱼雷 → 爆炸 + 等离子火灾（`atmos_spawn_air o2/plasma, TEMP 1000`）→ 灼伤 + 中毒。
- 脏弹炮弹 → 爆炸 + `nuclear_waste` 贴花 + 辐照弹丸（300 rad，添加一个辐射
  组件持续 5 分钟）→ 辐射中毒 → 毒素（有机体）/ 脑创伤（IPC）。
- 上层结构致命一击 → 在链接区域内预告的爆炸 → 钝击/灼伤。
- 破口 → 减压 → 缺氧损失（大气）。
面向玩家的后果：一枚鱼雷可以一次性把多名船员送入火灾/辐射/破口分诊；医疗舱会同时
接收灼伤、中毒和辐射病人的混合，外加爆炸造成的断肢。
证据：`nsv13/code/modules/overmap/weapons/projectiles_fx.dm:143-231`（脏弹炮弹 + 燃烧载荷）、
`nsv13/code/modules/overmap/weapons/damage.dm`（中继路径）；`damage_control.md` 中的 DAMCTRL-002。
条件：仅适用于具有 `occupying_levels` 的舰船；`small_craft` 的缓解是 no-op（战斗机不会注入伤害）。
例外 / 覆写：登舰战斗（系统 17）会添加来自陆战队/登舰者的普通弹道/激光伤。
置信度：MEDIUM（中）（路由经交叉引用笔记 + 载荷代码追踪；每种武器 ⇒ 每种伤害类型的映射未穷尽枚举）

### MED-018
断言：NSV 添加了一个**合成体急救包** —— `/obj/item/storage/firstaid/robot`（"robotic treatment kit"）
包含一个焊接工具、焊接护目镜、电缆线圈、2× system-cleaner 医疗笔、一瓶放射性消毒剂、
一个注射器和一个健康分析仪；外加独立的 solder / radioactive disinfectant / system
cleaner 瓶子和一个 `system cleaner medipen`。
面向玩家的后果：医疗舱和机器人技师可以发放一个机器人专用套件；一个机器人可以无需有机体药物
就被 "治愈"（焊接/补足，清除毒素+辐射）。
证据：`nsv13/code/game/objects/items/storage_items.dm:116-171`。
条件：普通储存套件；通过 `PopulateContents` 填充。
例外 / 覆写：无。
置信度：HIGH（高）

### MED-019
断言：一个 `MediKitty`（`/mob/living/simple_animal/bot/medbot/catmedbot`）是一个换皮医疗机器人变体，且一个
探索/市场自动售货机向外派队伍出售**生存医疗笔**（一支大型一体式笔：salbutamol/leporazine/
tricordrazine/epinephrine/lavaland_extract/omnizine）。
面向玩家的后果：医疗舱的风味医疗机器人；生存笔是标准的外派队伍应急治疗。
证据：`nsv13/code/modules/mob/living/simple_animal/bot/catmed.dm:1-9`；
`code/modules/reagents/reagent_containers/hypospray.dm:221-228`（生存笔内容物）；
`code/modules/exploration_crew/exploration_vendor.dm:16` 和 `code/modules/mining/machine_vending.dm:140`（自动售货机）。
条件：不适用。
例外 / 覆写：无。
置信度：HIGH（高）

## NSV 医疗目录

| 物品 / 机器 / 化学品 | 功能 | 获取方式 |
|---|---|---|
| `autodoc`（`/obj/machinery/autodoc`） | 将一个储存的器官/植入体插入乘员，槽位交换，无失败；emag 会**肢解**乘员 | 仅地图（Serendipity）；无 R&D 电路板，`circuit = null` |
| `organ grower`（`/obj/machinery/limbgrower`） | 从已装载烧杯中的试剂（合成肉等）培育肢体/器官 | `adv_biotech` 电路板（基础）+ 地图 |
| `refillable chem dispenser` | 有限化学品储存，从烧杯补充 | `biotech` 电路板（`all_nodes.dm:80`） |
| `autoinjector printer`（`/obj/machinery/autoinject_printer`） | 将缓冲化学品变为 NSV 自动注射器（≤10× ≤10u） | R&D 节点 "Autoinjector Medipens"（tier 4，prereq adv_biotech+adv_surgery） |
| `autoinjector`（`/obj/item/reagent_containers/hypospray/autoinjector`） | 10u 即时穿透服装注射；需要开盖；未盖盖子时拿起有自我刺伤风险 | 由自动注射器打印机打印 |
| `system cleaner medipen` | 从合成体清除毒素/化学品 | 在机器人治疗套件中 / 地图 |
| `robotic treatment kit`（`firstaid/robot`） | 面向合成体的急救（焊机、电缆、cleaner、rad-disinfectant） | 地图 / 医疗舱 |
| `MediKitty`（`bot/medbot/catmedbot`） | 换皮医疗机器人 | 地图 |
| Radioactive Disinfectant（试剂） | 从**合成体**移除储存的辐射 | 5 乙醇 + 1 苯酚 + 1 碘 + 1 水 |
| Highjack（试剂，drug） | 合成体致幻；过量会**肢解头部**/渗油 | 1 liquid_solder + 1 system_cleaner + 1 radioactive_disinfectant + 1 radium |
| Liquid Solder（基础试剂） | 修复合成体脑损伤 / 创伤 | 1 乙醇 + 1 铜 + 1 银 @370 K |
| System Cleaner（基础试剂） | −2 毒素 + 剥离其他化学品（合成体） | 1 乙醇 + 1 氯 + 2 苯酚 + 1 钾 |
| Synthflesh（基础试剂） | 治疗钝击+灼伤（仅接触/贴片）；unhusk（<50 灼伤，≥100u） | 1 血液 + 1 碳 + 1 styptic + 1 silver sulfadiazine |
| Cryogenic Tyrosene（试剂） | 战斗机燃料；>130 K 爆炸（**非药物**） | 1 燃料 + 5 甲烷 @≤40 K |
| Naval Coffee / Coffee Creamer | 让你清醒（−sleepiness/−drowsiness）；咖啡会成瘾 | 饮品 |

## 跨系统依赖 / Cross-System Dependencies

- **系统 12/15（船体/护盾/损管）：** 伤员经由中继的内部弹丸进入医疗舱
  （DAMCTRL-001/002）。爆炸 → 钝击/灼伤；燃烧 → 灼伤+中毒；脏弹炮弹 → 辐射；致命一击引爆 →
  钝击/灼伤；破口 → 缺氧。医疗舱需求随损管失败程度而扩大。
- **系统 13（工程/反应堆）：** 辐射病人来源。`stormdrive`（许多 `radiation_pulse` 调用，
  包括 `:1250` 熔毁时 10000）、`rbmk`（`:176-182,363`）、`pdsr`（`:205-555`）、`gravitygenerator_modular`
  （`:51` 1800）、`inertial_dampener`（`:58`）、`plutonium_sludge` 贴花（`:30-34`）、核沉降天气。
  治疗：碘化钾/戊乙酸（有机体）或放射性消毒剂（合成体）。
- **系统 17（登舰）：** 陆战队/登舰者造成普通的武器伤；登舰机器人是 IPC
  （合成化学品病人）。见 `boarding.md`。
- **系统 20（研究）：** 器官培育器物种磁盘（`xenoorgan_bio`）、自动注射器打印机电路板（tier 4）、
  可再填充分配器电路板（biotech）。舰船内部药品的可用性受 R&D 门控。
- **系统 25（职业）：** 医疗职业的装备配置/权限在此范围之外。

## 未解问题 / Open Questions

1. **机械臂效率 bug（MED-008）：** 确认 DM 编译器是否将遮蔽的
   `var/production_coefficient` 视为 proc 局部变量（使部件升级成为 no-op）。对
   `organgrower.dm` 文件做一次编译/警告检查即可定论。
2. **培育器 "morph" 风味：** 没有代码实现肢体
   描述中 "morph on first use in surgery" 的说法；它纯粹是装饰性文本，还是曾有机制被移除？
3. **autodoc 对植入体的接受：** `organ_type = /obj/item/organ` 意味着赛博植入体被接受，但
   不存在地图/R&D 指引，且仅映射了一台 autodoc（Serendipity）。确认预期用途。
4. **轮换地图上的克隆：** 实际处于轮换中的哪些舰船有克隆舱，决定了克隆
   是真实的生存路径还是稀罕物（仅在 Shrike/Galactica/Gladius/Hammerhead/Aetherwhisp/Atlas 上观察到，
   而非 Instanced 登舰布局）。
5. **autodoc 拆解：** `circuit = null` 却拥有螺丝刀/撬棍动作 —— 确认它能否被
   打包/在舰船之间移动。
6. **Highjack 致命性平衡：** 过量去头依赖 `get_bodypart("head")`；确认若 IPC
   已经失去头部时的交互（代码通过渗油覆盖了无头情形）。
