# MidRunJoin · 杀戮尖塔 2 联机中途加入

让朋友可以在一场**已经开始的联机冒险**中随时加入，或在有人临时缺席时将其移除（支持**直接删除**或**暂存个人进度，稍后恢复并按差值补齐奖励**）。兼容标准联机存档与自定义模式存档，单份 `MidRunJoin.dll` 同时支持**正式版（public）**与 **public-beta** 分支。

- **当前版本**：`0.5.0`
- **Steam 创意工坊**：[MidRunJoin - Join your friends in Progress](https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271)
- **安装要求**：**仅需房主与需要中途补齐奖励的新加入玩家安装**（`affects_gameplay: false`，其他老队友不装也能正常进房联机；若检测到有玩家未安装，房主端会弹出明确提示）。

---

## 核心功能

### 1. 联机读档入口与成员调整界面
- 在主菜单 **联机 → 读取联机存档** 按钮右上角提供悬浮入口 **`＋ 中途加入`**（支持中英双语自动跟随游戏语言）。
- 点击后打开全屏原版风格弹窗，支持在一次读档中同时完成：
  - **添加多名新成员**：支持从 Steam 好友列表点选（正在游玩 STS2 / 在线优先，支持搜索与仅看在线）、直接输入非好友 SteamID64，以及本地局域网（`--fastmp` / 非 Steam）下的玩家编号输入。
  - **职业选择与随机职业**：可选任意角色（自动兼容正式版与测试版角色列表）或 **`随机`**（创建大厅时自动随机分配）。
  - **移除缺席成员（三级行内确认）**：点击已有成员后的 **`移除`** 按钮，原地展开三个选项：
    - **`删除`**：将该玩家从本次队伍中移出，立即重新在下方可选玩家中变为可用。
    - **`保存进度`**：按当前联机存档指纹（种子、创建时间、房主、进阶、模式、幕次）暂存该玩家的完整个人数据（卡组、遗物、药水、金币、生命、个人 RNG 与遗物袋），同样立即在下方可选玩家中变为可用。
    - **`取消`**：取消本次移除操作。
  - **已暂存进度置顶与自动恢复**：拥有有效暂存进度的玩家会在好友列表最上方置顶，并在名字前显示金色存档图标；重新选中时自动锁定其原职业，入队后恢复保存时的个人数据，并仅按**离开期间错过的房间差值**补发奖励。联机存档通关或被删除时，暂存缓存会自动清理。
- **自动邀请与安全取消**：Steam 模式下建立调整后大厅会自动向所有新增成员发送 Steam 大厅邀请；若在开局前取消，原联机存档、随机流与遗物袋保持原样不受污染。

### 2. 按经过房间计算的原生补齐奖励
- 扫描房主路线（兼容分路类 mod，避免重复统计其他队友分支房间）在 `MapPointHistory` 中已经过的房间，通过游戏原版 `RewardsSet.GenerateRewardsFor` + `Hook.ModifyRewards` 生成战斗掉落并合并为一个奖励界面（类似召唤铃风格）在读档完成后发放给新加入玩家：
  - **普通怪 / 精英 / Boss 战**：走原版遭遇掉落逻辑（金币、选卡、精英遗物、药水、其他 mod 的奖励钩子），所有金币合并为单个金币槽，药水上限不超过玩家药水栏位；每场 Boss 额外提供 1 次删牌奖励。
  - **宝箱房**：宝箱随机金币 + 遗物。
  - **问号 / 商店**：各补少量金币。
  - **营火**：每经过 2 个营火提供 1 次卡牌升级（向下取整）。
  - **先古事件（含涅奥）**：按实际经过的具体先古，每次提供 1 个原生“先古遗物 3 选 1”奖励。
  - **生命值同步**：按现有队伍平均剩余生命比例初始化新人当前生命。
  - **自定义局个人开局修饰符**：对新加入玩家自动执行可归属到个人的开局修饰符（实现 `ModifierModel.GenerateNeowOption` / `ClearsPlayerDeck` 的修饰符，如全明星、轮抽等）；恢复暂存进度的玩家不会重复执行。

### 3. 自适应安装兼容与房主未安装警告弹窗
- `MidRunJoin.json` 声明 `affects_gameplay: false`，未安装 mod 的玩家不会被原版握手拦截。
- 房主在建房时会自动为新加入玩家补齐早期楼层的空 `PlayerMapPointHistoryEntry` 占位（同时保留 [HistorySafety.cs](src/HistorySafety.cs) 防护），确保未装 mod 的老队友或新人在战后结算与历史记录悬停查看时不会抛出异常。
- **自适应补给同步模式**：
  - **全员安装**：启用完整补给模式（含原版掉落、铁匠卡牌升级、自定义局个人修饰符）。
  - **有未安装的老队友在场**：自动切换为**安全同步模式（Safe-Sync）**——仅在加入者本机使用独立临时 RNG 生成 `RewardSynchronizer` 原生可广播的奖励（金币、选卡、遗物、药水、先古 3 选 1、删牌），不修改共享遗物池与玩家 RNG，跳过卡牌升级与自定义局修饰符，保证未装 mod 的老队友零不同步。
