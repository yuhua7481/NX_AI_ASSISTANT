# NX 版本支持矩阵

本表必须反映实测，不可凭编译通过或产品版本号推断支持状态。

| NX/Designcenter release | Host runtime | NXOpen assembly fingerprint | Adapter package | Build | Load test | Live-part integration | Installer upgrade | Status |
|---|---|---|---|---|---|---|---|---|
| NX2512 | .NET 8（CoreCLR 8.0.31，WindowsDesktop.App 8.0.31；由 `ugii_env_ug.dat` 的 `UGII_NXOPEN_DOTNET_CORE_MAJOR_VERSION=8` 生效） | `NXBIN\managed_core`：NXOpen 2512.0.0.0（x64, net8.0），NXOpen.UF 2512.1700.0.0 | NXAI.Assistant.NxHost.NX2512 | built（2026-10-13，0 警告 0 错误） | run_journal 真实进程内加载+读路径通过（2026-10-13，`artifacts\nxload_validation_result.txt`）；GUI 菜单入口+modeless 窗口+活动部件读取+模型对话已用户实机验收通过（2026-10-15，六项全过，见 `reports/PHASE_1_CHAT_AND_UI_ACCEPTANCE.md`） | planned（阶段 3） | planned | loaded |
| 后续更高版本 | 待安装时探测（NxCompatProbe） | 待探测 | 按版本新增 | planned | planned | planned | planned | planned |

状态仅允许使用：`planned`、`built`、`loaded`、`integration-tested`、`supported`。

备注：
- NX2512 的 `loaded` 依据是 run_journal 启动的真实 NX 进程内完成：插件程序集加载、NXOpen 单一类型宇宙、活动部件读取（无部件/有部件两种情形）。交互式菜单入口、modeless 窗口与 NX 退出清理尚待用户 GUI 实测（见 `docs\PHASE_0_REPORT.md` §5），通过后本表更新并保持 `loaded`，直到一次性零件集成测试通过才可升 `integration-tested`。
- 证据分级与加载上下文（ALC）约束见 `docs\TECHNICAL_SPIKE_RESULTS.md`。
