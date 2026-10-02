> 本文为 research/evidence/security.md 的中文翻译。

# 安保、舰船防御与反登舰

范围：军舰上的舰内安保，以及对抗敌对登舰者 / 船员反派的防御。涵盖安保职业装备与武器、Ju
Jitsu 武术、安保等级、登舰撞针 / 友军误伤规则、IFF/应答机安保、海盗与船员反派物品、
gulag 包，以及安保如何击退登舰者和船员反派。交叉引用：系统 17（登舰——见
`research/evidence/boarding.md`）、系统 23（小队）、系统 24（船员反派）、系统 8/11
（星图/IFF、阵营）。本文件中的全大写系统 id（SEC-###）为本地编号。

## 系统概述

NSV 的舰船安保是空间站安保的换皮版，带有军事化框架：安保官（Security Officer）职业被
重命名为 **军事警察（Military Police）**，该部门上限为 10 名警官，另有安保主管（Head
of Security）、典狱长（Warden）、侦探（Detective）与医务官（Brig Physician）。单独的
**军械长（Master At Arms，MAA）** 是一个 *军火/指挥* 角色，**并非** 安保部门的成员——
它不拥有禁闭室/军械库权限（`master_at_arms.dm:21-22`）。轻武器 **仅限弹道武器**
（代码中遍布 "NSV13 no lasers" 注释）；制式随身武器是 9mm Glock-13，制式非致命工具是
X24 泰瑟（Tazer）。反登舰防御是一个双层系统：常设的安保部门，加上基于小队的
"General Quarters（全员战备）" 动员——后者通过小队补给机将普通船员临时征召为副手，并
可授予他们禁闭室/安保权限。陆战队长枪造成的友军误伤由一把特殊密钥的 *登舰撞针*
控制，而非由武器本身控制。

## 核心玩法循环

- **和平时期：** 宪兵（MP）巡逻、逮捕并用橡胶 Glock 弹、泰瑟和 Ju Jitsu（由配发的
  手册传授）将船员反派收监。典狱长看守军械库并管理禁闭室 / gulag。致命/穿甲/燃烧弹匣
  存在，但不是默认装备。
- **General Quarters（红色警报）：** 通过警报广播；舱内灯光变红；撤离舱发射变为玩家
  控制；军械库可被打开。船员前往 **小队补给机** 领取装备；XO/HoS 启用小队权限，并
  （对非安保小队）开放武器许可。遭受攻击的舰船会自动提升至红色警报。
- **Condition Zebra（斑马状态）：** 每一扇防火门砰然关闭以密封船体（战备 / 损管）。
- **击退登舰：** AI/敌舰投放 KNPC 或幽灵角色登舰者的舱体；安保 + 被动员的小队追剿
  他们。陆战队步枪的友军误伤由登舰撞针门控，仅能从军械库控制台解锁。
- **登舰进攻（船员侧）：** 船员登上敌舰；与安保相关的终点是黑入敌方 IFF 控制台以翻转
  其阵营（见 BOARD-009）。

## 机制

### SEC-001 — 舰船安保轻武器均为弹道武器；存在不同的致命性层级
断言：NSV 制式安保枪械（定义于 `nsv13/code/modules/jobs/security/weapons.dm`）全部为
弹道武器，且默认弹药为非致命弹。
面向玩家的后果：宪兵开局携带一把点射 9mm Glock-13BR 和一把 X24 泰瑟；致命/穿甲/
燃烧弹药存在，但必须刻意装填。
证据：`/obj/item/gun/ballistic/tazer`（"X24 Tazer"，:2-20）——单发弹匣
（`/obj/item/ammo_box/magazine/tazer_cartridge`，max_ammo 1），fire_delay 2 秒，发射
`/obj/item/projectile/energy/electrode/hitscan`（射程 2，伤害 75 STAMINA，hitscan，:298-302）。
`/obj/item/gun/ballistic/automatic/pistol/glock/security`（"Glock-13BR"，:40-57）——
burst_size 3，fire_delay 2，弹匣 `/obj/item/ammo_box/magazine/glock`（橡胶，max_ammo 15）。
Glock 弹匣变体见 :193-223：致命（`c9mm`）、橡胶（`c9mm/rubber` 弹丸伤害 20 STAMINA）、
穿甲（armour）与燃烧。各自的 protolathe 设计受
`departmental_flags = DEPARTMENTAL_FLAG_SECURITY` 门控（:105-183）；橡胶 + 泰瑟弹丸
额外可由 autolathe 打印（:123-129，:185-191）。
其他变体：基础船员版 Glock-13、`glock/command`（指挥军官，:91-94）、`glock/makarov`
与 `/makarov/lethal`（较旧的 H&KC 手枪，预装致命弹，:59-65）、`m1911/m9le`（.45 M9LE，
:67-76）。`/security/hos` = "Winona"，全自动，`INDESTRUCTIBLE`，仅 HoS 可用（:96-103）。
`/obj/item/projectile/energy/electrode/hitscan/on_hit`（:304-320）：命中后有 10% 概率
造成 **心脏病发作**（`set_heartattack(TRUE)`）——即便在 "眩晕" 泰瑟上也是一种致命的
失效模式。
条件：泰瑟为单发，需手动更换弹匣；警官携带一个 5 发弹匣架
（`tazer_cartridge_storage`，:275-286），每发之后都要更换。
例外 / 覆写：安保机械人武器 `/obj/item/gun/energy/printer/taser`（Cyborg X25，e_cost
600）与 `/obj/item/gun/energy/printer/glock`（致命/橡胶切换）为能量供给，仅限打印机
（:324-358）。
置信度：HIGH（高）。