- **房主端未安装警告弹窗**：
  - 当房主检测到进入大厅的玩家（支持复数）未安装 `MidRunJoin` 时，房主界面立即弹出警告弹窗，提示 **`检测到加入玩家没有安装 mod，因此不会补发奖励`**，并在下方以纵向列表列出所有未安装玩家的名字与身份标签，提供两个按钮：
    1. **`打开创意工坊`**：通过 Steam Overlay 接口（`SteamFriends.ActivateGameOverlayToWebPage`）在游戏内安全打开创意工坊页面；若当前不可用则弹出半透明 Toast 提示。
    2. **`忽视并以初始状态进入`** / **`忽视并以保存状态进入`**（同时包含新玩家与有存档玩家时，优先显示 **`忽视并以初始状态进入`**）：关闭弹窗并放行开局（未装 mod 的新人将以初始状态或已保存进度直接进入，不触发补给与不同步）。

---

## 使用步骤

1. 队伍结清当前房间奖励、推进到安全检查点（例如选定下一节点并退出到主菜单）。
2. 房主进入 **联机** 菜单，点击 **读取联机存档** 右上角的 **`＋ 中途加入`**。
3. 按需移除缺席成员（删除或保存进度）、添加新成员并选择职业（或随机）与补给模式，点击 **`建立调整后大厅`**。
4. 原队友与新成员加入大厅并全员点击就绪后进入冒险；新成员自动弹出合并补给界面，领取后全员从当前节点同步继续。

---

## 构建与安装

需要 .NET 9 SDK 与本地 Windows 版 Slay the Spire 2 安装目录：

```powershell
dotnet build .\MidRunJoin.csproj -c Release
```

如游戏不在默认路径，可显式指定 `GameDir`：

```powershell
dotnet build .\MidRunJoin.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

Release 构建会将 `MidRunJoin.dll` 与 `MidRunJoin.json` 输出到 `dist/MidRunJoin/`（并生成 `dist/MidRunJoin.zip`）。将 `dist/MidRunJoin` 文件夹复制到游戏的 `mods/` 目录并启用即可，成功加载日志为 `[MidRunJoin] Checkpoint lobby enabled.`。

---

## 代码结构

| 文件 | 职责 |
|---|---|
| [MidRunJoin.json](MidRunJoin.json) | Mod 清单（`v0.5.0`，`affects_gameplay: false`） |
| [src/Entry.cs](src/Entry.cs) | `ModInitializer` 入口与 Harmony 补丁注册 |
| [src/JoinLobby.cs](src/JoinLobby.cs) | 悬浮按钮、全屏成员调整模态框、行内 3 按钮移除与大厅就绪校验 |
| [src/MissingModWarning.cs](src/MissingModWarning.cs) | 房主端 `PeerVersionInfo` 探测、未安装玩家纵向列表弹窗与 Safe-Sync 切换 |
| [src/Candidate.cs](src/Candidate.cs) | 候选联机存档构建、暂存恢复、早期楼层历史占位补齐 |
| [src/PlayerStash.cs](src/PlayerStash.cs) | 按联机存档指纹保存/校验/恢复暂存玩家进度，通关或删档自动清理 |
| [src/CatchUp/ICatchUpFlow.cs](src/CatchUp/ICatchUpFlow.cs) | 补给策略接口与按房主路线筛选的历史楼层扫描器 |
| [src/CatchUp/CatchUpRegistry.cs](src/CatchUp/CatchUpRegistry.cs) | 原生房间掉落补给实现（完整模式 + Safe-Sync 模式） |
| [src/CatchUp/CatchUpCoordinator.cs](src/CatchUp/CatchUpCoordinator.cs) | 8-bit 标记编解码（240..255）、读档后补给与个人修饰符调度 |
| [src/HistorySafety.cs](src/HistorySafety.cs) | 战后结算与历史悬停防崩补丁 |
| [src/Compat.cs](src/Compat.cs) | 正式版（`public`）与 `public-beta` 跨版本兼容层 |
| [src/SteamHelper.cs](src/SteamHelper.cs) | Steam 好友列表、昵称解析与自动大厅邀请 |
| [src/VanillaUi.cs](src/VanillaUi.cs) | 原版风格 UI 样式与程序化存档图标 |
| [src/Localization.cs](src/Localization.cs) | 中英双语本地化文本 |

---

## 4 层自动化回归测试

一键执行全套 4 层测试（单元测试 → MegaDot CompatProbe → 游戏内 Fixture → 多进程真实 ENet 联机 E2E）：

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1
```

| 测试层 | 脚本 / 文件 | 覆盖范围 |
|---|---|---|
| **Layer 1：单元测试** | [tests/UnitTests/Program.cs](tests/UnitTests/Program.cs) | 补给公式、生命同步、ID 解析、存档指纹、历史过滤等纯逻辑 |
| **Layer 2：CompatProbe** | [tests/Run-CompatProbe.ps1](tests/Run-CompatProbe.ps1) | 校验全部方法体 JIT 编译与所有 Harmony 目标签名匹配 |
| **Layer 3：游戏内 Fixture** | [tests/FixtureTest.cs](tests/FixtureTest.cs) | 真实无头进程验证全屏 GUI、随机角色、暂存/恢复与差值奖励、Safe-Sync 补给、未安装警告弹窗、大厅就绪矩阵与历史防崩 |
| **Layer 4：多进程 ENet E2E** | [tests/E2ETest.cs](tests/E2ETest.cs) / [tests/Run-E2E.ps1](tests/Run-E2E.ps1) | 真实多进程联机：2P 战斗检查点 → 取消对照 → 2P→4P 扩员补给与 4P 同步战斗 → 4P→3P 移除重载 |
