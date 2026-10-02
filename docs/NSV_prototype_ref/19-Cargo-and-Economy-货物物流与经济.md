> 本文为 research/evidence/cargo_economy.md 的中文翻译。

# 货物、物流与经济

系统 #19 的研究笔记。事实来源是 `D:\code\NSV13` 中撰写时检出的代码提交。交叉引用：`missions_objectives.md`（系统 6 —— 货运/目标树）、`munitions.md`（系统 10 —— 鱼雷/货物发射器）、`round_flow.md`。

## 系统概览

一艘 NSV 战舰上存在**两条独立的补给管线**，外加一条信用点（credit）管线：

1. **标准补给控制台 -> 补给穿梭机**（基础 /tg/ 货物，未修改的机制）。
   舰船拥有一个真实的 `supply_home` 停靠口（例如 `_maps/map_files/Aetherwhisp/Aetherwhisp2.dmm:28701`），
   因此普通的控制台订购循环可以正常工作。货箱从 CentCom 购买并飞运到舰船。
   证据：`code/modules/cargo/orderconsole.dm`、`code/modules/shuttle/supply.dm`。

2. **货运目标 -> 货物发射器 -> 货运鱼雷**（NSV 特有，"信使"物流）。
   船员会接到补给请求目标，把物品装进一枚货运鱼雷，然后通过 Cargo DRADIS 控制台，用 M4-C 货物货运发射器将其发射到目标空间站。这是 freight_type/目标机制被使用的*唯一*地方。
   证据：`_cargo.dm`、`objective_cargo.dm`、`nsv13/code/modules/overmap/ai-skynet.dm`、`cargo_launcher.dm`。

3. **经济**：部门银行账户由一个回合预算池供资；船员通过工资、出口和外快（bounty）赚取信用点。跨图层目标**不**支付信用点（见 CARGO-010）。

`nsv13/code/modules/cargo/packs.dm` 只是**增加**补给包（它并不替换控制台）；
`nsv13/code/modules/cargo/space_catalog.dm` **不是**订购目录 —— 尽管文件名如此，它定义的是 `/obj/item/book/space_yellow_pages`，一本商人名录书。

## 核心玩法循环

- **购买货品**：工程/货物控制台 -> 购物车 -> 发送穿梭机。穿梭机离开 CentCom 时装载已付费的货箱；穿梭机抵达 CentCom 时出售船员装载的任何东西。（CARGO-001/002）
- **赚取信用点**：部门预算每 5 分钟自动补充（CARGO-003）；在穿梭机上出售出口品（CARGO-004）；领取外快（CARGO-005）。
- **货运任务**（仅信使游戏模式）：阅读补给请求表单，备货/寻找物品，装载一枚货运鱼雷（最多 4 个槽位），将其装入 M4-C 发射器的膛室，然后通过 DRADIS 投递。空间站会精确校验内容物（不允许多余的垃圾），并回寄一份盖章的表单。（CARGO-006..009）
- **目标完成没有信用点奖励**；回报是威胁度降低以及空间站交还的东西（投递的货物 / 盖章的文书）。（CARGO-010）

## 机制

### CARGO-001
断言：标准补给控制台的订购经由补给穿梭机进行。购买阶段在穿梭机离开 CentCom 朝向舰船时（`supply_away`）运行：它从 Cargo 部门账户扣费并装载所订购的货箱，然后飞往舰船的 `supply_home` 停靠口。出售阶段在穿梭机从舰船被送往 CentCom 时运行。此处没有 NSV 覆写。
面向玩家的后果：普通的货物订购在战舰上的运作方式与空间站完全一样 —— 你先下单，然后发送/呼叫穿梭机。注意订单是在*返程*时扣费，而不是在购物车确认时。
证据：`code/modules/cargo/orderconsole.dm:163-213`（`send` 动作调用 `SSshuttle.moveShuttle`），`code/modules/shuttle/supply.dm:76-127`（`initiate_docking`："buy when we leave home" 在 `getDockedId() == "supply_away"` 时触发 `buy()`；"sell when we get home" 在停靠于 `supply_away` 之后触发 `sell()`；`buy()` 向 `get_dep_account(ACCOUNT_CAR)` 或付费账户 `*1.1` 扣费），舰船地图包含 `id = "supply_home"`。
条件：控制台 `can_send` 为 TRUE（不是仅请求的变体）；穿梭机未被阻挡（`SSshuttle.supplyBlocked`）。
例外 / 覆写：`requestonly` 控制台只提交请求；`self_paid` 向 ID 持有者的个人账户扣费，另加 10% 的手续费，该手续费记入 Cargo 预算（`supply.dm:111-125`）。标记为 `DropPodOnly` 的补给包无法在此订购（`orderconsole.dm:151,321`）。
置信度：HIGH（高）