### SEC-002 — 定制的舰载枪械：维和步枪、宪兵 SMG、陆战队步枪（带登舰撞针）
断言：`nsv13/code/game/objects/items/custom_guns.dm` 定义了舰船的重型枪械。
面向玩家的后果：这些是警官/陆战队从军械库或小队补给机领取的武器；陆战队步枪默认
出厂即带一把登舰撞针。
证据：`/obj/item/gun/ballistic/automatic/peacekeeper` "M2A45 security pulse rifle"
（:4-26）——磁性 6mm，弹匣 75，burst 3，`weapon_weight = WEAPON_HEAVY`，背槽位。两种
弹丸：6mm 电击（眩晕，stamina 15，stutter/jitter 5，射程 7，AP 0）与致命 6mm 钨芯
（伤害 10，`armour_penetration = 30`）（:28-69）。`/obj/item/gun/ballistic/automatic/mp_smg`
"MP-16A4 'peacemaker'" 全自动 9mm 无托 SMG（burst 2，WEAPON_HEAVY，:78-101）。
`/obj/item/gun/ballistic/automatic/marine_rifle` "M4A-16A1" 5.56mm 全自动突击步枪，
`pin = /obj/item/firing_pin/boarding`（:103-128）。
`/obj/item/gun/ballistic/shotgun/automatic/pistol` "Solir 4" 弹壳左轮，带
`pin = /obj/item/firing_pin/implant/pindicate`（:130-145）。`/obj/item/gun/ballistic/rifle/boltaction/pdc`
"Point Defence Rifle"，发射 PDC 弹（:155-171）；也是一种 uplink 物品（`uplink_items.dm:13`）。
条件：维和步枪/宪兵 SMG 也作为军械库货运箱出售（`nsv13/code/modules/cargo/packs.dm:440-463,
657-685`，权限 `ACCESS_ARMORY`）。
例外 / 覆写：基础 `automatic` 枪械被强制 `automatic = 1`（:1-2）。
置信度：HIGH（高）。

### SEC-003 — Ju Jitsu 武术（安保的徒手格斗）
断言：安保可以学习 **Ju Jitsu**，一种以一次性训练书配发的连招武术，提供抓取/摔倒
动作，效果随目标已有的减速程度而缩放。
面向玩家的后果：一名训练有素的宪兵可以绊倒、投摔、锁臂和勒昏嫌疑人，造成耐力与
缺氧伤害以及长时间麻痹，但每个具名招式都有 5 秒冷却。
证据：`nsv13/code/modules/jobs/security/martial_art.dm`。书籍
`/obj/item/book/granter/martial/jujitsu` "surviving edged weapons"，`oneuse = TRUE`，
传授 `/datum/martial_art/jujitsu`（:6-21）。连招（`check_streak` :46-59）：Takedown =
Disarm,Grab；Judo throw = Harm,Harm,Grab；Armlock = Disarm,Harm,Grab,Grab。
- Takedown（`takedown` :61-80）：需要 `D.total_multiplicative_slowdown() >= 2`
  （"先让他们慢下来"）；Paralyze 2 秒，Knockdown 7 秒，10 oxy，aggressive grab。
