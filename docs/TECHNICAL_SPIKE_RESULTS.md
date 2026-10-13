# 阶段 0 技术调查与验证记录（TECHNICAL SPIKE RESULTS）

日期：2026-10-13。所有结论标注证据等级：

- **[实测]** = 在本机真实 NX2512 进程中执行验证，有输出文件/日志证据（`artifacts/`）。
- **[安装证据]** = 从 NX2512 安装目录只读检查得到的事实（文件、二进制字符串、官方样例）。
- **[编译验证]** = 针对真实 NX2512 程序集编译通过即证明的 API 事实。
- **[推断/待实测]** = 证据指向但尚未在真实交互会话验证的事项。

## 1. NX2512 安装环境

| 项 | 值 | 证据 |
|---|---|---|
| 安装根目录 | `D:\Program Files\Siemens\DesigncenterNX2512` | [安装证据] |
| NXBIN | `<根>\NXBIN`（含 `ugraf.exe`、`run_journal.exe`、`SignDotNet.exe`、`nxclrldr.dll`） | [安装证据] |
| .NET Framework 上下文程序集 | `<根>\NXBIN\managed`（NXOpen v2512.0.0.0，MSIL，无 TargetFrameworkAttribute；NXOpen.UF v2512.1700.0.0） | [安装证据] `tools/NxCompatProbe` 输出 |
| .NET 8 上下文程序集 | `<根>\NXBIN\managed_core`（NXOpen v2512.0.0.0，x64，`.NETCoreApp,Version=v8.0`；NXOpen.UF v2512.1700.0.0） | [安装证据] 同上 |
| managed_core 批处理运行器 | `run_dotnet_core_nxopen.exe` + `runtimeconfig.json`（tfm `net8.0`，`Microsoft.NETCore.App 8.0.0`） | [安装证据] |
| .NET Framework 运行器 | `NXBIN\run_dotnet_nxopen.exe`（`.exe.config` 要求 `requiredRuntime v4.0.30319`） | [安装证据] |
| 本机 .NET 8 运行时 | NETCore.App 8.0.15 / 8.0.31；WindowsDesktop.App 8.0.15 / 8.0.31（WinForms 可用） | [安装证据] `dotnet --list-runtimes` |
| 本机 .NET SDK | 8.0.425 | [安装证据] |
| 本机 .NET Framework | 4.8.1（Release 533509） | [安装证据] |

## 2. 交互式 NX2512 的托管运行时选择 —— 结论：.NET 8（managed_core）为默认

**结论：交互式 NX2512 进程默认以 .NET 8 加载 NXOpen 与第三方 .NET 插件，引用 `NXBIN\managed_core` 程序集。** 证据链（多源交叉）：

1. `UGII\ugii_env_ug.dat` 第 3089 行（**未注释、生效**）：`UGII_NXOPEN_DOTNET_CORE_MAJOR_VERSION=8`。[安装证据]
2. `NXBIN\nxclrldr.dll`（NX 的 CLR 加载器）字符串：
   - `ERROR: UGII_NXOPEN_DOTNET_CORE_MAJOR_VERSION is not set. Please set it to at or above the floor version, %i.`
   - `Using .NET Directory: %s.` / `Using .NET Windows Desktop Directory: %s.`（会查找 Windows Desktop 运行时，即支持 WinForms/WPF）
   - `Defaulting to .NET Framework`（仅当上述环境变量未设置时才回落 .NET Framework）
   - `Expired Signature detected. Program needs to be rebuilt and signed.` / `DotNet author license is present. Authentication passed`
   - 相关环境变量：`UGII_NXOPEN_DOTNET_CORE_MAJOR_VERSION`、`UGII_NXOPEN_DOTNET_CORE_VERSION`、`UGII_NXOPEN_DOTNET_CORE_VERSION_OVERRIDE`、`UGII_NXOPEN_DOTNET_CORE_ROOT`（dotnet 非默认路径时）。[安装证据]