### CARGO-002
断言：货箱只有在穿梭机被召回时才会送达；如果它从未被派出，则什么也不会被购买；如果订购的货品超出了穿梭机的空闲地格，则发出的货箱会更少（循环在 `!empty_turfs.len` 时中断）。
面向玩家的后果：Atlas 地图本身就警告 "Do not order more than 6 crates at once"（`_maps/map_files/Atlas/atlas.dmm:9313`）—— 这是小型穿梭机停靠口的一个真实玩法约束。
证据：`code/modules/shuttle/supply.dm:96-108`（`empty_turfs` 守卫，`break`）。
条件：穿梭机大小 / 停靠区面积。
置信度：HIGH（高）（机制），MEDIUM（中）（确切的货箱上限取决于地图）

### CARGO-003
断言：每个部门银行账户都会自动获得资金。经济子系统每 5 分钟触发一次，向每个普通账户发放工资（`payday(1)`，从该账户所属的部门提取），并调用 `distribute_funds(14000)`，按加权份额分配：Engineering 2、Security 2、Medical 2、Science 2、Cargo 2、**Munitions 2**、Service 1、Civilian 1（14 份 => 每份 1000 => 每个 2 份的部门获得 2000）。初始化时每个部门被注入 `round(25000/8) = 3125`。
面向玩家的后果：Cargo 通常在回合开始时约有 3125，并且每 5 分钟获得约 2000；个人工资从同一部门资金池中支出，如果资金池枯竭则可能发放失败。
证据：`code/controllers/subsystem/economy.dm:8-9,34-40,46-114`；`economy.dm:62-88`（账户补偿），`code/modules/economy/account.dm:62-88`（`payday`，若部门缺资金则中止）。
条件：`SSeconomy` 在 `RUNLEVEL_GAME` 下运行。
例外 / 覆写：`budget_pool = 25000`；`VOTE` —— VIP 账户被注入 `rand(8888888,11111111)`（`economy.dm:1,39`）并在每次触发时补足，因此它永远不会耗尽。
置信度：HIGH（高）

### CARGO-004
断言：船员通过把物品装入补给穿梭机并将其送往 CentCom 来赚取信用点，CentCom 会针对全局出口清单运行 `export_item_and_contents`。
面向玩家的后果：出口循环是主要的 "变卖战利品" 渠道；出口扫描仪会在运送前显示物品的价值。在基础上新增的 **NSV 特有出口品**：
- 钚燃料棒：**9000** 信用点（`nsv13/code/modules/cargo/exports/engineering.dm:1-4`）。
- 辛迪加空投兵头盔 **300**、护甲 **700**、辛迪加行李袋 **200**、Stechkin 手枪 **250**
  （排除子类型）、C-20R 冲锋枪 **500**、辛迪加耳机/加密密钥 **500**
  （`nsv13/code/modules/cargo/exports/syndie.dm:4-33`）。
- 基础材料出口保留：等离子体 200、钻石 500、香蕉矿 1000、钛/金 125、
  铀 100、银 50、铜 15、铁/玻璃 5（按 `MINERAL_MATERIAL_AMOUNT`），位于
  `code/modules/cargo/exports/materials.dm`。