- Judo throw（`judo_throw` :82-102）：需要对目标处于持续抓取状态；将其移动到你的身后，
  Paralyze 7 秒，40 oxy。
- Armlock（`armlocking` :109-129）：Knockdown 20（警官也如此），警官承受 15 stamina，
  目标 `Paralyze(70)`，并有 5 秒窗口进行再次抓取。
- Chokehold（`disarm_act` :181-186）：在 `GRAB_NECK` 或更高级别下以抓取 INTENT_GRAB →
  `SetSleeping(200)`。
- Cracked arm（`harm_act` :161-160）：在 `armlockstate` 期间，对胳膊的一次 harm 对该
  胳膊造成 100 stamina。
- 帮助动词 `jujitsu_help`（:35-44）复述连招。
条件：`no_guns = FALSE`（可使用枪械），`deflection_chance = 0`，每招式
`cooldown = 5 SECONDS`。配发给：军事警察（`military_police.dm:142`）、HoS
（`head_of_security.dm:60`）、典狱长（`warden.dm:55`）。（不配发给 MAA。）
例外 / 覆写：若目标地格被阻挡，Judo throw 会阻止位移（`is_blocked_turf`，:90-91）。
置信度：HIGH（高）。

### SEC-004 — 安保等级：五种状态，效果逐步升级
断言：NSV 使用五种安保等级；其中两个 NSV 专有的是 **Condition Zebra** 以及其余等级
的军事化命名。
面向玩家的后果：警报等级是任务术语，而非空间站的 "code red"。
证据：定义于 `code/__DEFINES/misc.dm:50-54`：GREEN=0 "condition 3"，BLUE=1 "condition
2"，RED=2 "general quarters"，ZEBRA=3 "condition zebra"，DELTA=4 "delta"
（`get_security_level`/`num2seclevel`/`seclevel2num`，`security_levels.dm:103-141`）。
`set_security_level`（`security_levels.dm:10-101`）：
- RED：提升时以 `action_stations.ogg` 播放 `gq_announce`；舱内红光开启，45 秒后
  自动关闭；撤离舱变为 `admin_controlled = 0`（玩家可发射）；火警更新。
- ZEBRA：`condition_zebra.ogg` 宣告；红光 30 秒后自动关闭；舱体由玩家控制。
- DELTA：仅宣告（无灯光切换），舱体由玩家控制。
- 从 GREEN/BLUE 提升至 RED/ZEBRA 会缩短紧急穿梭机计时器（`SSshuttle.emergency.
  modTimer`）。
条件：`set_security_level` 忽略超出范围的值，并在等级未变化时空转（:24-25）。它触发
`COMSIG_GLOB_SECURITY_ALERT_CHANGE` 并记录一次 blackbox 统计。
设置者：通讯控制台（需要 `ACCESS_CAPTAIN` 或硅基；`communications.dm:116-145`）、
钥匙卡认证（两张 ID，`keycard_authentication.dm:159-167`）、AI 死亡
（`ai/death.dm:50` → red）、AI 星图将某艘舰船锁定为目标
（`ai-skynet.dm:1619-1621`）、核弹/malf/blob/cult 终局（delta）。
例外 / 覆写：**DELTA 无法被撤销**——通讯控制台在 delta 下阻止更改
（`communications.dm:137-140`），钥匙卡认证在 delta 下拒绝红色警报
（`keycard_authentication.dm:88-91,147-149`）。管理员可设置任何等级（`randomverbs.dm:850`）。
置信度：HIGH（高）。

### SEC-005 — Condition Zebra 密封每一扇防火门
断言：Zebra 是唯一会全舰强制关闭防火门（window firelocks）的等级；其他等级会重新
打开它们。
面向玩家的后果：Zebra 即 "战备 / 船体完整性"——所有防火卷帘落下，将舰船隔舱化以
抵御破口和登舰者。
证据：`nsv13/code/modules/security_levels/security_levels.dm`。`firedoor/open()`
（:16-22）：门一旦打开，若等级为 ZEBRA 它会在 3 秒后自行重新关闭。
`on_alert_level_change`（:24-42），注册于 `COMSIG_GLOB_SECURITY_ALERT_CHANGE`
（:12-14）：在 ZEBRA 时 → `close()`；在任何其他等级时 → `open()`（除非该区域着火 /
保压 / 被焊接 / 断电）。
条件：`is_station_level(z)` 守卫；被焊接/断电的门会被跳过。
`/obj/effect/landmark/zebra_interlock_point` 已弃用（DEPRECATED）并会自行删除
（`:1-10`）——防火门会自动注册，因此 map 制作者不得使用它。
例外 / 覆写：`open()` 覆写仅对 ZEBRA 触发 3 秒的重新关闭计时器；其他等级保持门开启。
置信度：HIGH（高）。

