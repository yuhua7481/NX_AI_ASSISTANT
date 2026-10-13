# NX 版本兼容、升级与更新策略

## 目标

NX2512 是首个开发和验收基线；产品要能随用户安装的 NX 版本升级而演进，并有明确路径适配后续更高版本。这里的“可升级”不是声称同一 DLL 能无条件跨任意未来版运行，而是保证架构、构建、安装和验证流程允许低成本添加新版本适配器，并且不会破坏仍受支持的旧版本。

## 兼容性原则

1. **分离共享代码与 NXOpen 宿主代码。** Provider HTTP、消息编排、工具 schema、输入校验、设置模型等共享逻辑不直接引用版本特定的 `NXOpen.dll`。菜单入口、插件生命周期、NXOpen 类型、NX 线程调度和建模工具实现放入 NX Host Adapter。
2. **按真实环境选运行时。** 不预设所有 NX 版本使用同一个 .NET runtime。每个目标 NX 安装的参考程序集和官方支持运行时都必须从该版本实际安装与官方发布资料核实。
3. **版本探测不等于兼容证明。** 启动时收集产品版本、已加载 NXOpen assembly 的 Location/Version、当前宿主运行时和 adapter id。再查兼容性清单，不能只比较文件夹名。
4. **能力注册而不是散落版本判断。** 每个 Profile 只注册已经在对应版本验证的 NX 工具。某个版本没有某个 API 或操作不兼容时，关闭该工具或使用版本化实现，并清楚告诉用户原因。
5. **安全默认值。** 未知或未验证的 NX 版本可提供只读诊断，但默认禁用模型修改工具；不能静默执行未经验证的代码。
6. **不承诺无测试的二进制前向兼容。** 新版本可能改变运行时、程序集或 API。需要时为每个目标版本重新构建与分发宿主适配器；通用代码和用户数据保持独立。

## 建议结构

```text
src/
  NXAI.Assistant.Core/             # 不引用 NXOpen
  NXAI.Assistant.Providers/        # 模型 API 客户端，不引用 NXOpen
  NXAI.Assistant.UI/               # 聊天窗口/设置界面
  NXAI.Assistant.NxHost.Abstractions/  # 共享的宿主工具契约
  NXAI.Assistant.NxHost.NX2512/    # 针对 NX2512 的 NXOpen 实现
  NXAI.Assistant.NxHost.<Release>/ # 后续版本独立适配器/构建配置
installer/
  manifests/
    supported-nx-versions.json
```

实际项目结构可以根据阶段 0 发现的 runtime 限制微调；不要为了追求未来通用性而一次性建立大量空工程。

## 版本 Manifest 建议字段

```json
{
  "product": "NX AI Assistant",
  "packageVersion": "0.1.0",
  "profiles": [
    {
      "profileId": "NX2512-dotnet8",
      "nxRelease": "NX2512",
      "status": "planned",
      "hostRuntime": "must-be-verified",
      "nxOpenAssemblyVersion": "must-be-detected",
      "adapterPackage": "NXAI.Assistant.NxHost.NX2512",
      "capabilities": []
    }
  ]
}
```

以上字段值中的 `must-be-verified` 是格式示例，不能作为实际版本判断数据。应由探测/构建过程填入真实值。

## 兼容状态

| 状态 | 含义 | 可否启用模型修改工具 |
|---|---|---|
| `planned` | 已规划，未构建 | 否 |
| `built` | 已用目标版本引用程序集编译 | 否 |
| `loaded` | 在目标 NX 中真实载入 | 尚不能仅凭此启用所有操作 |
| `integration-tested` | 至少一组代表性操作在独立测试部件通过 | 仅允许已验证能力 |
| `supported` | 该版本当前支持矩阵和回归验收通过 | 允许已注册且已验证的能力 |

## 每次添加更高版本的升级流程

