# 测试计划（1030 更新 · v2）

## 一、每次构建必须通过的离线门禁

| 门禁 | 入口 | 覆盖 |
|---|---|---|
| 五模式目录 | `tests/PluginModeCatalog` | 插件五模式顺序、原生映射、F11 循环、状态摘要 |
| 原生超时契约 | `tests/NativeTimeoutPattern` | 1030 `WildLadderBot.HandleGame` 超时表达式形状（3 次重复） |
| 竞技场策略/台账 | `tests/ArenaPolicy` | 停止边界、奖励门禁、台账、选牌策略 |
| 评分协议 | `tests/ArenaScoring` + `VerifyArenaScoringSession` | 盒子评分协议、失效缓存 |
| 中控配置 | `tests/ControllerConfiguration` + `VerifyArenaRuntimeConfiguration` | 配置应用门禁、模式映射 |
| 中控停止/RunStop | `tests/ControllerModeStats` + `VerifyArenaRunStop` | 本轮停止回执顺序、停止边界、战绩解析 |
| 战网登录等待 | `tests/BattleNetLoginWait` + `VerifyBattleNetLoginWait` | 登录页等待、官方启动链 |
| 酒馆保护 | `VerifyBg*` | 位置进展、购买来源、冻结队列、非法选项、拒绝回滚、英雄就绪 |
| 无目标技能 | `VerifyTargetlessAbilityGuard` | 1030 映射 ABI、只读映射钩子 |
| 纯净安装/还原 | `tests/InstallerSmoke` | 包内材料安装、备份、还原、哈希不变、1030 哈希 |
| 包隔离与清单 | `VerifyArenaIsolation` / `VerifyPackageManifest` | 包内无开发目录/日志、504 项 SHA256 |

## 二、五模式实机矩阵（每次发版后按需执行）

| 检查项 | 标准 | 狂野 | 休闲 | 酒馆战棋 | 竞技场 |
|---|---|---|---|---|---|
| 中控下发配置后开场 | ☐ | ☐ | ☐ | ☐ | ☐ |
| 插件上报模式正确 | ☐ | ☐ | ☐ | ☐ | ☐ |
| 首局真实动作（出牌/购买） | ☐ | ☐ | ☐ | ☐ | ☐ |
| “停止并等待本局”回执 | ☐ | ☐ | ☐ | ☐ | ☐ |
| 竞技场本轮停止与领奖 | — | — | — | — | ☐ |
| 停止后关闭炉石 / 保留客户端 | ☐ | ☐ | ☐ | ☐ | ☐ |
| 状态与战绩显示 | ☐ | ☐ | ☐ | ☐ | ☐ |
| 中控退出后插件继续运行 | ☐ | ☐ | ☐ | ☐ | ☐ |

## 三、1030 原生差异核对

- 程序集版本仍为 `13.0.0.0`；成员差异：新增 8 个（`BlackoutQualityRecovery` 等），移除 `HsMod.Direct.HsDirectRecommendationSource::IsTitanAbility`（改为 `IsMinionAbility`）。
- 本仓插件/工具未引用被移除成员；`VerifyTargetlessAbilityGuard` 以 1030 DLL 校验映射 ABI。
- 1030 的 `HandleGame` 超时局部槽位仍为 V29（`tests/NativeTimeoutPattern` 固定断言）。

## 四、已知限制

- 离线门禁不能替代实机自然终局、自然回执和真实领奖。
- 中控以管理员清单启动；实机验收需要用户在高权限会话中操作。
- 实机复测由用户在本轮交付后统一安排。