### SEC-006 — 门可被配置为在红色警报时自动打开
断言：带有 `red_alert_access` 标志的门在 RED 或更高等级下解除所有权限要求。
面向玩家的后果：某些已铺设的门（例如权限锁定的舰桥/要害舱室）在 General Quarters
期间对任何人开放。
证据：`code/game/machinery/doors/door.dm:39,54-57`——当
`red_alert_access && GLOB.security_level >= SEC_LEVEL_RED` 时 `check_access_list`
返回 TRUE。当等级升至红色时，`handle_alert`（:76-81）播放门闩声并发出消息。检查
文本会改变以提示解除（:44-52）。
地图用途：`_maps/map_files/Tycoon/Tycoon2.dmm:20313`（以及遗留的 Galactica 地图）。
条件：仅适用于显式设置 `red_alert_access = 1` 的门；该信号仅在初始化时设置了该标志
的情况下注册（:73-74）。
例外 / 覆写：这是 *唯一* 由安保等级驱动而改变门的权限等级的机制。
置信度：HIGH（高）。

### SEC-007 — 登舰撞针（友军误伤控制）——安保摘要
断言：安装有 **登舰撞针** 的武器在友方主舰/采矿舰上时无法开火，除非军械库控制台已
解锁它们且等级 >= 红色，或用户拥有军械库权限；离开友方舰船后它们始终可开火。
面向玩家的后果：长枪（陆战队步枪）无法被用来扫射自己的舰船；一旦登舰者/外遣队
离开友方 Z 层，同一把武器即可使用。
证据：`nsv13/code/game/machinery/computer/boarding_pin.dm`。
`GLOBAL_VAR_INIT(boarding_guns_z_locked, TRUE)`（:1）。
`/obj/item/firing_pin/boarding`（:5-11）：`pin_removeable = TRUE`，`force_replace =
TRUE`，`req_one_access = list(ACCESS_ARMORY)`。`pin_auth`（:13-25）：`allowed(user)`
→ TRUE；若 `on_friendly_overmap(user)` → 返回 `!boarding_guns_z_locked &&
(GLOB.security_level >= SEC_LEVEL_RED)`；否则 TRUE。`on_friendly_overmap`（:27-33）
仅当该 mob 的星图 `role` 为 `MAIN_OVERMAP` 或 `MAIN_MINING_SHIP` 时为 TRUE。控制台
`/obj/machinery/computer/boarding_guns`（:47-93）：`req_access = ACCESS_ARMORY`，
`INDESTRUCTIBLE`，不可建造；在 "Away only" 与 "General Quarters" 之间切换全局变量。
一盒 10 个撞针 `:36-43`。
条件：`handle_pins`（`gun.dm:309-320`）仅在枪有撞针且 `!no_pin_required` 时调用
`pin_auth`；基础 `/obj/item/firing_pin` 的 `pin_auth` 返回 TRUE（pins.dm:58-59），
因此未修改的枪始终可开火。
与安保相关的含义：标准 **宪兵 Glock-13BR 没有登舰撞针**，在任何地方都可正常开火
——撞针是陆战队/外遣队的控制手段，而非宪兵的。宪兵缺乏 `ACCESS_ARMORY`
（`military_police.dm:19-23`），因此一名宪兵在友方舰船上无法自行授权一把带登舰撞针的
武器，这与陆战队无异。只有 HoS/典狱长（军械库权限）能直接绕过撞针。
例外 / 覆写：被 EMAG 的撞针完全绕过 `pin_auth`（`gun.dm:313`，`pins.dm:41-45`）。
由于 `force_replace`/`pin_removeable` 为 TRUE，撞针可被换成默认撞针——一条明显的
破坏性绕过途径。完整机制见 BOARD-013。
置信度：HIGH（高）。

