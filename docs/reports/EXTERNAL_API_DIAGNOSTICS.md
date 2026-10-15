# 外部 API 诊断记录（EXTERNAL_API_DIAGNOSTICS）

> 本文件记录**独立于 NX AI Assistant 项目**的外部模型 API / LiteLLM 排查测试。
> 这些结果不能用来声称 NX AI Assistant 实现了视觉输入、图片理解或任何未实现的 API 兼容能力——项目当前只实现普通文本 Chat Completions。
> 本文件不含任何真实 Key、Token、Authorization 头或用户私密信息。证据来源：用户提供的测试结果（对话内确认），未附原始请求/响应日志存档。

## 1. GLM-5.3 / LiteLLM 兼容性排查（2026-10 中旬，具体日期未记录）

用户报告的可核实结果（逐项独立验证，非本项目测试）：

| # | 场景 | 结果 |
|---|---|---|
| 1 | NVIDIA GLM-5.3 直连请求 | 通过——正常返回模型内容 |
| 2 | LiteLLM OpenAI 兼容 Chat Completions（普通文本） | 通过 |
| 3 | LiteLLM Anthropic Messages（普通文本） | 通过 |
| 4 | 非流式工具调用 | 通过——返回预期 `tool_use` 与 `city=Paris` 参数 |
| 5 | Anthropic 流式文本 | 通过——收到 `message_start`、内容增量、`message_stop` 事件 |
| 6 | 流式工具调用 | 通过——HTTP 200、`tool_use`、正确参数、完整结束事件 |

**结论**：用户当前的 GLM-5.3 接入链路（直连与 LiteLLM 两种）在文本、工具调用（流式/非流式）上均工作正常。这佐证了项目 Phase 2（工具调用）在模型侧的可行性，但**模型侧可用 ≠ 插件侧已实现**。

**未决问题**：Claude 编程界面曾因发送图片无法继续；取消图片重新执行后恢复。**失败确切根因未确定，未通过失败请求日志证明**——仅记录现象，不下结论。

## 2. GLM-5.3-Flash 速度测试（Dahl，日期未记录）

- `zai-org/GLM-5.3-Flash`，5 次请求全部成功。
- 完整请求耗时：7.08 / 0.58 / 1.12 / 0.56 / 0.56 秒，平均约 **1.98 秒**。
- **重要局限**：每次返回固定 128 个 completion tokens（达到当时指定的 `max_tokens` 上限）——测的是"固定输出长度下的端到端延迟"，且 7.08s 首测明显含冷启动。**本批数据只能作为历史测试记录，不能作为可靠的纯生成速度基准**；如需基准需固定 token 数、排除冷启动、多次重复。

## 3. 与本项目的边界（防误读）

- 本项目当前**不实现**：图片/多模态输入、Anthropic Messages 协议、流式输出、工具调用执行。
- 上述外部测试通过与否不改变 `PROJECT_STATUS.md` 中项目自身的验证状态分级。
- Phase 2 实现工具调用时，LiteLLM 兼容链路可作为参考路径之一，但插件仍以 OpenAI 兼容 Chat Completions 为主协议。