证据：`supply.dm:172-212`（`sell()` -> 在 Cargo 账户上 `D.adjust_money(ex.total_value[E])`），
`nsv13/code/modules/cargo/exports/engineering.dm`、`exports/syndie.dm`、
`code/modules/cargo/export_scanner.dm:38-58`。
条件：收益进入 `get_dep_account(ACCOUNT_CAR)`（Cargo 预算）。
例外 / 覆写：出口清单通过 `setupExports()` 惰性构建；`SSshuttle.export_categories`
由控制台的 `get_export_categories()` 设置，因此被 emag/违禁品控制台会解锁更多内容
（`orderconsole.dm:49-54,188`）。
置信度：HIGH（高）

### CARGO-005
断言：外快（来自外快控制台）在被领取时会支付一笔分配给**所有**部门的信用点。奖励文本 = `reward * bounty_modifier`；领取时调用
`distribute_funds(reward * bounty_modifier * 3)`，其中 `bounty_modifier = 3`。
面向玩家的后果：一笔 1000 信用点的外快会分配出 9000 信用点，分摊到各部门。
在穿梭机上运送符合外快条件的物品也能完成它们（`bounty_ship_item_and_contents`）。
NSV 新增了一笔外快："Syndicate Drop Trooper Equipment"，奖励 **5000**，`required_count = 5`
（五种所列类型中的任意一种，仅计数 —— 并非要求成套）。
证据：`code/modules/cargo/bounty.dm:6,16,22-24`；`code/controllers/subsystem/economy.dm:27`；
`nsv13/code/modules/cargo/exports/syndie.dm:36-41`。
条件：外快未被领取；拥有控制台权限。
置信度：HIGH（高）

### CARGO-006
断言：**货运鱼雷**是所有货运目标的投递容器，最多容纳 **4 件物品**（`max_stuff = 4`）。物品通过点击拖拽到鱼雷上装载；撬棍可卸载（或者里面的乘员可以破出）。
面向玩家的后果：单次货运最多可携带四个 "槽位" 的货物；分组 docstring 明确警告分组目标不得超过 4 种预包装物品类型。
证据：`nsv13/code/modules/munitions/ammunition/torpedos/torpedo_types.dm:115-178`；
`nsv13/code/datums/freight_type/group/_group.dm:8-9`。
条件：物品不得被锚定 / 过重（`try_load`）。
例外 / 覆写：内容物可以包含一个货箱（嵌套），因此有效物品数取决于里面包装了什么。
置信度：HIGH（高）

### CARGO-007
断言：**M4-C 货物货运发射器**（`/obj/machinery/ship_weapon/torpedo_launcher/cargo`）将货运鱼雷发射至一个空间站。它并非战术武器：它不注册跨图层武器 datum，并且是从 **Cargo DRADIS 控制台**（`/obj/machinery/computer/ship/dradis/minor/cargo`）发射的，而不是战术控制台。
面向玩家的后果：物流与战斗炮术是分开的；弹药技师仍须给鱼雷上膛并解除保险（按控制台印出的说明），然后使用 cargo DRADIS 来瞄准接收方。
证据：`cargo_launcher.dm:1-45`（`link_to_overmap_weapon_datum` 返回 no-op，`weapon_datum_type = null`），
`nsv13/code/modules/overmap/radar.dm:135-177,296-298`（cargo dradis 上的 `hail` 动作调用
`target.try_deliver`），`radar.dm:143-154`（游戏内说明纸张）。
条件：控制台必须链接到发射器（地图 ID 或多功能工具缓冲交换）；发射器已上膛、保险已解除、cargo DRADIS 在 `hail_range` 内。
例外 / 覆写：发射器通过 `GLOB.blacklisted_cargo_types` 拒绝危险内容物（活体生物、核装置、AI 容身机器、传送器等）—— 见 `nsv13/code/modules/overmap/ai-skynet.dm:399-405` 中的 `try_deliver`；清单位于 `code/modules/shuttle/supply.dm:1-25`。
置信度：HIGH（高）