3. NX2512 自带第三方交互插件 `SIMULATION\magnet\application\Infolytica.*.dll` 为 x64（managed_core 特征，区别于 managed 的 MSIL）。[安装证据]
4. **[实测]** run_journal 启动的真实 NX2512 进程内：`NXOpen location: D:\...\nxbin\managed_core\NXOpen.dll`，`System.Private.CoreLib version: 8.0.0.0`，`CoreCLR runtime version: 8.0.31`。见 `artifacts/nxload_validation_result.txt`。

含义：本产品 NX2512 适配层按 `net8.0(-windows)` 构建并引用 `managed_core` 程序集；`managed`（.NET Framework）路径仅在显式关闭上述环境变量时使用，不作为本产品目标。

## 3. 插件加载入口机制（菜单 + custom directory）

**[安装证据]** 全部来自 NX2512 安装目录自带文件，未参考旧项目代码：

- 加载配置由用户级环境变量 `UGII_CUSTOM_DIRECTORY_FILE` 指向一个目录清单文件（当前本机该变量指向旧项目 `D:\AI\NX_MCP\nx_custom_dirs.txt`）。
- 清单文件格式（`UGII\menus\custom_dirs.dat` 内自带说明）：目录每行一个、行内不能有其他内容；`#` 开头为注释；支持 `#include`；目录不存在时该行被忽略。
- custom directory 结构（`UGII\ugii_env_ug.dat` 1134-1230 行自带说明）：
  - `startup\`：NX 启动时自动加载（旧项目在 NX2206 验证过：NX 会调用 DLL 的静态 `Startup()`，本安装保留了全部该机制文件）。
  - `application\`：菜单 `.men` 文件与对应动作 DLL（加载延迟到实际调用）。
- 菜单 `.men` 语法与 `.NET` 回调约定（以 NX2512 自带 `SIMULATION\magnet\application\sc_magnet_sfem.men` 为准）：
  ```
  MENU UG_TOOLBOX
     CASCADE_BUTTON <菜单名>
     LABEL <显示名>
  END_OF_MENU
  MENU <菜单名>
     BUTTON <按钮名>
     LABEL <显示名>
     ACTIONS Infolytica.Simcenter.AirRegion.dll
  END_OF_MENU
  ```
  - `ACTIONS` 的 `.dll` 后缀表示 .NET 回调，目标写法为 `<全限定类名>.dll`（同目录放置同名 DLL 文件）。
- 回调入口方法：静态 `Main()`。[安装证据] `Infolytica.Simcenter.AirRegion.dll` 内含方法名字符串 `Main`；该约定亦与社区文档一致。**[待实测]** 交互点击时的精确签名（无参 vs `string[] args`）未在本机验证；当前实现为 `public static void Main()`，若实测签名不符只需改一处（`src\NXAI.Assistant.Entry\Entry.cs`）。
- 签名/许可证：NX 只加载"已用 NX SignDotNet 签名"或"本机有 NXOpen .NET author license（`dotnet_author`）"的第三方程序集。[安装证据] `nxclrldr.dll` 字符串 + `SignDotNet.exe`（用法：`SignDotNet [<switches>] <filenames...>`，`-verify` 校验）。本项目 `build.ps1 -Sign` 提供签名步骤；是否需要签名以实测为准。
- `NXBIN\report_custom_dirs.exe`：可报告 custom 目录解析结果，后续安装器可用作自检。

## 4. NX 托管加载上下文（ALC）实测结论 —— 关键运维知识

在真实 NX2512 进程（run_journal 批处理）中实测得到（`tools\NxLoadValidation\NxLoadValidation.cs`，结果文件 `artifacts/nxload_validation_result.txt`）：

1. NX 使用**自定义、可回收（collectible）AssemblyLoadContext** 加载托管代码；其 `Name` 为 `D:\...\nxbin\managed_core\NXOpen.Utilities.dll`。[实测]
2. **`Assembly.LoadFrom` 会把 NXOpen 解析为第二份副本并破坏类型身份**（实测复现：`Unable to cast object of type 'NXOpen.Session' to type 'NXOpen.TaggedObject'`）。正确做法是让插件的 NXOpen 引用解析到 NX 已加载的同一实例（单一类型宇宙）。[实测]
3. 非可回收 ALC 不能引用 NX 可回收 ALC 中的程序集（`0x80131515: A non-collectible assembly may not reference a collectible assembly`）。[实测]
4. NX 的 ALC **不会**探测插件目录下的普通依赖（`NXAI.Assistant.Core` 在 NX ALC 中解析失败）；依赖需要插件自己的加载路径（NX 交互加载时由其 ManagedLoader/NXOpenLoadContext 处理；验证 journal 中用等价 ALC 模拟）。[实测]
5. 因此部署铁律：**不得把 `NXOpen*.dll` 复制进插件目录**（构建中所有 NXOpen 引用均为 `Private=false`），插件自身程序集（Core/UI/NxHost/Entry）同目录部署。[实测+安装证据]

## 5. UI 技术选型

- WinForms（`net8.0-windows`，`UseWindowsForms=true`）：NX 加载器明确查找 .NET Windows Desktop 运行时（§2 证据 2），本机已装 8.0.31；WPF 亦可用但 WinForms 与 NX 消息循环共存更简单。
- 聊天窗口为 modeless `Form.Show()`，在 NX UI 线程创建；NX 主消息循环泵送窗口消息，NX 主界面保持可交互。[安装证据]（机制）+ **[待实测]**（交互行为本身）
- 重复入口点击 → `NxHostBoot.ShowChatWindow()` 激活已有窗口，不重复创建。[编译验证]
- 后续阶段的 NXOpen 调度：阶段 0 全部读操作发生在 NX UI 线程（菜单回调、窗体事件），无跨线程 NXOpen 调用。网络/后台线程在阶段 1 引入，届时使用 NX UI 线程调度（旧项目在 NX2206 验证过 `UF_UI_lock_ug_access(UF_UI_FROM_CUSTOM)` + 隐藏 WinForms 控件模式，作为历史事实记录，不复制其代码）。

## 6. 本阶段实际使用的 API（编译验证于真实 2512.0.0.0 程序集）

- `NXOpen.Session.GetSession()`、`Session.Parts.Work`、`Session.Parts.Display`、`Session.ListingWindow`
- `NXOpen.BasePart.FullPath`、`NXOpen.BasePart.PartUnits`（注意：`Part.Units` 是类型不是属性，实例属性为 `PartUnits`）
- `NXOpen.Part`/`BasePart.Name`、`Session.Parts.OpenActiveDisplay(path, DisplayPartOption.AllowAdditional, out PartLoadStatus)`

## 7. 版本升级路径（为未来 NX/Designcenter 版本预留）

- 每个目标版本：用 `NxCompatProbe` 探测其 `managed_core`（如该版本仍默认 .NET 8；否则依据其 `ugii_env_ug.dat`/`nxclrldr.dll` 重新确认运行时）→ 用该版本自己的程序集重建 `NXAI.Assistant.NxHost.<Release>` → 真实加载验证 → 一次性零件集成测试 → 更新 `docs/SUPPORTED_NX_VERSIONS.md`。
- 共享层（Core/UI/Entry 壳）不引用任何版本特定 NXOpen 程序集；NXOpen 只出现在 `NxHost.<Release>` 适配层。
- 一个 DLL 未经重建与实测不能宣称跨版本兼容（CLAUDE.md 规则）。

## 8. 不确定项（待实测确认）

1. 交互式会话中菜单点击后 ManagedLoader 对 `ACTIONS <Class>.dll` 的精确回调签名（§3）。
2. 无 `dotnet_author` license 时是否必须 SignDotNet 签名（建议直接 `-Sign` 一次消除变量）。
3. GUI 交互链路：菜单出现、modeless 窗口、NX 保持可交互、重复点击激活、NX 退出清理——本会话无法点击 NX 图形界面，需用户按 `docs\PHASE_0_REPORT.md` 的步骤实测。
4. `#include` 语法在本机 NX 的 custom_dirs 解析中的行为（当前部署文件直接列出两个目录，未用 include，规避该不确定项）。
