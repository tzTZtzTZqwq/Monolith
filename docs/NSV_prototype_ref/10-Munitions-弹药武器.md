> 本文为 research/evidence/munitions.md 的中文翻译。

# 弹药武器 —— 舰船武器与弹药

## 系统概述
"弹药武器"是 NSV13 中负责**舰炮物理层面**的系统：位于船体内部的机械（一个 `/obj/machinery/ship_weapon` 子类型）加上送入其中的弹药物品。它被刻意从*开火规则*（系统 9）中拆分出来：这里的机械负责装填、上膛、维护、挥发特性，以及炮弹殉爆时的地图内爆炸；而 overmap 上的抛射物／点射／装甲逻辑位于 `/datum/overmap_ship_weapon` 与 `projectiles_fx.dm`，此处仅作交叉引用。

一个循环的两半：
- **弹药生产** —— 研究（弹药科技树节点）→ PROTOLATHE 设计图 → 实体炮弹／导弹／鱼雷；外加货物补给箱与专用机械（舷侧装填机、高斯分配器、导弹构建机械臂、ammo_sorter）。
- **火炮操作** —— 把弹药送到炮位（弹药推车／VLS／弹药架）、装填、送弹、上膛、解除／开启保险、开火，然后在火炮故障或炮弹在你身上引爆之前对其进行保养。

实体火炮与 overmap 火炮是两个对象，通过 `weapon_datum_type` → `link_to_overmap_weapon_datum()` 连接。只有当实体火炮上报自身*已装填、已上膛且保险已关闭*时，一发炮弹才能抵达战术控制台的开火控制（`update()` → `mark_physical_weapon_loaded()`，`_ship_weapon.dm:400-414`）。

## 核心游戏循环
1. **研究**弹药科技节点（`all_nsv_nodes.dm`）以解锁 PROTOLATHE 设计图（`munitions_designs.dm`），全部归入 "Advanced Munitions" 车床分类，并由 `DEPARTMENTAL_FLAG_MUNITIONS` 限定。
2. **生产**弹药于原生机：炮弹、发射药包、弹头、制导／推进／IFF 部件、舷侧弹壳／装药、phoron 核心，或机械（导弹构建器、ammo_sorter、慢速传送带）。
3. **组装**制导弹药：导弹／鱼雷**弹壳**由手工或自动化装配线（`automation.dm` 机械臂 + 慢速传送带）建造，弹头／导引头／推进器装配到弹壳上。
4. **备置**炮弹于火炮处：弹药推车（6 格）、ammo_sorter 弹药架（12 格，可升级）、VLS 装填器（2）、高斯弹药架（12），或甲板炮的弹药架。
5. **装填 → 送弹 → 上膛**火炮（下述状态机）。在其所属系列有相应开关时开启保险／解除保险开关（发射药包用 multitool 解除保险；混合轨道炮装药筒；等离子炮气体 + 对准）。
6. 从战术／炮手控制台**开火**（系统 9）。实体火炮播放其动画 + 内部音效，并产生 `local_fire()` 效果（未佩戴听力保护时耳膜爆裂；等离子炮使未受保护的眼睛致盲）。
7. **保养**火炮：经过 `maint_req` 发之后它会发生故障并拒绝开火，直到被拧开 → 松开螺栓 → 上油；舷侧炮另外还需要擦洗以清除积碳；若火炮／弹药架被高温波及或被大力击中，挥发性的弹药可能爆炸。

## 机制

### MUNI-001
断言：每门舰炮都是一个 `/obj/machinery/ship_weapon`，运行一套固定的装填状态机 `STATE_NOTLOADED → LOADED → FEEDING → FED → CHAMBERING → CHAMBERED → FIRING`，且只有处于 `STATE_CHAMBERED` 时才能开火。
面向玩家的后果：把弹药放进去并不等于火炮"已装填" —— 你必须装填、然后送弹、然后上膛，并且（对大多数炮而言）关闭保险。开火会消耗已上膛的炮弹，并使火炮回退一个或多个状态。
证据：`state` 变量默认 `STATE_NOTLOADED`（`_ship_weapon.dm:74`）；当 `state < STATE_CHAMBERED || !chambered` 时 `can_fire` 返回 FALSE（`_ship_weapon.dm:468-469`）；`fire()` 将状态设为 `STATE_FIRING`，每发之后若有剩余弹药则变为 `STATE_FED`，否则变为 `STATE_NOTLOADED`，若为 `semi_auto` 则再次 `chamber()`（`_ship_weapon.dm:495-526`）。`update()` 只有在 `!safety && chambered` 时才将 overmap 武器标记为已装填（`_ship_weapon.dm:400-414`）。
条件：`semi_auto = TRUE` 的火炮在射击之间自行上膛（轨道炮、甲板炮、舷侧炮、PDC、高斯、VLS）；`auto_load` 火炮从弹药自动送弹。
置信度：HIGH（高）