### SEC-008 — IFF / 应答机安保
断言：舰船的 IFF 信号由 IFF 控制台设置；它可以被 emag，而一旦被 emag，持有一把多
功能工具 2 分钟即可翻转舰船的阵营（登舰胜利条件）。
面向玩家的后果：IFF 控制台是一个高价值、防卫薄弱的目标；让它无人看管，登舰者就能
通过给你的舰船重新贴标签来 "犯下战争罪"。
证据：`nsv13/code/game/machinery/iff_console.dm`。`multitool_act` 仅在 `EMAGGED`
时生效（:79-95）；`hack_goal = 2 MINUTES`（:17）；`hack()`（:124-162）：
`SEND_SIGNAL(OM, COMSIG_SHIP_BOARDED)`，随后翻转 `OM.faction`——syndicate→nanotrasen，
nanotrasen→syndicate（若 `role == MAIN_OVERMAP`，则额外生成一支 Solgov 拦截舰队并
宣告 "Contact with [station] lost"，:141-155），pirate→nanotrasen。`emag_act`
（:97-104）设置 EMAGGED。在 `hacking` 期间，`process()`（:69-77）在公共/辛迪加频道上
周期性地发出 "Unauthorized IFF transponder access detected in [area]!!" 无线电警告
（冷却 400）。登舰子类型 `start_emagged = TRUE`（:64-67）。若舰船仍被 `hammerlocked`，
重建的控制台会重新 emag（:33-49）。
条件：黑入需要控制台被 EMAG；并发的黑入会被阻止；黑入玩家必须持有多功能工具并在
2 分钟的 `do_after` 中存活下来。
例外 / 覆写：未知阵营回退到 `initial(OM.faction)`（:161-162）。
置信度：HIGH（高）。

### SEC-009 — 阵营关系决定友/敌（并驱动舰队敌意）
断言：舰船效忠是一个 `faction` 字符串；`datum/faction` 系统存储成对关系以及预设的
盟友/敌人，从而预设谁与谁交战。
面向玩家的后果：把你的 IFF 翻转为 syndicate 会使 Nanotrasen 阵营的舰队变为敌对；
syndicate 与海盗互为盟友。
证据：`nsv13/code/modules/overmap/factions.dm`。关系常量 ALLIES 200 / NEUTRAL 100 /
DISTRUST 50 / ENEMIES 0 / HATRED -100（:2-6）。`setup_relationships`（:43-59）默认
全部为 NEUTRAL，然后应用 `preset_allies`/`preset_enemies`（hatred）。
`/datum/faction/nanotrasen` 预设敌人 = `{SYNDICATE, PIRATES}`（:126-134）；
`/datum/faction/pirate` 盟友 = `SYNDICATE`，敌人 = `NT`（:171-177）；
`/datum/faction/syndicate` 盟友 = `PIRATES`（:150-159）。阵营累积 `tickets` 并拥有
`victory()` 条件；它们会定期 `send_fleet`。
条件：`check_status(id)` 返回原始关系分数。
例外 / 覆写：阵营胜利文本是通用的，并与游戏模式完成挂钩。
置信度：HIGH（高）。

### SEC-010 — 海盗 / 船员反派物品
断言：NSV 添加了海盗风格的装备和一次海盗突袭事件；海盗船员是一种幽灵角色，会勒索
或突袭舰船。
面向玩家的后果：海盗索取约 80% 的货运预算，若被拒绝，则作为一支登舰/突袭船员生成，
配有海盗无线电和一个战利品定位工具。
证据：
- `nsv13/code/game/objects/items/space_pirate_items.dm`：`/obj/item/radio/headset/pirate`
  + `/obj/item/encryptionkey/pirate`（频道 `RADIO_CHANNEL_PIRATE`，`independent =
  TRUE`）（:3-16）；`/obj/item/loot_locator`（:19-55）——`interact` →
  在 `GLOB.exports_list`（`/datum/export/pirate`）上执行 `find_random_loot`，报告
  "Located: [name] at [area]"，500 tick 冷却；海盗登舰者太空服/头盔（:58-92）。
- `/datum/export/pirate/*`（`code/modules/events/pirates.dm:408-468`）：`ransom`
  （`/mob/living/carbon/human`，指挥人员 3000，其他 1000，"mint condition only"）、
  `parrot`（2000）、`cash`、`holochip`。
- 海盗突袭事件 `code/modules/events/pirates.dm:1-59`：weight 10，min_players 20，
  每回合一次；`/proc/send_pirate_threat` 索要
  `payoff = max(20000, 货运账户的 80%)`；回答 "No way." 或未能支付会生成海盗
  （`spawn_pirates`），它会从幽灵中征集 `ROLE_SPACE_PIRATE`。
