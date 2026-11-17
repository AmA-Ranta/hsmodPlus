# 编码规范（Karpathy 风格落地版）

本仓库的代码风格遵循“显式、直白、可单测、少魔法”。目标不是写得聪明，而是让下一个人（或明天的自己）能在五分钟内看懂并安全修改。

## 1. 四条原则（卡帕斯编程 / karpathy-guidelines）

1. **动手前先思考**：先写下假设与成功判据；有歧义就问；有更简单的方案就直说。不要用实现掩盖不确定。
2. **简洁优先**：只写解决问题所需的最少代码；不为一次性逻辑造抽象；不写“以后可能用到”的扩展点。200 行能压到 50 行就重写。
3. **外科式最小改动**：只动必须动的地方；顺手“优化”相邻代码属于越界；发现无关死代码只报告、不删除。
4. **目标驱动**：把任务改写成可验证目标（先写会失败的断言，再让它通过；重构前后测试必须一样绿）。

## 2. 本仓库的具体落地

1. **显式优于聪明**：不依赖隐式约定、不做运算符重载式的小技巧，分支条件写成能读出来的句子。
2. **一个函数只做一件事**：超过约 40 行的函数视为需要拆分信号；超过 100 行视为必须拆分。
3. **纯逻辑与副作用分离**：
   - 判断“该不该做”的逻辑放进 `*Policy` 纯静态类（无 IO、无时间、无随机，参数进、布尔/枚举出）；
   - 真正做事（点按钮、重启进程、读写文件）放在 `*Runtime` / 窗口类里。
4. **命名说人话**：`ShouldQueueStart`、`IsSwitchDrivenStop`、`DescribeTarget`，不写 `Handle2`、`proc`、`tmp`。
5. **错误必须显式**：要么返回值表达失败，要么抛异常并写清原因；禁止 `catch { }` 吞掉未知异常（只允许吞“已知无害”的清理动作）。
6. **不做隐式全局状态**：状态字段集中在少数类里，跨文件共享靠明确的方法参数，不靠静态可变字段。
7. **注释解释“为什么”**，不复述代码；业务原因、边界条件、历史坑必须写。

## 3. 分层与依赖

```
ArenaController.Wpf    WPF 前端：只做界面、把事件转成 ControllerHost 调用
ArenaController       引擎：安装 / 启动 / 监控 / 设置 / 停止与切换编排
  Ui/                 Windows 窗体承载 + ControllerHost 桥接
  Ui/*Policy.cs       纯决策（可单测，被 tests/ArenaPolicy 直接引用）
  Control/            与游戏/插件无关的纯策略与契约
ArenaBoxBot           插件：游戏内自动化（net48，BepInEx）
Shared/               引擎 ↔ 插件文件契约（两侧共享同一份源码）
tests/                每项目标一个可执行回归
tools/                构建 / 校验 / 验证脚本
```

规则：
- 上层可以依赖下层，**下层不得反向依赖 UI**；
- UI 层禁止做业务判断（例如“能不能切换模式”必须问 Policy）；
- 同一份契约源码用 `<Compile Include="..\..\...">` 链接，禁止复制粘贴两份。

## 4. 测试规则

1. 每个 `*Policy` 必须至少有一组断言，输出形如 `PASS: xxx policy (N assertions)`；
2. 修 bug 先补断言（能复现旧行为失败），再改实现；
3. 提交前必须全绿：
   - `dotnet run --project tests\ArenaPolicy -c Release`
   - `pwsh -File tools\Validate.ps1`
4. 涉及真实游戏的行为（重启客户端、点击、对局边界）用“点击驱动 + 日志取证”的端到端测试，结果落到 `artifacts/*.csv` 与 `docs/TEST_PLAN_MANUAL.md`。

## 5. 命名与注释约定

| 类型 | 约定 | 示例 |
|---|---|---|
| 纯策略 | `*Policy.cs`，静态类，方法为动词短句 | `ControllerModeSwitchPolicy.ShouldQueueStart` |
| 运行时 | `*Runtime.cs`，持有状态与副作用 | `ArenaActivityRuntime` |
| 契约 | `*Contract.cs` / `*Boundary.cs` | `ArenaStopBoundary` |
| 布尔 | `Is/Should/Can/Has` 前缀 | `IsSameMode` |
| 事件/回调 | 过去式或名词 | `HostChanged` |

注释：
- 公共 API 用英文 `<summary>`，业务约束（为什么不能 XX）用中文写在实现处；
- 每个“看起来奇怪”的分支必须有一行解释，通常指向一次真实故障。

## 6. 禁止清单

- ❌ 魔法数字（超时、阈值）散落在代码里 → 提到常量并命名；
- ❌ 复制粘贴分支（同一逻辑写两遍）→ 抽函数或 Policy；
- ❌ `catch { }` 吞异常（清理动作例外，且需注释）；
- ❌ UI 线程里做阻塞等待（用 `async` + 超时）；
- ❌ 在策略类里调用 `DateTime.Now`、文件、进程、网络（无法单测）。

## 7. 评审清单（提交前自查）

- [ ] 新增判断逻辑是否在 `*Policy` 且有断言？
- [ ] 是否引入了新的隐式状态或跨层依赖？
- [ ] 失败路径是否有明确信息（日志/异常）而非静默？
- [ ] 是否有可能打断正在进行的对局/任务？（切换、停止、重启的地方必须检查）
- [ ] `tests\ArenaPolicy` 与 `tools\Validate.ps1` 是否全绿？
- [ ] 文档（README / ARCHITECTURE / TEST_PLAN_MANUAL）是否需要同步？

## 8. 现状与演进

`ArenaController\Ui\MainForm.cs` 是历史遗留的巨型文件（5000+ 行）。按本规范，已逐步把可判定逻辑抽出：

| 抽出文件 | 职责 | 测试 |
|---|---|---|
| `ArenaController\Ui\ControllerModeSwitchPolicy.cs` | 运行中切换模式的全部判定 | `ControllerModeSwitchPolicyTests`（14 断言） |
| `ArenaBoxBot\ArenaSceneRecoveryPolicy.cs` | 非竞技场界面退回主菜单的判定 | `ArenaSceneRecoveryPolicyTests`（8 断言） |
| `ArenaController\Control\ArenaStopBoundary.cs` | 竞技场停止边界判定 | `ArenaPolicyTests` 内联断言 |
| `Shared\ArenaRunStopContract.cs` | 引擎↔插件停止契约 | `ArenaRunStopTests` |

后续每新增一处业务判断，都按同样方式“策略化 + 断言化”，不继续往 `MainForm.cs` 里堆分支。