1. **检测**：找到本机新的 NX 安装、版本、NXOpen assemblies、目标 framework/runtime 和插件入口机制。
2. **评估 API 变化**：检查该版本官方发布说明与 NX Open 更新信息；在可用时使用 NXOpenReporter 对现有调用生成 API 使用/变更报告，识别 changed、deprecated、deleted 的 API。
3. **适配和构建**：复制的是本项目共享源代码/适配器抽象的设计，不是旧 NX 安装目录中的 DLL。使用新 NX 的官方引用重新构建对应宿主适配器。
4. **自动化测试**：运行 Provider、设置迁移、Prompt/Tool schema、Plan 和工具验证测试。
5. **真实加载测试**：在目标 NX release 中验证入口按钮、窗口生命周期、当前部件读取和插件退出清理。
6. **集成测试**：使用一次性测试零件验证当前部件读取、代表性几何操作、操作后状态读取和保存/重新打开。
7. **打包**：给该版本 adapter 单独标识/打包，不覆盖其他版本安装；更新支持清单、校验和与发布说明。
8. **升级/回滚测试**：保留用户 API 设置、加密密钥引用、聊天数据与旧 adapter；验证升级失败时可恢复旧包。
9. **标记支持状态**：只有完成真实集成测试后，才把对应 Profile 标记为 `supported`。

## 安装与用户数据

- 安装程序扫描已安装的 NX 实例，但只对已支持版本执行静默部署；未测试版本提示不支持或仅安装非侵入式诊断信息。
- 允许 NX2512 与新版本并存；各实例加载自己匹配的 adapter。
- 用户配置和聊天数据存放在用户数据目录，而不是 NX 安装文件夹或插件二进制目录。
- API Key 使用 Windows DPAPI/Credential Manager 等合适机制保存；迁移时不以明文导出密钥。
- 配置 JSON 使用 schema version，升级时迁移并保留未知字段；升级前自动备份，失败后可回滚。
- 安装/更新包做完整性校验，写文件前检查目标位置与现有版本，不能覆盖用户模型、证书或配置。

## 测试矩阵

每个正式支持版本至少有以下记录：

- [ ] 用该版本自己的 NXOpen reference assemblies 编译
- [ ] 确认宿主运行时和加载上下文
- [ ] NX 内真实加载插件与显示入口
- [ ] 打开/重复点击/关闭窗口
- [ ] 读取当前活动工作部件
- [ ] 在一次性测试部件中完成代表性建模操作
- [ ] 通过 NXOpen 验证实际特征/几何
- [ ] 保存并独立重新打开验证
- [ ] 设置/密钥/聊天数据升级迁移
- [ ] 安装、卸载、升级、回滚
- [ ] 原有支持版本回归

## 已知事实与谨慎边界

- Siemens NX 的持续发布可能带来 API、运行时和构建工具变化，因此插件需要维护逐版本兼容性记录。
- Siemens 的 NX 2506 材料指出该系列首次官方支持 .NET 8 与 .NET Framework 4.8 并行，并提到 NXOpenReporter 可用于报告 API 变化及评估升级需要的工作。该事实说明运行时/版本适配不能被忽略，但**不能直接证明当前 NX2512 或任意未来版本的实际 runtime**；每次仍须检查目标安装和官方对应发布资料。
- Siemens 的产品命名自 2026 年 6 月起发生变化（Designcenter 品牌），因此版本探测应依赖实际安装标识及程序集信息，不能只匹配旧产品名称。

## 外部参考

- Siemens Community, “How to create a NX Open C# .NET 8 project for NX 2506 with Visual Studio 2022”: https://community.sw.siemens.com/s/article/How-to-create-a-NXOpen-CSharp-NET-8-project-for-NX-2506-with-Visual-Studio-2022
- Siemens NX 2506 Release Notes overview (NXOpenReporter, API stability / migration material): https://www.scribd.com/document/890015554/NX-2506-Release-Notes  
  Note: this is a mirrored copy of release notes; confirm details against the release notes in the official Siemens Support Center for the exact target release.
- Siemens Designcenter product naming/release overview: https://www.siemens.com/en-us/technology/nx-cad/