### MUNI-002
断言：火炮有一个维护倒计时。每发都会递减 `maint_req`；归零时火炮 `weapon_malfunction()` 并拒绝一切开火，直到通过打开面板（螺丝刀）并加注 ≥10 单位机油来保养。
面向玩家的后果：持续射击会迫使出现维护中断。无视它会把火炮锁死（面板闪红灯、火花、抖动）；弹药技师通过拧开面板、松开外壳螺栓、施加 10u 机油来恢复，从而清除该标志并重置 `maint_req = max(maint_req, rand(15,25))`。
证据：`after_fire()` 递减 `maint_req`，归零时调用 `weapon_malfunction()`（`_ship_weapon.dm:580-587`）；`weapon_malfunction()` 设置 `malfunction=TRUE`，产生火花、红灯（`ship_weapon_maintenance.dm:21-31`）；当 `maintainable && malfunction` 时 `can_fire` 予以阻止（`_ship_weapon.dm:475-476`）；`oil()` 要求 `MSTATE_UNBOLTED` + `/datum/reagent/oil` 10u，清除 `malfunction`，设置 `maint_req = max(maint_req, rand(15,25))`（`ship_weapon_maintenance.dm:119-139`）；初始 `maint_req = rand(20,25)`（`_ship_weapon.dm:104`）。
条件／例外：`maintainable = FALSE` 的火炮（甲板炮、舷侧炮、PDC、50cal、高斯）不会以这种方式发生故障 —— 它们有自己的失效模式（积碳、卡壳）。维护面板用 screwdriver_act 打开；用 wrench_act 松开螺栓；两者均为 40 tick 的 `use_tool`。`MSTATE_PRIEDOUT` 已被注释掉（`ship_weapon_maintenance.dm:1`），但在某些 `examine` 字符串中仍被引用。
置信度：HIGH（高）

### MUNI-003
断言：挥发性弹药携带一个 `/datum/component/volatile`。它在被点燃时引爆，并且（若 `explode_when_hit`）在遭到重击时引爆；爆炸规模随该弹药的 `volatility` 缩放。
面向玩家的后果：放在火旁或遭到击中的鱼雷或一叠发射药包可能殉爆，并连锁波及附近的一切。发射药包和鱼雷弹头是经典的"看好那些炮弹，军械士官"危险源。
证据：当 `volatility > 0` 时基础弹药添加 `AddComponent(volatile_type, volatility, explode_when_hit, volatility_scale)`（`ammunition.dm:17-18`）；`volatile.explode()` 运行 `explosion(parent, 0, 0.75*ExPower, 1.5*ExPower, 2*ExPower, ...)`，其中 `ExPower = volatility * explosion_scale`（`volatility_component.dm:22-25`）；`burn_act` 以 `prob(CLAMP(volatility*10,0,100))` 触发（`:30`）；`damage_react` 以 `prob(CLAMP(amount/5*volatility,0,100))` 触发（`:36`）。
条件：`volatile_when_hit` 区分"被击中即爆炸"与"仅燃烧" —— 基础弹药默认 `explode_when_hit = FALSE`（`ammunition.dm:6`）；导弹将其设为 TRUE。数值：powder_bag 挥发度 2，plasma accelerant 4，naval artillery 3，导弹 3，鱼雷 3–4，freight `volatility 0`。
置信度：HIGH（高）

