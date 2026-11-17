# 外部载荷（唯一来源）

本目录是完整发布包的必要输入，不是本项目编译缓存。

| 目录/文件 | 用途 |
|---|---|
| `Native/HsMod.dll` | 原 `1031更新` 的 HsMod 基线，仅目标缺失时供安装器使用 |
| `Native/version.dll` | 同一基线的盒子桥接载荷，仅目标缺失时使用 |
| `Hearthstone/BepInEx/core/` | BepInEx 与 Harmony/MonoMod 运行时 |
| `Hearthstone/BepInEx/UnstrippedCorlib/` | 18 个 Mono/JSON/UniTask 依赖；Doorstop 明确指向此目录，不可漏包 |
| `Hearthstone/BepInEx/config/` | 配置模板，不放用户配置 |
| `Hearthstone/BepInEx/patchers/` | 运行时补丁加载器 |
| `Hearthstone/winhttp.dll`、`doorstop_config.ini` | 游戏加载入口 |
| `UniverseLib.Mono.dll` | 插件依赖 |
| `Box/RankBypass.ini.template` | 盒子桥接配置模板 |

原生基线 SHA256（迁移仅改路径、不改字节）：

```text
CF2DDED0A8968366A5DF6949DAAD255E2E219D20EF3B7D8DA2C6F96C1C513F97  Native/HsMod.dll
A43EBBAFB792DFB6E816F4973048692A3BB2E61EDC19FD82D5F36BC83A566B87  Native/version.dll
```

不要把已安装游戏目录、日志、账号状态、用户配置复制进来。更新依赖需确认来源与分发许可，重新运行构建与发布校验。
本仓库不包含炉石/盒子应用本体，也不包含炉石编译参考程序集；开发机需自行提供。