### CARGO-008
断言：投递由接收站**精确**校验。`check_objectives` -> `objective.check_cargo` 仅在以下情况批准：(a) freight_type 分组接受所请求的内容物，且 (b) **没有**未追踪的 "垃圾" 残留。匹配是针对容器内容物的索引清单进行的；储物柜和货运鱼雷本身被忽略（`blacklisted_paperwork_itemtypes`）。成功后，目标的 `status` 被设为 1，空间站会致谢并经由 supplypod 回寄一份盖章的请购单。
面向玩家的后果：你不能 "把所有东西堆进去" 来一次性完成多个目标，除非每件物品都被单独请求；错误/多余的货物会被拒收，货物会在延迟后被退还（或没收）。
证据：`nsv13/code/game/gamemodes/overmap/objectives/cargo/_cargo.dm:165-191`，
`nsv13/code/modules/overmap/ai-skynet.dm:455-484,575-589`，
`nsv13/code/datums/freight_type/single/_single.dm:3-7`（typecache）。
条件：接收方为同阵营，除非该商人是 "inhabited_trader"（adminbus）；
`nsv13/code/modules/overmap/ai-skynet.dm:591-621`。
例外 / 覆写：如果空间站并不期待任何货物，它会以 "unexpected shipment" 为由拒收，并视 `returns_rejected_cargo` 决定是否退还货物
（`ai-skynet.dm:543-561`）。
置信度：HIGH（高）

### CARGO-009
断言：货运目标分为两大族，且**仅由 Courier 游戏模式分配**：
- **donation（捐赠）**：矿物（某随机矿物的 50 张）、血液（一种血型，目标 200 单位）、
  医疗化学品（>=1 种药品，约 90 单位分摊到化学试剂*或*药丸/贴片容器中）、
  食物（某随机菜肴 3-5 份）、弹药（某随机弹药物品 6-12 件）、社交物资
  （蛋糕 + 200u 乙醇 + 3 份包装好的礼物）。
- **transfer（转运）**：预包装的安全投递品 —— 物理信用点（`rand(3,15)*1000` 信用点全息芯片）、
  科技数据磁盘、机密文件、应急 EVA 物资（罐/服/盔/面罩各 5 个，`REQUIRE_ALL`）、
  随机战斗机部件（2-4 件），以及一个活体标本（随机 simple_animal）。
证据：`nsv13/code/game/gamemodes/overmap/courier.dm:24`（`random_objectives = subtypesof(donation)
+ subtypesof(transfer)`），以及 `objectives/cargo/donation/*` 和 `objectives/cargo/transfer/*` 下的每个文件。
条件：`pick_station()` 在回合开始时只瞄准*不同*星系中的 **NT** 空间站
（`_cargo.dm:62-84`）。
例外 / 覆写：转运目标设置 `send_to_station_pickup_point = TRUE` —— 预包装的货箱被预先放置在一个空间站，船员必须飞过去并在商人菜单中索取它，然后再将其投递到目的地（`_cargo.dm:29-34,86-119`）。矿物捐赠刻意将 Shallowstone（矿物）商人排除作为目的地（`donation/minerals.dm:21-44`）。
置信度：HIGH（高）

### CARGO-010
断言：完成跨图层货物目标**不支付任何信用点**。整个流程（空间站致谢 "thank you"、回寄盖章表单）不包含针对货物目标的任何 `adjust_money`/全息芯片支付。唯一的奖励是被动的：每个*新*完成的目标会降低威胁度 `TE_OBJECTIVE_THREAT_NEGATION = 50`，并推动游戏模式向胜利/成就迈进。
面向玩家的后果：船员不会因货运任务获得报酬；激励是威胁缓解和回合推进，而不是 Cargo 预算。
证据：`check_cargo` 仅设置 `status = 1`（`_cargo.dm:187-191`）；`objectives/cargo/` 下任何地方都没有金钱调用；威胁度降低位于 `overmap_mode.dm:476-478`；定义位于
`nsv13/code/__DEFINES/skynet.dm:30`；`approve_shipment` 只发送文书（`ai-skynet.dm:575-589`）。
条件：——
例外 / 覆写：**已弃用**的 `/datum/nsv_mission` 层有一个 `payout()`，它会发送一个预装的全息芯片（`nsv13/code/datums/missions.dm:71-83`），但整个系统是死代码
（见 `missions_objectives.md`；在其自身文件之外从未实例化任何 `/datum/nsv_mission`）。
置信度：HIGH（高）