### MUNI-004
断言：未受保护的船员会因开火而受伤，且与正在开火的火炮的距离很重要。基础火炮会震爆耳膜；等离子炮会致盲；舷侧炮会烫伤其后方的任何人。
面向玩家的后果：弹药区域需要听力保护（等离子炮附近还需要眼部保护）；站在舷侧炮后方是切实危险的。
证据：`local_fire()` 对 `bang_range`（默认 8）内且 `get_ear_protection() < 1` 的听觉者执行 `M.soundbang_act(1,200,10,15)`（`_ship_weapon.dm:547-553`，`bang_range` 变量）。等离子炮 `local_fire()` 对眼部保护 <2 者执行 `M.flash_act(10)`（`plasma_gun.dm:216-221`）。舷侧炮尾焰 = 炮后方 10 点 BURN + Stun(5) + Knockdown(50)（`broadsides.dm`）。
置信度：HIGH（高）

### MUNI-005
断言：制导弹药（导弹／鱼雷）由部件组装到一个弹壳上，可通过手工，或通过由慢速传送带连接的单一功能机械臂组成的自动线完成。
面向玩家的后果：导弹／鱼雷补给是一条制造链，而非自动贩卖购买：建造弹壳，然后按顺序装配弹头 + 制导 + 推进 + IFF；自动化机械臂（`missile_builder` 子变体）各自在弹壳到达正确构建状态时执行一个步骤。
证据：`automation.dm` 机械臂：welder `target_states=list(10)`、wirer `list(8)`、screwdriver `list(3,5)`、assembler 持有组件并按 `target_state`／`fits_type` 装配；慢速传送带以 2s 在各工位之间移动弹壳。PROTOLATHE 设计图 `missilebuilder/missilewelder/missilescrewer/missilewirer/missileassembler` + `slowconveyor` 位于 "Automated Missile Construction" 科技节点下（`all_nsv_nodes.dm:83-91`；`munitions_designs.dm:1-60`）。导弹构建使用 12 个离散状态（`missiles/missile_construction.dm`）。
条件：`slowconveyor` 可由 AUTOLATHE 建造（也可经货物）；其余为 PROTOLATHE／MUNITIONS 标志。
置信度：HIGH（高）

### MUNI-006
断言：舷侧炮的失效模式是**积碳**，而非故障。积碳随每发上升，既降低精度也可能使火炮烟囱卡壳（卡死）；可通过用 swabber（或太空清洁器）擦洗来清除。
面向玩家的后果：舷侧炮是"高射速但需频繁清洁"的武器：不加节制地开火会污染炮管并最终堵死它，而手工装填一个新弹壳也可能污染炮尾。
证据：`broadsides.dm` 跟踪 0–100 的积碳；烟囱卡壳几率约 `soot/10`，上限 10%；弹药箱／弹壳装填可以 `prob(soot)` 污染；overlay 反映积碳水平；`swabber` 设计图正是为此存在（`munitions_designs.dm:262-270`）。`maintainable=FALSE`，因此没有上油循环。
条件：还可通过扳手旋转以切换左舷／右舷；尾焰伤害（MUNI-004）。
置信度：MEDIUM（中）（积碳曲线从源码注释／数值读取；确切的烟囱卡壳常量并非本次逐行全部复核）

### MUNI-007
断言：ammo_sorter 弹药架在装填弹药时会磨损，并且可能**卡壳**；卡壳的分拣器必须用撬棍解卡，而耐久度用机油恢复。
面向玩家的后果：为你的火炮供弹的自动化装置本身就是消耗品：大量的自动装填最终会卡壳，使补给中断，直到技师用撬棍把它疏通并上油。
证据：`ammo_rack.dm` `durability = 100`、`max_durability = 100`、`repair_multiplier = 10`、`jammed` 标志；`weardown()` 每次装填减 1，然后 `jamchance = CLAMP(-50*log(50, durability/50),0,100)`，命中时设置 `jammed` 且 `durability=0`（`:346-358`）；机油修复 `durability += oil_amount * repair_multiplier`（`:192`）；撬棍解卡约 10s；通过物质仓的升级子类型 `max_capacity = 21`（`:283-287, 364-367`）；基础 `max_capacity = 12`（`:130`）。
置信度：HIGH（高）

