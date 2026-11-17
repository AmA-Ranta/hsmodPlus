# hsmodPlus 中控（1030 · v2）

`hsmodPlus.exe` 是**可选**的外部控制端，只做四件事：

1. **安装 / 修复 / 纯净还原**：把安装包里的 `HsMod.dll`（1030）、`ArenaBoxBot.dll`、`UniverseLib.Mono.dll`、盒子 `version.dll` 安装到游戏/盒子目录，并生成 `RankBypass.ini`、`HsMod.cfg` 和安装清单；还原时按清单删除加载器文件并恢复备份。
2. **启动与停止**：优先经官方 Battle.net 启动炉石，找不到战网时才让盒子接管；绝不直接双击 `Hearthstone.exe`。停止时写插件停止请求，等待插件回执。
3. **界面控制**：五种模式、卡组名称、竞技场类型与额度、停止策略、每日计划。
4. **监控**：炉石/盒子进程、HsMod 本机接口、插件状态（模式、选牌、对局、台账）、最近日志。

中控**不实现**任何游戏内动作、选牌策略或对局决策——这些都在独立的 `ArenaBoxBot.dll` 里。中控不在时插件照常运行；中控退出不影响插件已开启的任务。

## 关键行为

- **默认管理员**：manifest 为 `requireAdministrator`，安装/还原不再因权限中途失败。
- **图标**：多尺寸 🛀 emoji 已嵌入 exe（任务栏/标题栏/资源管理器一致）。
- **原生可替换**：`HsMod.dll` / `version.dll` 的哈希只用于日志提示，不阻止启动；手动替换新原生后中控仍可正常使用。
- **插件状态为准**：模式、脚本开关、对局与台账以插件上报为准，中控不写入插件的状态文件。

## 目录结构

```
ArenaController/
  Program.cs            入口（高 DPI、管理员清单由 app.manifest 提供）
  app.manifest          requireAdministrator + longPathAware
  Assets/bath.ico       🛀 多尺寸图标
  Ui/                   MainForm 及各 Tab 界面
  Install/              安装/还原、路径探测、诊断日志
  Launch/               战网/盒子启动链、进程发现、登录等待
  Control/              模式映射、配置应用门禁、停止请求绑定
  Monitor/              运行监控、日志读取、战绩解析、恢复监控
```

构建：`tools/BuildRelease.ps1`（默认 1030 原生）。验证：`tools/RunValidation.ps1 -Package <zip>`。
