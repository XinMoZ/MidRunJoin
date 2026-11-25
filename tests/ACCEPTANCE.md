# MidRunJoin v0.5.0 测试与验收说明

`MidRunJoin` 将自动化测试按证据级别拆分为 4 层，每层仅声称其实际能够证明的结论；未自动覆盖的真机 Steam 场景单列于文末手工验收清单。

## 一键运行全部自动化测试

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -GameDir "你的 STS2 安装目录"
```

也可通过环境变量 `STS2_GAME_DIR` / `STS2_TEST_RUNTIME_DIR` 指定路径，或按需指定新人职业 / 跳过多进程 E2E：

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -GuestChar SILENT
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -SkipE2E
```

## 自动化测试分层矩阵

| 层级 | 入口 | 证明范围 | 实测结果（正式版 `v0.111.1` & `public-beta` `v0.114.0` 均通过） |
|---|---|---|---|
| **Layer 1：离线单元测试与打包校验** | `dotnet run --project .\tests\UnitTests\MidRunJoinUnitTests.csproj -c Release` | `SteamHelper` 好友分组排序/搜索/在线过滤/异常注入/邀请分发/`TryOpenWorkshopPage`、200 好友排序与按键搜索及本地化性能基准（防卡顿预算）、`HistoryBellFormula` 纯奖励汇总公式、`CatchUpHealthFormula` 血量同步、JSON 本地化全量键对齐与回退、`MidRunJoin.json` 清单（`affects_gameplay: false`）及 `dist/MidRunJoin` 产物洁净度 | **PASS**：212 项检查通过（200 好友排序 `< 5ms`、搜索过滤 `< 2ms`、本地化 `< 50us`） |
| **Layer 2：程序集与 Harmony 兼容探针** | `pwsh -NoProfile -File .\tests\Run-CompatProbe.ps1` | 隔离 `.NET 9` 运行时下，MegaDot headless 加载 `sts2.dll`、`0Harmony.dll` 与 `MidRunJoin.dll`；校验 15 个关键游戏 API、JIT 准备全部方法体、`Harmony.PatchAll` 精准绑定全部 12 个目标方法 | **PASS**：15 项 API 通过，579 个方法体 JIT 准备通过，12 个 Harmony 补丁绑定通过 |
| **Layer 3：游戏内夹具回归** | `pwsh -NoProfile -File .\tests\Run-Fixture.ps1` | 单进程 headless 真游戏内运行：标准与自定义模式存档支持、移除缺席玩家（删除/保存进度/取消）与混合增删、暂存指纹校验与恢复差值补给、`DeleteCurrentMultiplayerRun` 自动清理暂存、多新人与全角色/随机角色初始化、`HistoryBellCatchUpFlow` 原生房间掉落与 `BuildSafeSyncRewards`（不修改 `PlayerRng`）、4 段 8-bit 标记（`240..255`）、早期楼层历史占位与 `HistorySafety` 防崩、房主端未安装警告弹窗（纵向多玩家列表 + 按钮优先级 + Steam Overlay 不可用 Toast）、4P `LoadRunLobby` 就绪矩阵、**游戏内防卡顿性能预算**（弹窗打开 `< 250ms`、警告弹窗 `< 100ms`、30 层后期补给 `< 150ms`、`Prepare` `< 150ms`、`SafeGetEntry×1000` `< 15ms`） | **PASS**：全部功能与性能预算检查通过 |
| **Layer 4：4 进程真实 ENet 端到端** | `pwsh -NoProfile -File .\tests\Run-E2E.ps1 -GuestChar SILENT -Guest4Char DEFECT` | 独立隔离档案与本地 ENet（UDP 33771）4 进程实测：<br>1. **Phase 1**：双人打完第 1 怪并进入第 2 怪战斗中保存检查点。<br>2. **Cancel-2P**：房主打开弹窗点取消后正常双人续局，验证零副作用。<br>3. **Phase 2（2P→4P 多新人 + 合并补给 + 4P 战斗）**：房主一次添加 `peer3` + `peer4`，全端加载后弹出补给界面并同步领取，随后 4 人完成 4P 战斗与存盘。<br>4. **Phase 3（4P→3P 移除缺席玩家）**：房主通过弹窗移除 `peer4`，3 人读档进入下一房间并校验 3P 倍率。 | **PASS**：Phase 1 + Cancel-2P + Phase 2 + Phase 3 全部通过 |

## 仍需真机 Steam 手工验收的项目

上述自动化测试使用 `--force-steam=off` 与本地 ENet 或内存桩，不能替代以下真机 Steam 场景：

1. **Steam 好友搜索列表与自动邀请**：真机 Steam 环境下测试百人以上好友列表的搜索、“仅看在线”筛选、已暂存好友置顶与存档图标显示、手动 SteamID 输入、双击添加多名好友并自动发送 Steam 大厅邀请。
2. **Steam Overlay 创意工坊跳转**：开启与关闭 Steam 游戏内覆盖界面时，分别验证点击未安装警告弹窗的「打开创意工坊」按钮能否通过 Steam Overlay 打开页面或弹出半透明 Toast。
3. **跨客户端安装组合实机联机**：房主 + 新人安装、老队友未安装时的完整实战与战后结算画面验收。
4. **破限人数联机与多分辨率布局**：与 5+ 人联机破限 Mod 同开，以及 1080p / 1440p / 4K 下悬浮按钮与弹窗的视觉布局检查。