### MUNI-008
断言：能量武器没有实体弹药。它们随时间充能，每发消耗固定电量；持续射击受反应堆功率限制（`power_modifier` 缩放）。
面向玩家的后果：能量炮永远不会"打光炮弹"，但可能被压制射速：它们耗尽电容器的速度快于补充速度，而较弱的电网会更慢地为其充能，因此实际限制是负载下的射速。
证据：基础 `/obj/machinery/ship_weapon/energy` 使用 `charge`、`charge_rate`、`charge_per_shot`、`max_charge`、`power_modifier`、`power_modifier_cap`、`static_charge`；`fire()` 执行 `charge -= charge_per_shot`（`_ship_weapon.dm:528-542`）；`process()` 按 `power_modifier` 缩放的速率回充。Phaser/beam cannon MK2 = 每发 660k，上限 3.3M；phase cannon = 每发 4M，上限 8M；激光 PD = 每发 1M，上限 4M，`static_charge`，RefreshParts 中的电容器缩放 max_charge／cap；激光 AMS = 每发 3M，上限 3M。BSA = 每发 75M，`power_modifier_cap 3`。
置信度：HIGH（高）

### MUNI-009
断言：混合轨道炮可在实心弹丸与充气装药筒之间切换，其弹丸是**由材料锻造**的 —— 所装材料的导电率／密度／硬度决定弹丸伤害与穿透。
面向玩家的后果：弹药技师可通过选择材料刻意制作更好或更便宜的轨道炮弹药；该武器是一个可配置平台，而非固定炮弹。
证据：`hybrid_weapons/hybrid_railgun.dm`：NT-ST049 'Sturm'；`slug_shell` 0/1 切换；弹丸 `max_ammo=5`，上限 400 kW；装药筒 `max_ammo=1`，上限 1 MW；锻造弹药从材料属性推导伤害／穿透（`railgun_ammo.dm` railgun_ammo/forged）；`alignment` 每发下降，导致蓝空间误射；`functional_crit` 通过焊接修复；通过科技磁盘供给。
置信度：MEDIUM（中）（机制已确认；确切的伤害缩放系数本次未重新推导）

### MUNI-010
断言：货物／快递发射器是**舰船货物机械，而非武器** —— 货物鱼雷发射器和货物发射器刻意不上报给战术控制台。
面向玩家的后果：你无法意外地（或有意地）从炮手控制台发射货物；载荷通过各自的控制装置发送，并在 dradis 上追踪。
证据：`cargo_launcher.dm` 设置 `weapon_datum_type = null`（无 overmap 武器 datum）并链接到 dradis；货运输鱼雷在开火时是被移动而非被 `qdel` 掉（`_ship_weapon.dm:511-512` 例外）。货舱中的鱼雷发射器在地图上显示为 `torpedo_launcher cargo`。
置信度：HIGH（高）

### MUNI-011
断言：弹药补给通过两条非车床途径抵达舰船：**货物补给箱**（批量弹匣／弹壳／装药）和**专用分配／装填机械**（高斯分配器、舷侧炮弹装填机）。
面向玩家的后果：有些弹药由货物批量订购（便宜、慢），有些则由子部件在现场制作（舷侧炮弹需要 5 个弹壳 + 5 个装药 + 1 个发射药包来制作 5 发炮弹；铀装药需要等离子）。
证据：`code/modules/cargo/packs.dm:340-459` —— `light_cannon`（10 箱）、`heavy_cannon`（10）、`broadside_casings`（15）、`broadside_loads`（15）、`broadside_pack`（5 装药 + 5 弹壳 + 1 发射药）。`broadside_ammo.dm` 的 `broadside_shell_packer` 配方；`gauss_ammo.dm` 的 `gauss_dispenser`（`dispense_amount=12`，45s，需供电）。
置信度：HIGH（高）

### MUNI-012
断言：一门火炮的物理状态必须上报给 overmap 武器 datum，控制台才能发射它，而若干武器设有*独立的*保险／解除保险动作，炮手无法从控制台绕过。
面向玩家的后果：在弹药库中装填火炮只是工作的一半；必须还有一名船员实际解除其保险（关闭保险／发射药解除保险／关闭气体调节器），否则控制台的火控将保持失效。
证据：`update()` 仅在 `!safety && chambered` 时标记为已装填（`_ship_weapon.dm:400-414`）；`can_fire` 在 `safety` 上予以阻止（`:477-478`）；naval_artillery 通过 multitool 解除保险（`deckgun_ammo.dm`）；等离子炮要求 `safety` 关闭、`plasma_mole_amount >= plasma_fire_moles`（500）、调节器关闭，且对准 ≥ ~25（`plasma_gun.dm:173-214`）。
置信度：HIGH（高）

