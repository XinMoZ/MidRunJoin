<div align="center">

# MidRunJoin · 杀戮尖塔 2 联机中途加入

**在《杀戮尖塔 2》联机存档中随时添加新朋友或移除缺席队友**

[![Steam Workshop](https://img.shields.io/badge/Steam%20Workshop-3844124271-1b2838?logo=steam)](https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271)
[![Version](https://img.shields.io/badge/version-0.5.1-blue)](MidRunJoin.json)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![STS2 Branches](https://img.shields.io/badge/STS2-public%20%26%20public--beta-orange)](#兼容性)

[English](README.md) | **简体中文**

</div>

---

## 简介

**MidRunJoin** 允许《杀戮尖塔 2》联机房主在读档时随时**添加新玩家**（并按队伍已走过的房间补齐奖励），或**移除临时缺席的队友**（支持直接删除，或按本局存档暂存其个人进度，稍后回归时自动恢复并仅补发离开期间的差值奖励）。

- 同时兼容**标准联机存档**与**自定义模式联机存档**。
- 单份 DLL 同时兼容**正式版（`public`）**与 **`public-beta`** 测试分支。
- 声明 `affects_gameplay: false`：**仅需房主与需要中途补齐奖励的新加入玩家安装**，未装 mod 的老队友仍可正常进房联机。

---

## 核心功能

### 1. 联机读档成员调整界面
- 在主菜单 **联机 → 读取联机存档** 按钮右上角提供悬浮入口 **`＋ 中途加入`**（自动跟随游戏语言切换中/英双语）。
- **一次读档支持同时添加多名新成员**：
  - **Steam 模式**：从好友列表点选（按「正在游玩 STS2 → 在线 → 离线」排序，支持关键词搜索与「仅看在线」筛选），也支持手动输入非好友 SteamID64。
  - **本地局域网 / `--fastmp` 模式**：直接输入玩家编号。
  - **职业选择**：可选当前游戏版本的全部角色，或选择 **`随机`**（创建调整后大厅时随机分配）。
- **移除缺席成员（行内三级确认）**：点击已有成员后的 **`移除`** 按钮，原地展开 **`删除` / `保存进度` / `取消`**：
  - **`删除`**：移除该玩家，并使其立即在下方可选玩家列表中重新可用。
  - **`保存进度`**：按当前联机存档指纹（种子、创建时间、房主、进阶、模式、幕次）暂存该玩家的完整个人数据（卡组、遗物、药水、金币、生命、个人 RNG 与遗物袋），同样立即可在下方重新选择。
  - **`取消`**：取消本次移除操作。
- **暂存进度置顶与自动恢复**：拥有有效暂存进度的玩家会在好友列表最上方置顶并显示金色存档图标；重新选中时自动锁定其原职业，入队后恢复保存数据并仅按**离开期间错过的房间差值**补发奖励。联机存档通关或删除时缓存自动清理。
- **自动邀请与零副作用取消**：Steam 模式下建立调整后大厅会自动向所有新增成员发送 Steam 大厅邀请；开局前取消不会污染原存档、随机流或共享遗物池。

### 2. 按经过房间计算的原生补齐奖励
扫描**房主路线**（兼容分路类 mod，避免重复统计队友分支房间）在 `MapPointHistory` 中已经过的房间，通过游戏原版 `RewardsSet.GenerateRewardsFor` + `Hook.ModifyRewards` 生成战斗掉落并合并为一个奖励界面在读档完成后发放：

| 房间类型 | 补给内容 |
|---|---|
| **普通怪 / 精英 / Boss 战** | 原版遭遇掉落（金币合并为单个金币槽，药水上限不超过药水栏位，兼容其他 mod 奖励钩子）+ 每场 Boss 额外 1 次删牌 |
| **宝箱房** | 宝箱随机金币 + 遗物 |
| **问号 / 商店** | 各补 10 金币 |
| **营火** | 每经过 2 个营火提供 1 次卡牌升级（向下取整） |
| **先古事件（含涅奥）** | 按实际经过的具体先古，每次提供 1 个原生「先古遗物 3 选 1」奖励 |
| **生命值** | 按现有队伍平均剩余生命比例初始化新人当前生命 |
| **自定义局修饰符** | 对新加入玩家自动执行可归属个人的开局修饰符（`GenerateNeowOption` / `ClearsPlayerDeck`），恢复暂存进度的玩家不重复执行 |

### 3. 自适应 Safe-Sync 与房主端未安装警告弹窗
- **早期楼层历史占位防崩**：房主建房时自动为新加入玩家补齐早期楼层的空 `PlayerMapPointHistoryEntry` 占位（并保留 [HistorySafety.cs](src/HistorySafety.cs) 兜底），确保未装 mod 的队友或新人在战后结算与历史悬停查看时不崩溃。
- **自适应补给同步模式**：
  - **全员安装**：启用完整补给模式（含原版掉落、铁匠卡牌升级、自定义局个人修饰符）。
  - **有未安装的老队友在场**：自动切换为**安全同步模式（Safe-Sync）**——仅在加入者本机使用独立临时 RNG 生成 `RewardSynchronizer` 原生可广播的奖励（金币、选卡、预设遗物、药水、先古 3 选 1、删牌），不修改共享遗物池与玩家 RNG，跳过卡牌升级与自定义局修饰符，保证未装 mod 队友零不同步。
- **房主端未安装警告弹窗**：房主通过 `GetVersionInfoForPeer` 检测到进入大厅的玩家（支持复数）未安装 `MidRunJoin` 时，房主端弹出警告弹窗，显示 **`检测到加入玩家没有安装 mod，因此不会补发奖励：`**，并在下方以纵向列表展示所有未安装玩家的昵称与身份标签，提供两个按钮：
  1. **`打开创意工坊`**：仅通过 Steam Overlay 接口（`SteamFriends.ActivateGameOverlayToWebPage`）安全打开创意工坊页面；若当前不可用则弹出半透明 Toast 提示。
  2. **`忽视并以初始状态进入`** / **`忽视并以保存状态进入`**（同时包含新玩家与有存档玩家时，优先显示 **`忽视并以初始状态进入`**）：关闭弹窗并放行开局。

---

## 安装方式

### Steam 创意工坊（推荐）
在 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271) 订阅后，在游戏内 Mod 菜单启用 **MidRunJoin** 即可。

### 手动安装
1. 从源码构建 Release 产物（见[从源码构建](#从源码构建)）或下载发布包。
2. 将包含 `MidRunJoin.dll` 与 `MidRunJoin.json` 的 `MidRunJoin/` 文件夹复制到游戏的 `mods/` 目录。
3. 启动游戏并启用 mod，成功加载日志为 `[MidRunJoin] Checkpoint lobby enabled.`。

---

## 使用步骤

1. 队伍结清当前房间奖励，推进到检查点并退出到主菜单。
2. 房主进入 **联机** 菜单，点击 **读取联机存档** 右上角的 **`＋ 中途加入`**。
3. 按需移除缺席成员（删除或保存进度）、添加新成员并选择职业（或随机）与补给模式，点击 **`建立调整后大厅`**。
4. 原队友与新成员加入大厅并全员点击就绪后进入冒险；新成员自动弹出合并补给界面，领取后全员从当前节点同步继续。

---

## 兼容性

- **游戏分支**：单份 DLL 已在**正式版（`public`, v0.111.1）**与 **`public-beta`（v0.114.0）** 实测通过（[src/Compat.cs](src/Compat.cs)）。
- **其他 Mod**：兼容自定义模式修饰符、分路类 Mod（按房主路线统计补给）以及多人破限 Mod。

---

## 常见问题与已知限制

- **为什么存档处于战斗开始时会禁用携带补给？**  
  在战斗中领取补给会中途修改战斗内牌组与遗物状态，触发联机校验不同步（`StateDivergence`）。请先打完当前战斗回到地图后再带补给加入，或选择「无补给」加入。
- **为什么不能在未安装 mod 的加入人客户端弹窗？**  
  未安装 mod 的客户端运行 100% 原版代码，STS2 原版协议仅支持发送固定错误码，无法向未装 mod 的客户端推送自定义 UI；因此未安装提醒统一在房主端弹出。
- **为什么有未装 mod 老队友时会自动跳过铁匠升级与自定义局个人修饰符？**  
  原版 `RewardSynchronizer` 仅广播金币、卡牌、遗物、药水与删牌，而卡牌升级走全员锁步的 `PlayerChoiceSynchronizer`、自定义局修饰符会直接修改本地卡组；跳过这两项并使用独立 RNG 可确保未装 mod 的队友不会发生 `StateDivergence` 校验不同步。若希望体验完整特性，建议全员安装。

---

## 从源码构建

需要 **.NET 9 SDK** 与本地 Windows 版《杀戮尖塔 2》安装目录（可通过环境变量 `STS2_GAME_DIR` 或 `-p:GameDir` 指定）：

```powershell
dotnet build .\MidRunJoin.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

Release 产物（`MidRunJoin.dll` 与 `MidRunJoin.json`）会输出到 `dist/MidRunJoin/`。

---

## 代码结构

| 文件 | 职责 |
|---|---|
| [MidRunJoin.json](MidRunJoin.json) | Mod 清单（`v0.5.1`，`affects_gameplay: false`） |
| [src/Entry.cs](src/Entry.cs) | `ModInitializer` 入口与 Harmony 补丁注册 |
| [src/JoinLobby.cs](src/JoinLobby.cs) | 悬浮按钮、全屏成员调整模态框、行内 3 按钮移除与大厅就绪校验 |
| [src/MissingModWarning.cs](src/MissingModWarning.cs) | 房主端 `PeerVersionInfo` 探测、未安装玩家纵向列表弹窗、半透明 Toast 与 Safe-Sync 切换 |
| [src/Candidate.cs](src/Candidate.cs) | 候选联机存档构建、暂存恢复、早期楼层历史占位补齐 |
| [src/PlayerStash.cs](src/PlayerStash.cs) | 按联机存档指纹保存/校验/恢复暂存玩家进度，通关或删档自动清理 |
| [src/CatchUp/ICatchUpFlow.cs](src/CatchUp/ICatchUpFlow.cs) | 补给策略接口与按房主路线筛选的历史楼层扫描器 |
| [src/CatchUp/CatchUpRegistry.cs](src/CatchUp/CatchUpRegistry.cs) | 原生房间掉落补给实现（完整模式 + Safe-Sync 模式） |
| [src/CatchUp/CatchUpCoordinator.cs](src/CatchUp/CatchUpCoordinator.cs) | 4 段 8-bit 标记编解码（`240..255`）、读档后补给与个人修饰符调度 |
| [src/HistorySafety.cs](src/HistorySafety.cs) | 战后结算与历史悬停防崩补丁 |
| [src/Compat.cs](src/Compat.cs) | 正式版（`public`）与 `public-beta` 跨版本兼容层 |
| [src/SteamHelper.cs](src/SteamHelper.cs) | Steam 好友列表、昵称解析、大厅邀请与 Steam Overlay 跳转 |
| [src/VanillaUi.cs](src/VanillaUi.cs) | 原版风格 UI 样式与程序化存档图标 |
| [src/Localization.cs](src/Localization.cs) | 数据驱动本地化加载器（读取 `localization/*.json`，支持内嵌与外部覆盖） |
| [localization/](localization/) | 独立 JSON 本地化文本表（[eng.json](localization/eng.json)、[zhs.json](localization/zhs.json)） |

---

## 自动化测试

详见 [tests/ACCEPTANCE.md](tests/ACCEPTANCE.md)。一键运行 4 层回归测试：

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

---

## 开源协议与开发说明

- 本项目采用 [MIT License](LICENSE) 开源。
- 开发说明：前期可行性调研与架构设计使用了 **GPT-6 Astra**，后续核心功能实现、跨版本适配与测试验证过程使用了 **Gemini Neon 4**。