- 海盗登舰者套装 `nsv13/code/modules/clothing/custom_outfits.dm:64-97`（lead/sapper/
  gunner；Makarov 手枪，x4 塑胶炸药，栓动步枪）——见 BOARD-004。
条件：x4/塑胶炸药与栓动步枪套装旨在用于破坏和枪战。
例外 / 覆写：一张海盗舱体地图（`_maps/templates/pirate_pod.dmm`）用于玩家登舰者。
置信度：物品/出口部分为 HIGH（高）；事件→登舰者的耦合为 MEDIUM（中）
（pirates.dm 使用自己的穿梭机路径，NSV 星图路径止于出口列表）。

### SEC-011 — Gulag 包（囚犯束缚）与 gulag 积分经济
断言：NSV 引入了 **gulagpack**，一种安保配色的电击包背包，会在无线电指令下电击
佩戴者，且未经他人帮助无法取下；gulag 囚犯可通过打捞敌方弹药来赚取自由积分。
面向玩家的后果：一名被判刑的囚犯可被强制劳动，佩戴一个可远程触发的电击包；认领
敌方导弹/鱼雷残骸会为其 gulag ID 的自由计数器增加数值。
证据：`nsv13/code/game/objects/items/devices/radio/gulagpack.dm`。`WEIGHT_CLASS_HUGE`，
背槽位，`on = TRUE`，`code = 2`，`frequency = FREQ_ELECTROPACK`（:12-18）。
`receive_signal`（:71-91）：若佩戴者是活体 mob 且 `on`（100 tick 冷却），使其随机
沿一个基本方向移动一步，迸出火花，并 `L.Paralyze(100)`（10 秒）。`attack_hand`/
`MouseDrop` 拒绝自行取下（"You need help taking this off!"，:35-49）。对头盔使用
`attackby` 会制作一个 `shock_kit`（:51-69）。可通过 protolathe 设计
`/datum/design/gulagpack` 打印（安保部门；iron 10000，glass 2500）（`weapons.dm:81-89`）。
Gulag 经济：`/obj/item/card/id/gulag`（`code/game/objects/items/cards_ids.dm:669-716`）
追踪 `goal`/`points`/`permanent`；敌方 `missile_types.dm:16-30` 与
`torpedo_types.dm:17-29` 暴露 `claimable_gulag_points`（导弹 50，鱼雷 75），用一张
gulag ID 击打它们即可认领。
条件：电击仅影响 `loc` 位于包内的 mob；代码必须匹配。
例外 / 覆写：存在 `suicide_act`（FIRELOSS）。基础 SS13 的 gulag 传送器 / 物品回收机
（`code/game/machinery/gulag_teleporter.dm`，`gulag_item_reclaimer.dm`）负责发送囚犯，
并在 `points >= goal` 且非永久时释放他们。
置信度：HIGH（高）。

### SEC-012 — 常设安保职业、装备与军械库内容
断言：NSV 舰船配备 HoS + 典狱长 + 至多 10 名军事警察（安保官）+ 侦探 + 医务官；MAA
是一个独立的军火角色。
面向玩家的后果：安保开局携带弹道随身武器、闪光弹、手铐、警棍、泰瑟和 Ju Jitsu 手册；
军械库存放着更重的装备。
证据：`config/jobs.txt:7,11,48-51`（Head of Security 1，Master At Arms 1，Warden 1，
Detective 1，Security Officer 10，Brig Physician 1）。军事警察套装
`military_police.dm:125-152`：`belt = /obj/item/storage/belt/security/full`，
`suit_store = glock/security`，`l_pocket = handcuffs`，`r_pocket = flash`，背包 =
{glock 橡胶弹匣，泰瑟，弹匣架，PDA，**Ju Jitsu 手册**，小队寻呼机，经典警棍}。制服
"military police uniform"，护甲 "Military Police Armour"（melee 25，bullet 40，
laser 15，覆盖 CHEST|GROIN|LEGS）（:178-199）。HoS 套装 `head_of_security.dm:41-70`
（"peacekeeper" 制服；背包 = {装满的警棍，泰瑟，弹匣架，Ju Jitsu，警棍}；变色龙额外项
= Winona）。典狱长 `warden.dm:38-64`（suit_store glock/security；背包 = 警棍，泰瑟，
弹匣架，glock 弹匣，寻呼机，警棍）。军械库储物柜
（`code/game/objects/structures/crates_lockers/closets/secure/security.dm`）：HoS 储物柜
现在发放 **Winona**（glock/security/hos）而非能量枪，并包含 `ammo_box/c9mm/rubber`
（:156-160）；典狱长储物柜有一把 **紧凑型战斗霰弹枪**、`shield/riot/flash`
"strobe shield"、`ammo_box/c9mm/rubber` 与 `door_remote/head_of_security`（:201-203）；
安保官储物柜增加了橡胶 9mm 以及通常的背心/头盔/HUD/手电（:211-219）。存在 Solgov 变体
（`nsv13/.../custom_closets.dm:199-278`），配有 `gun/energy/laser/retro`（Solgov
"no ballistics" 主题）。
条件：所有安保职业都携带 `/obj/item/implant/mindshield`。
例外 / 覆写：配置可通过 `check_config_for_sec_maint()` 增加维护通道权限。
置信度：HIGH（高）。