### MUNI-013
断言：等离子炮（MPAC）是一件定制的、高维护的特殊武器：它需要管道输送的 phoron 气体被冷凝到 500 摩尔、保持接近 100% 的约束场、保持高磁对准度，以及射击之间的长冷却；操作失误会泄放等离子、生成一只敌对的深紫色史莱姆，并可能杀死操作者。
面向玩家的后果：操作 MPAC 是一场气体管路 + 对准调校 + 计时的迷你游戏；搞砸了会在房间内造成灾难性后果。
证据：`overmap/weapons/plasma_gun.dm`：`plasma_fire_moles=500`、`plasma_mole_amount`、`alignment`（100）、`field_integrity`（100，保险关闭时衰减）、`cooldown`（100，射击之间约 1.5 分钟）；`misfire()` 泄放 `atmos_spawn_air("plasma=[plasma_mole_amount]...")`、`makedarkpurpleslime()`、tesla_zap；`can_fire` 以气体、调节器（`loader.on`）和对准阈值为门槛（低于 90/75/50 为概率性误射，低于 25 为必然误射）；撬棍拆解会烧伤使用者；`multitool_act` 重新调校对准；`plasma_loader` 调节器机械；`Vintergatan Manual` 一书对其有记载。
置信度：HIGH（高）

### MUNI-014
断言：挥发性弹头具有不同的爆炸剖面：标准（HE）、`emptorp`（EMP 脉冲）和 `helltorp`（火焰 + 等离子大气），而货运输鱼雷是惰性货物。
面向玩家的后果：不同的鱼雷载荷在误爆时和命中时做的不同；地狱火载荷误爆是一场清空房间的火风暴，而 EMP 载荷误爆是舰船范围的 emp 而非大爆炸。
证据：`volatility_component.dm` 子类型：`emptorp/explode()` = `empulse(...)` + 小爆炸（`:69-78`）；`helltorp/explode()` = 大爆炸 + `atmos_spawn_air("o2=20;plasma=110;TEMP=700")`（`:84-94`）；标准来自 `:16-25`。鱼雷类型设定这些：hellfire NTP-6 挥发度 4 → helltorp；proto_disruption NTP-I1x 挥发度 2 → emptorp；freight `volatility 0`。
置信度：HIGH（高）

## 武器／弹药目录
| 武器 | 弹药类型 | 角色 | 如何生产 | 映射于（玩家舰船） |
|---|---|---|---|---|
| MAC / railgun（`mac.dm`、`railgun.dm`） | `railgun_ammo`、`railgun_ammo/uranium` | 重型单发 | PROTOLATHE `railgun_round` | （任务／稀有；许多船的货舱内有发射器） |
| Hybrid railgun 'Sturm'（`hybrid_railgun.dm`） | 弹丸 / 气体装药筒（锻造） | 重型、可配置 | 由材料 + 科技磁盘锻造 | Shrike |
| Deck gun M4-15 'Hood' / 'Yamato'（`deck_guns.dm`） | `naval_artillery`（+AP、homing、magneton）、`powder_bag`/plasma | 重型火炮 | PROTOLATHE `naval_shell`/`_ap`/`powder_bag`/`plasma_accelerant` | Eclipse、Gladius、Atlas、Snake、Tycoon、Vago、Galactica、Serendipity、Testship |
| Broadside 'Sucker Punch'（`broadsides.dm`） | `broadside_shell`（+plasma、+uranium） | 中型舷侧、高射速 | `broadside_shell_packer`（5 弹壳 + 5 装药 + 1 发射药 → 5 发炮弹） | Snake、Hammerhead |
| PDC（`pdc.dm`） | 弹匣 `nsv/pdc`（300） | 轻型近防／反战机 | 货物箱 / 建造 | 许多（pdc_mount） |
| 50cal 防空（`50Cal.dm`） | 弹匣 `nsv/anti_air`（300） | 轻型防空炮塔 | 建造 | 若干（pdc_mount/anti_air） |
| Flak（`flak.dm`） | 弹匣 `nsv/flak`（150） | 反鱼雷防空（flak 云） | 建造 | Testship（pdc_mount/flak） |
| Gauss gun NT-BSG（`gauss_gun.dm`） | gauss 弹药 | 中型、12 发弹药架 | `gauss_dispenser`（每次 12 发） | Eclipse、Gladius、Shrike、Atlas、Snake、Tycoon、Serendipity、Galactica、Hammerhead、Vago |
| Torpedo launcher M4-B（`torpedo_launcher.dm`） | 鱼雷（NTP-2/4/6/F、decoy、probe） | 重型制导 | 手工／自动组装 | Aetherwhisp、Gladius、vonneumann、Shrike、Atlas、Snake、Tycoon、Serendipity、Galactica、Hammerhead、Vago、Testship |
| Cargo torpedo launcher | 货运输鱼雷（NTP-F） | 后勤（无控制台） | 组装 | 大多数舰船（cargo 变体） |
| Cargo launcher M4-C（`cargo_launcher.dm`） | freight | 后勤（无控制台） | 组装 | 若干 |
| VLS M14（`revision2/vls.dm`） | 导弹 / 鱼雷 | 垂直发射、2 个发射单元 | 组装 | Gladius、vonneumann、Tycoon、Serendipity、Galactica、Hammerhead、Atlas、Vago、Testship |
| Missile NTMS / Sparrowhawk | 制导导弹（+巡航） | 中型制导 | 自动导弹线 | VLS 装填 |
| BSA（`energy_weapons/bsa.dm`） | 电力 | 超重型 hitscan（穿透内部） | 建造 | Testship、Galactica |
| Phaser / beam cannon / phase cannon（`energy_weapons/phaser.dm`） | 电力 | 重型能量爆发 | 建造 | Aetherwhisp、vonneumann、Testship、Galactica |
| Laser PD（`energy_weapons/laser_pd.dm`） | 电力（静态充能） | 轻型能量近防 | 建造 | Aetherwhisp、vonneumann |
| Laser AMS（`energy_weapons/laser_ams.dm`） | 电力 | 反导弹 | 建造 | Aetherwhisp、vonneumann |
| Plasma caster MPAC（`plasma_gun.dm`） | `plasma_core` + 管道 phoron 气体 | 超重型制导 | PROTOLATHE `plasma_core` | Serendipity |