### CARGO-011
断言：Cargo 部门的预算是舰船物流的通货，而 Munitions 共享同一机制。两者都获得每次 `distribute_funds` 支付中的 2 份。另外，击杀辛迪加 AI 舰船**应当**支付一笔现金外快，但该支付 proc 从未被调用。
面向玩家的后果：摧毁敌对目标目前**不**会向船员支付信用点；`bounty_pool` 只会不断累积。（很可能是疏漏 / 死功能。）
证据：`NSV syndicate ships add to the pool` —— `nsv13/code/modules/overmap/types/syndicate.dm:87,101-103`
（`bounty = 1000` 为基础，主力舰为 15000/20000）；支付 proc `bounty_payout()` 位于
`nsv13/code/controllers/subsystem/starsystem.dm:370-379`，在 ACCOUNT_CAR 与
ACCOUNT_MUN 之间分配资金池 —— 但全仓库 grep 发现**没有调用者**调用 `bounty_payout()`。
条件：——
例外 / 覆写：`bounty_pool` 从 0 开始；某些舰船设置了更大的 `bounty` 值。
置信度：HIGH（高）（支付未被调用），MEDIUM（中）（将其解读为无意的）

### CARGO-012
断言：与空间站交易**仅单向的买方**。在商人处购买物品会花费 Cargo 预算（NT 舰船）或*辛迪加*预算（非 NT 舰船）。没有 UI 动作可以向商人出售货物；唯一的出售渠道是补给穿梭机出口循环。
面向玩家的后果：你不能把战利品销赃给空间站商人；出口请使用穿梭机。
证据：`nsv13/code/modules/overmap/traders.dm:346-354,376-383,435-459`（仅有 `purchase`/`receive_cargo`；账户按阵营选择），`ui_act` 中没有 `sell` 动作。
条件：在 30 个跨图层地格内（"双向通讯范围"），舰船必须是大舰
（`linked_areas`，而非小型飞行器/小行星）。
置信度：HIGH（高）

### CARGO-013
断言：`nsv13/code/modules/cargo/space_catalog.dm` 是 **Space Yellow Pages** 书
（`/obj/item/book/space_yellow_pages`），一本可阅读的附近商人名录，而非订购目录。它列出同一星区内 `TOO_FAR = 40` 范围内的商人，过滤掉受制裁的阵营，缓存 `2 MINUTES`，并以 50% 的概率随机省略远程（>`SORT_OF_CLOSE = 20`）商人。
面向玩家的后果：这本书是决定飞往何处/订购何物的情报辅助，不是购买 UI。
证据：`nsv13/code/modules/cargo/space_catalog.dm:1-69`。
条件：需要一个商人所有者调用 `set_basic_info`（在商人上生成）。
例外 / 覆写：跳过 `/datum/trader/randy`。
置信度：HIGH（高）

### CARGO-014
断言：`nsv13/code/modules/cargo/mission_cargos.dm` 和 `mission_rewards.dm` 属于已死的 `/datum/nsv_mission` 层（一个带有防篡改功能的大型 "货箱" 以及一个战斗补给弹药箱）。
存活的货物目标货箱是 `/obj/structure/closet/crate/large/freight_objective`
（`objective_cargo.dm`），它支持针对标本货箱的幽灵意识（ghost-sentience）投票，并在不可替换的密封货箱被摧毁时使目标失败。
面向玩家的后果：用撬棍打开一个密封的目标货箱会警告你，并可能导致任务失败；标本货箱可能被幽灵接管。
证据：`nsv13/code/modules/cargo/mission_cargos.dm:1`（"now deprecated by overmap gamemode cargo
objectives"）；`nsv13/code/modules/cargo/objective_cargo.dm:9-53`。
条件：货箱为 `allow_replacements = FALSE`。
例外 / 覆写：——
置信度：HIGH（高）

