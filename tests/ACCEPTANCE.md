# MidRunJoin v0.4.0 测试与验收说明

参考 `AstralEmotes` 的分层验证体系，`MidRunJoin` 将自动化测试按证据级别拆分为 4 层，每层仅声称其实际能够证明的结论；未自动覆盖的真机 Steam 场景单列于文末手工验收清单。

## 一键运行全部自动化测试

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1
```

可按需指定新人职业或跳过多进程 E2E：

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -GuestChar SILENT
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -SkipE2E
```

## 自动化测试分层矩阵

| 层级 | 入口 | 证明范围 | 本次实测结果（`sts2.dll` `0.1.0+d09d48e5`） |
|---|---|---|---|
| **Layer 1：离线单元测试与打包校验** | `dotnet run --project .\tests\UnitTests\MidRunJoinUnitTests.csproj -c Release` | `SteamHelper` 好友分组排序/关键词搜索与仅看在线过滤/异常注入/邀请分发、200 好友排序微基准、`HistoryBellFormula` 纯奖励汇总公式（战斗/精英/Boss/问号与商店/宝箱/火堆向下取整/先古之民）、`MidRunJoin.json` 清单及 `dist/MidRunJoin` 产物洁净度 | **PASS**：47 项检查通过，`RankAndFormatFriends(200)` 约 `~133 us/op` |
| **Layer 2：程序集与 Harmony 兼容探针** | `pwsh -NoProfile -File .\tests\Run-CompatProbe.ps1` | 隔离 `.NET 9` 运行时下，MegaDot headless 加载 `sts2.dll`、`0Harmony.dll` 与 `MidRunJoin.dll`；校验 16 个关键游戏 API（含 `RewardsCmd.OfferCustom`、`GoldReward`、`CardReward`、`RelicReward`、`CardUpgradeReward`、`CardRemovalReward` 等）、JIT 准备全部方法体、`Harmony.PatchAll` 精准绑定 5 个目标方法 | **PASS**：16 项 API 通过，218 个方法体 JIT 准备通过，5 个 Harmony 补丁绑定通过 |
| **Layer 3：游戏内夹具回归** | `pwsh -NoProfile -File .\tests\Run-Fixture.ps1` | 单进程 headless 真游戏内运行：任意存档节点接受校验（战斗中 / 地图 / 幕初均不再拦截）、移除缺席玩家与混合增删（同时加人+踢人+清理 `PreFinishedRoom.ExtraRewards`）、无上限多新人与全 6 职业初始化、重复 ID 拦截、`HistoryBellCatchUpFlow` 12 房间完整奖励生成与确定性校验（`11 rewards: gold=244, relics=2+2 ancient, upgrades=1, removals=1, cards=4`）、`FloorAddedToDeck` 8-bit 标记一次性消费防重入、进阶 10 效果、原存档/RNG/共享遗物袋零副作用与 4P 网络包往返、悬浮按钮与搜索好友弹窗高度边界及 4P `LoadRunLobby` 就绪矩阵 | **PASS**：236 项检查通过（`Prepare` 双新人平均耗时 `~22 ms`） |
| **Layer 4：4 进程真实 ENet 端到端** | `pwsh -NoProfile -File .\tests\Run-E2E.ps1 -GuestChar SILENT -Guest4Char DEFECT` | 独立隔离档案与本地 ENet（UDP 33771）4 进程实测：<br>1. **Phase 1**：双人打完第 1 怪并进入第 2 怪**战斗中**保存（`PreFinishedRoom == null`），验证真实战斗中存档。<br>2. **Cancel-2P**：克隆存档，房主打开弹窗点取消后正常双人续局，验证人数仍为 2 且怪物保持 2P 血量。<br>3. **Phase 2（2P→4P 多新人 + 召唤铃铛式补给 + 4P 战斗）**：房主一次添加 `peer3 (SILENT)` + `peer4 (DEFECT)` 并选择默认 `history_bell` 流程；开局后全端在 `NGame.LoadRun` 完成后由 `RewardsCmd.OfferCustom` 确定性弹出原版非终结 `NRewardsScreen`；`peer3` 实领堆叠金币 `+25`、`peer4` 实领堆叠金币 `+38` 并跳过剩余奖励完成同步；随后 4 人正常完成当前 4P 战斗（`4.4x` 倍率、首回合 4 端出牌、次合清场、领奖并进入第 3 房且保存 4P 存档）。<br>4. **Phase 3（4P→3P 移除缺席玩家）**：房主通过弹窗界面移除缺席的 `peer4`，3 人读档进入第 3 房，核对 3P 席位 `[1, 2, 3]` 与 3P 怪物倍率（`3.3x`）。 | **PASS**：Phase 1 + Cancel-2P + Phase 2 (`SILENT`+`DEFECT` 实领补给与 4P 战斗) + Phase 3 (`4P->3P` 踢人重开) 全部通过 |

## 仍需真机 Steam 手工验收的项目

上述自动化测试使用 `--force-steam=off` 与本地 ENet 或内存桩，**不能替代**以下真机 Steam 场景：

1. **Steam 好友搜索列表与多新人邀请**：真机 Steam 环境下测试百人以上好友列表的关键词搜索、“仅看在线”筛选、双击添加多名好友并自动发送 Steam 邀请。
2. **中后期多房间存档的召唤铃铛式补给界面滚动体验**：在经过多个战斗/精英/Boss/宝箱/火堆/先古之民的真实联机存档中，确认新人打开 `NRewardsScreen` 后能顺畅滚动领取多张选牌、堆叠金币、随机遗物/先古遗物、强化升级与删牌，且关闭后不影响房主与其他队友选路。
3. **破限人数联机兼容性**：与任意联机人数破限 Mod（如 5+ 人联机）同开时，确认添加第 5+ 名新人或移除其中缺席成员后大厅与进局均正常。
4. **不同分辨率下的悬浮按钮与弹窗布局**：检查 1080p / 1440p / 4K 下「读取联机存档」按钮右上角 `＋ 中途加入` 悬浮按钮及弹窗高度是否正常显示无遮挡。