（"映射于"列 = `_maps/*.json` 中其 `.dmm` 文件放置了该 `/obj/machinery/ship_weapon/` 子类型的玩家舰船。所列出的全部伤害数值 —— 例如 naval shell 250–350、torpedo 250、hellfire 400、BSA beam 1000、plasma ball 150 —— 均位于 `projectiles_fx.dm`，即系统 9 的文件。）

## 跨系统依赖
- **系统 9（开火规则）：** 伤害、点射规模、制导、装甲标志以及火控 UI 位于 `/datum/overmap_ship_weapon` 与 `projectiles_fx.dm`。本文只说明实体火炮必须先上膛 + 解除保险，该系统才会行动。
- **研究／科技树：** 弹药科技节点限定此处的每一个 PROTOLATHE 设计图（`all_nsv_nodes.dm`、`munitions_designs.dm`）。
- **货物／补给：** 批量弹匣和弹壳经 `cargo/packs.dm`；"Advanced Munitions" 车床分类为 `DEPARTMENTAL_FLAG_MUNITIONS`。
- **Overmap／映射：** `weapon_datum_type` 链接与地图放置。
- **电力（工程）：** 能量武器随反应堆 `power_modifier` 消耗／缩放；机械电力经 `power_change()`。
- **化学／大气：** 机油（10u）保养火炮与分拣器；等离子炮需要管道 phoron 气体 + 调节器；hellfire 生成等离子空气。
- **EVA／防护装备：** 听力保护抵消震爆；眼部保护抵消等离子闪光。

## 开放问题
- 舷侧炮确切的烟囱卡壳／积碳曲线常量（MUNI-006）—— 从注释读取；需要逐行遍历以枚举每一个 `prob()`。
- 混合轨道炮锻造弹丸的伤害／穿透公式（MUNI-009）—— 材料→属性的映射本次未进行数值重新推导。
- 哪些货物／任务补给投放交付哪些弹药类型、哪些仅为车床独有；`packs.dm` 第 340–459 行涵盖了已知的弹药箱，但其他补给包可能也储备炮弹。
- 甲板炮模块效果（autorepair/autoelevator/calibrator、gauss rack 升级）从科技树设计列表读取，但其每模块的游戏性增量未完全追踪。
- NPC／AI 舰船是否以抽象化（无限）弹药生成，还是需要同样的实体供应链（关系到 MAA 岗位在 PvE 中如何适用）。