### SEC-013 — 小队动员将船员征召为副手以击退登舰者
断言：在 General Quarters 期间，船员被组织成小队；**Security Support（安保支援）**
小队获得禁闭室/安保权限，并可从小队补给机武装（受武器许可约束）。
面向玩家的后果：普通船员可被推入反登舰战斗，甚至在指挥层的裁量下被授予禁闭室/门
权限。
证据：`nsv13/code/modules/squads/squad_manager.dm:9-16`——
`role_access_map[SECURITY_SQUAD] = {ACCESS_BRIG, ACCESS_SEC_DOORS, ACCESS_TRANSPORT_PILOT,
ACCESS_HANGAR}`；安保目标文本（`:24-26`）为 "assist security in repelling boarders
and participate in boarding actions"。`squad_computers.dm`（小队管理计算机，
`req_one_access = ACCESS_HEADS`）切换 `squad.access_enabled`（:83-89）与
`squad.weapons_clearance`（:116-120）。小队补给机（`squad_vendor.dm`，
`req_one_access = {HOP, HOS}`）对套件的发放进行门控：若 `!squad.weapons_clearance`，
所有 `/obj/item/ammo_box` 与 `/obj/item/gun` 会从套件中剥离（:37，:90-95）。套件
（`/datum/squad_loadout/nt|solgov/*`，:176-266）给予陆战队装备 + 一把带致命弹匣的
Glock。
条件：小队被隐藏/命名（Able、Baker、Charlie、Duff）；`disallowed_jobs` 包含所有安保
职业、指挥、工程、医疗（`squad_datum.dm:21-26`）——因此安保 **不会** 被编入小队；
Security Support 小队由普通船员（`assistant`/陆战队）填充。
例外 / 覆写：XO/HoS（小队）与舰长（警报等级）是控制点。
置信度：HIGH（高）（权限/许可）；对回合内船员实际承接情况为 MEDIUM（中）。

### SEC-014 — 处理船员反派：受保护职业与致命性阶梯
断言：与安保相关的职业受保护，不会被叛徒/革命招募，且该部门被期望按橡胶 → 泰瑟 →
致命的顺序升级武力。
面向玩家的后果：叛徒永远无法抽到 MP/典狱长/HoS/舰长/飞行员/MAA，革命也排除所有
安保；因此安保必须对 *其他* 船员反派使用武力。
证据：`code/game/gamemodes/traitor/traitor.dm:16` `protected_jobs = {SECURITYOFFICER,
WARDEN, HEADOFSECURITY, CAPTAIN, PILOT, MASTERATARMS}`。
`code/game/gamemodes/revolution/revolution.dm:17` `restricted_jobs` 包含 SECURITYOFFICER、
WARDEN、DETECTIVE、CAPTAIN、HOS、MASTERATARMS（以及 AI、工程、指挥）。致命性层级来自
SEC-001（默认橡胶 → 泰瑟 → 致命/穿甲/燃烧）。
条件：所有安保职业带有 `TRAIT_LAW_ENFORCEMENT_METABOLISM` mind 特质。
例外 / 覆写：MAA 作为 "安保邻近" 职业受到保护，尽管它属于军火。
置信度：HIGH（高）。

