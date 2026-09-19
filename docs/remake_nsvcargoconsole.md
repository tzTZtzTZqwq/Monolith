# NSV Cargo 控制台重制设计

## 状态与范围

本文档描述 NSV Cargo 控制台的重制方案。目标是复用现有 Cargo 订单 GUI，同时由 NSV 独立负责可买商品、价格、购买校验、CargoHub 扣款及独立网格交付。

## 核心设计

```text
Cargo 订单 GUI
→ 共享商品展示与购买意图
→ 按控制台组件分派后端
  ├── 普通 CargoSystem.Orders
  └── NsvCargoPurchaseSystem
      ├── 解析当前市场
      ├── 构造可买商品目录
      ├── 重新计算价格
      ├── 从 CargoHub 扣款
      └── 向 NSV 独立网格交付
```

GUI 只负责展示商品和发送购买意图，不拥有市场规则。普通 Cargo 与 NSV 可以使用同一个客户端窗口，但服务端购买处理器必须保持独立。

## 复用范围

优先复用：

- `CargoOrderConsoleBoundUserInterface`；
- `CargoConsoleMenu`；
- `CargoConsoleOrderMenu`；
- `CargoOrderRow`；
- `CargoConsoleInterfaceState`；
- `CargoOrderData`。

如果现有共享状态无法表达动态价格、不可购买原因或市场版本，应增加通用可选字段，避免建立平行的 NSV 购买 GUI 和 DTO。

## 商品目录

NSV 商品目录由服务器根据控制台上下文生成：

```text
NSV 市场控制台
→ 所属玩家舰船
→ 当前战略节点
→ 当前市场
→ 市场 buy offers
→ 可见与可购买规则
→ Cargo GUI 商品数据
```

商品是否显示、是否可买、实际价格、库存和限购均由 NSV 服务端决定。

## 购买请求

客户端只提交商品标识、数量和可选的目录版本，不得提交最终价格、市场、CargoHub、交付网格或交付坐标。

服务端收到请求后必须重新解析当前市场和商品，重新计算价格并验证余额，不能信任 GUI 中先前显示的数据。

## 后端分派

```text
普通 Cargo 控制台
└── CargoOrderConsoleComponent
    └── CargoSystem.Orders

NSV 市场控制台
└── NsvCargoMarketConsoleComponent
    └── NsvCargoPurchaseSystem
```

必须避免同一购买请求被普通 Cargo 和 NSV 两套系统重复执行。

## 独立网格交付

NSV 独立网格属于服务端交付上下文，不应加入 GUI 协议。

```text
市场控制台
→ 所属玩家舰船
→ CargoHub
→ 关联的独立交付网格
→ 受控交付位置
```

交付网格无效、交付位置失效或节点已经变化时，购买应失败，并避免错误扣款或重复生成商品。

## 实现阶段

1. 确认 `CargoConsoleInterfaceState`、`CargoOrderData` 和购买消息字段；
2. 让 NSV 市场控制台绑定 Cargo 订单 BUI；
3. 将 NSV buy offers 转换为共享商品数据；
4. 将购买请求分派给 `NsvCargoPurchaseSystem`；
5. 增加服务端商品、价格、余额和目录版本复检；
6. 保留 CargoHub 扣款及独立网格交付；
7. 完成后删除重复的 NSV 购买 GUI。

## 验收标准

- NSV 市场控制台可以打开现有 Cargo 订单 GUI；
- 不同战略节点显示各自的可买商品和价格；
- 普通 Cargo 行为保持不变；
- 客户端无法伪造价格、CargoHub 或交付目标；
- 旧市场报价不能成交；
- 商品只交付到服务器解析出的 NSV 独立网格；
- 失败不会造成错误扣款或重复交付。

## 相关文档

- `docs/feature_nsvcargodesign.md`；
- `docs/nsv_game_loop_design.md`；
- `docs/feature_persistentsectors.md`。