### CARGO-015
断言：NSV 货物包是新内容和价格/货箱换皮的混合体，全局注册（因此在任何控制台、舰船或空间站均可用）。值得注意的 NSV 补给包与成本：Naval Artillery Shells x10 **2000**；Cannonballs x10 **1000**；Torpedo construction kit **3000**；Missile construction kit **2500**；Standard torpedo warheads x10 **1500**；Armour-piercing warheads x10 **2500**；Freight torpedo warheads x3 **350**；Light/Heavy/Utility Fighter Starter Kits **9000/15000/10000**；Stormdrive reactor core **35000**；APNW machine boards **25000/50000**；reactor control rods **3000/8000**。
唯一仅 NSV 的分组标签页是 **"Munitions"**（权限为 Munitions 或 Syndicate Requisitions，安全武器箱）；其他 NSV 补给包被重新挂到已有的基础分组下（Security/Armory、Engineering、Medical、Materials、Service、Organic）。
面向玩家的后果：货物直接供应弹药与战斗机经济（交叉引用 `munitions.md` / `fighters.md`），并且 Munitions 可以批量购买弹头/弹药。
证据：`nsv13/code/modules/cargo/packs.dm:1-5,6-347` 等。
条件：如果空间站具有 `TRAIT_DISTANT_SUPPLY_LINES` / `TRAIT_STRONG_SUPPLY_LINES`，补给包成本会乘以 1.2x/0.8x（`code/modules/cargo/packs.dm:37-42`）。
例外 / 覆写：`hidden`/`contraband` 补给包需要被 emag 或违禁品控制台。
置信度：HIGH（高）

### CARGO-016
断言：货运弹头是一种**研究得到**的原型物品（`/datum/design/freight_warhead`，分类 "Advanced Munitions"，部门 Munitions），在 protolathe 中由 **5000 铁、1000 玻璃、500 铜** 制成；它将一枚鱼雷转化为货运鱼雷。完整的货运鱼雷也可以直接通过货物订购（弹头包 x3，售价 350）。
面向玩家的后果：货物/材料供给弹药研究，而同一货运部件既可通过研究也可通过直接购买获得。
证据：`nsv13/code/modules/research/designs/munitions_designs.dm:139-147`；
`nsv13/code/modules/munitions/ammunition/torpedos/torpedo_parts.dm:29-34`（`build_path`）。
条件：protolathe + 研究已解锁。
置信度：HIGH（高）

## 跨系统依赖 / Cross-System Dependencies

- **系统 6（任务/目标）**：整个货物目标 + freight_type 树是信使游戏模式的投递那一半。没有信用点，只有威胁/胜利。见 CARGO-009/010。
- **系统 10（弹药）**：货物补给包是鱼雷弹头/部件和战斗机套件的主要来源；货运弹头是一个研究物品；货物发射器是一门（非战术的）舰船武器。见 CARGO-007/015/016。
- **系统 20（研究）**：转运/数据目标使用一个 `tech_disk`；货运弹头是一种科技设计；科学补给包（`science` 组）通过同一控制台订购。
- **战斗机**：战斗机起始套件（9000-15000）是直接的货物购买（CARGO-015）。
- **跨图层导航**：货运目标需要 FTL 航行到一个不同星系的 NT 空间站，然后通过 DRADIS 瞄准进行最终的鱼雷投递。

## 未解问题 / Open Questions

1. `bounty_payout()` 从未被调用 —— 是有意为之的死代码还是回归？若是有意为之，则尽管每艘 AI 舰船都定义了 `bounty` 值，击杀辛迪加舰船却不支付任何东西。（见 CARGO-011。）
2. `distribute_funds` 两次为 **Munitions** 账户补足，却从未为实际的辛迪加（`ACCOUNT_SYN`）账户补足：`economy.dm:113` 在 "syndicate" 注释下调用 `get_dep_account(ACCOUNT_MUN)`。那么辛迪加预算是否因此只有其 `rand(...)` 种子，而 Munitions 被双重取用了？
3. 货运目标可否在 `courier` 之外的任何游戏模式中触达？grep 显示只有 `courier.dm` 抽取它们；巡防/舰队似乎省略了它们。
4. `roundstart_paychecks = 5`（`economy.dm:8`）—— 确认它是否/在哪里被消费；在经济子系统文件内未被引用。
5. 完整信使回合实际抵消多少威胁，相对于封锁舰队（超出 50/目标之外）—— 取决于此处未追踪的 `TE_THREAT_PER_HOUR` 交互。