### SEC-015 — 舰船安保不同于空间站安保（军械库、禁闭室、战时框架）
断言：在军舰上，安保规模更小、橡胶优先、仅限弹道，并受警报等级战时规则治理，而非
空间站的企业守则。
面向玩家的后果：警官更少，一个其重型致命武力必须被解锁的军械库，以及一个能够宣布
General Quarters / Condition Zebra 并在全舰范围内重新配置权限和防火门的指挥层
（舰长/HoS）。
证据：人员编制来自 `config/jobs.txt`（10 名 MP 对比空间站常态）；仅限弹道主题
（SEC-001、SEC-012）；警报等级效果 SEC-004/005/006；登舰撞针的军械库授权（SEC-007）；
NSV 上的钥匙卡认证设备增加了 `KEYCARD_FTL_SAFETY_OVERRIDE`
（`keycard_authentication.dm:7, 116-119,195-204`）——安保可以凭借两张钥匙卡解除 FTL
跃迁引擎安全锁。
条件：`Master At Arms` 及其他军衔通过阵营军衔文件
（`config/ranks/{corporate,military,royal_navy,sharpe}.txt`；例如 military：HoS=COL，
MAA=WO，Warden=GSGT，MP=LCPL）显示，强化了军事化框架。
例外 / 覆写：在 conquest/PvP 游戏模式中，对应的辛迪加船员角色包括一名
"Syndicate Marine Sergeant"，其 "leads Syndicate shipside security forces in
repelling boarders"（`nsv13/code/game/gamemodes/pvp/roles.dm:358-360`），以及一名
"Requisitions Officer" 作为 "defacto master at arms"（:257-259）——敌方一侧的同一
反登舰角色。
置信度：机制部分为 HIGH（高）；"框架" 综述为 MEDIUM（中）。

## 跨系统依赖

- **系统 17（登舰 / BOARD-013）：** 登舰撞针作为一种登舰机制被定义和控制；SEC-007/008
  概述了它面向安保的约束以及 IFF 夺取。`spawn_boarders`、KNPC 登舰者与幽灵角色登舰者
  位于 `nsv13/code/modules/overmap/boarding/` 以及舰队 AI（`ai-skynet.dm`）中。
- **系统 23（小队）：** `squad_manager.dm`、`squad_vendor.dm`、`squad_computers.dm` 是
  SEC-013 所引用的船员动员层；陆战队装备细节属于系统 23。
- **系统 24（船员反派）：** 叛徒/革命受保护职业列表（SEC-014）；登舰者反派 datum
  （`nsv13/code/modules/antagonists/boarders/boarders.dm`）。
- **系统 8/11（星图 / 阵营 / IFF）：** `factions.dm` 关系、`iff_console.dm`，以及
  NT→syndicate 翻转时的 Solgov 拦截响应（SEC-008/009）。
- **弹药：** gulag 积分经济从敌方导弹/鱼雷认领（`missile_types.dm`、`torpedo_types.dm`）；
  维和步枪/SMG/陆战队步枪是军械库货物（`cargo/packs.dm`）。
- **钥匙卡认证：** `code/modules/security_levels/keycard_authentication.dm`（红色警报、
  维护通道、BSA 解锁、FTL 安全覆写）。

## 未解问题

1. `SEC-006` 的 `red_alert_access` 仅在少数已铺设的门上设置（Tycoon2，以及遗留的
   Galactica 地图）；现役旗舰地图是否使用它未被穷尽确认——该机制可能在这些地图之外
   基本处于休眠状态。
2. `SEC-010`：海盗 *突袭事件*（`code/modules/events/pirates.dm`）生成自己的穿梭机和
   幽灵海盗；它并未明显接入 NSV 星图的 `spawn_boarders` 路径。登上玩家舰船的海盗
   登舰者是来自此事件还是来自舰队 AI 的 `AI_FLAG_BOARDER`，此处未做端到端追踪
   （属系统 17 范畴）。
3. 登舰撞针的 `pin_auth` 将 `MAIN_MINING_SHIP` 视为 "友方"；*采矿* 舰（一艘独立的
   外遣舰船）如何获得带登舰撞针的武器或一座登舰控制台未被检查。
4. 宪兵是否刻意 *缺少* `ACCESS_ARMORY`（从而无法自行授权带登舰撞针的武器）是设计
   意图还是基础权限列表的疏漏，代码中未说明。
5. `ammo_box/c9mm`（致命）弹药盒在本文件中没有显式的 `max_ammo` 覆写，而 AP/燃烧
   变体设置为 30——该致命弹药盒的容量默认值未与基础定义核对。
6. 小队 "Security Support" 动员需要一名主管（XO/HoS）在小队计算机上切换权限/许可；
   船员实际中的守卫行为是一种游玩模式断言，而非代码保证。
