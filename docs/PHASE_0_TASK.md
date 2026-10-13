# 给 GLM-5.3 的任务：建立全新 NX AI Assistant 工作目录

模型/provider 保持用户当前配置：`nvidia_nim/z-ai/glm-5.3`。不得更换模型或修改其连接配置。

## 目标

在 `D:\AI\NX_AI_ASSISTANT` 建立全新、独立的代码库，实现 NX2512 内运行的 AI 插件。该目录不存在时可以新建；若已存在，先检查并保护其中所有现有内容，不能覆盖未知文件。

**这是 clean-room 重建：可以从 `D:\AI\NX_MCP` 只读调查历史经验和验证证据，但禁止复制/粘贴其实现代码，禁止把旧项目加成依赖。** 不使用旧 Engine、旧 MCP、旧 GUI Bridge 或独立 Python 服务作为新产品运行时。

在开始前检查当前 Code 工作区是否确实为新目录。如果当前会话受限于旧仓库/worktree，先停止文件操作，提示用户在 Code 中打开 `D:\AI\NX_AI_ASSISTANT`，不得跨工作区绕过保护或自行同步。

## 必须先调查

除了先打通 NX2512，还要为后续更高版本预留升级架构。本阶段不要求立即支持所有版本，但不能把实现设计成只能在 NX2512 中继续扩展。

1. NX2512 实际安装路径和可用 NXOpen 程序集；记录 `managed` 与 `managed_core` 的差异和各自目标运行时，不得混用。
2. 安装目录或 SDK 内可用的 NXOpen C# 示例、入口签名、自定义主菜单按钮/菜单项加载方式。
3. 当前安装环境支持的插件装载生命周期、modeless WinForms/WPF 运行约束、NX UI 线程执行方式。
4. 依据真实证据选择最小原型技术路线，并写入 `docs/TECHNICAL_SPIKE_RESULTS.md`。注明哪些只读调查，哪些已真实测试。
5. 定义兼容性 Profile/Host Adapter 的最小边界：共享业务代码不引用 `NXOpen.dll`；NXOpen 入口和调用封装留在目标版本适配器中。
6. 在技术记录中说明如何为未来 NX 版本选择对应 NXOpen references/运行时并重新构建；不得承诺一个 DLL 可无测试跨所有未来版本运行。
7. 定义安装器如何识别多个 NX 安装实例，如何给各支持版本部署匹配的宿主组件，以及如何保留用户设置并回滚。

## 本轮只做第一条纵向功能

- 新建一个干净解决方案和项目。
- 插件可以从 NX 主界面入口打开 modeless 聊天窗口。
- 重复点击入口时激活既有窗口，不重复打开。
- 窗口不阻塞 NX 主界面。
- 显示当前活动工作部件的名称/路径/单位（取不到的字段标注未知，不伪造）。
- NX 退出时插件能清理资源。
- 暂不接模型 API，暂不创建/修改几何，先验证插件壳。

建议目录：

```text
D:\AI\NX_AI_ASSISTANT\
  CLAUDE.md
  README.md
  docs\
  src\NXAI.Assistant.Plugin\
  src\NXAI.Assistant.UI\
  src\NXAI.Assistant.Core\
  src\NXAI.Assistant.Providers\
  tests\
  tools\installer\
  samples\
```

可以按实际框架合理合并项目，但必须保持职责分层，不要过早创建很多空项目。

## 严格禁止

- 不依赖 MCP、GUI Bridge、Python、外部代理或外部 Agent。
- 不通过 `run_journal.exe` 作为产品主交互流程。
- 不修改 `D:\AI\NX_MCP` 中的代码、配置或部署文件。
- 不生成后台独立 `.prt` 作为首阶段验收。
- 不修改用户已有零件。
- 不提交 Git commit，不执行 destructive cleanup/reset。
- 不宣称插件“可以工作”，除非已在用户的 NX2512 环境实测加载并打开窗口；如果当前环境无法实测，应明确标为 build-only。

## 验收和交付

提供并执行可复现的构建/测试步骤。优先在 NX 中实测：
1. 加载插件成功；
2. 主界面入口可见；
3. 点击打开一个 modeless 窗口；
4. 窗口显示当前活动部件信息；
5. 重复点击不创建重复窗口；
6. 关闭 NX 后资源清理正常。

写出 `docs/TECHNICAL_SPIKE_RESULTS.md` 和 `docs/PHASE_0_REPORT.md`，包含：真实 NX 安装路径、使用的程序集/框架、入口加载方式、UI 技术选型、源代码文件清单、构建命令、NX 实测结果、限制和下一步。
