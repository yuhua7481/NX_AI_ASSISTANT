using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>Performs one chat-completions request. Tests replace it with a fake.</summary>
public delegate Task<ChatCompletionResult> ChatRequestDelegate(
    IReadOnlyList<OutboundMessage> messages,
    IReadOnlyList<ToolDefinition>? tools,
    CancellationToken cancellationToken);

/// <summary>
/// Presents a tool call for user confirmation. The window implements this:
/// it must not execute anything itself — it only collects the user's decision.
/// Returns null when the flow is aborted (window closing, request cancelled).
/// </summary>
public delegate Task<ToolConfirmationDecision?> ToolConfirmationDelegate(
    ToolConfirmationRequest request, CancellationToken cancellationToken);

/// <summary>
/// Coordinates sending between the chat window and <see cref="ModelService"/>:
/// validates input, appends the user message to the chat log, runs the request
/// asynchronously and writes the reply/error back into the log.
///
/// Tool protocol (Phase 2): the first model response of a turn may request one
/// registered tool call. The controller parses and validates it, hands it to
/// the confirmation delegate, and — only after explicit confirmation — runs
/// the typed executor once on the NX UI thread, then feeds the structured
/// result back to the model as a tool message for the final text reply. A turn
/// is capped at <see cref="MaxModelResponsesPerTurn"/> model responses and at
/// most one modeling execution; the final round never advertises tools.
///
/// Lives on the chat form's UI thread: StartSend/Cancel/Detach are called there and
/// the request continuation is posted back to the captured WinForms
/// SynchronizationContext, so log writes and events never touch controls from a
/// worker thread.
/// </summary>
public sealed class ChatSessionController : IDisposable
{
    /// <summary>Upper bound of User/Assistant/System messages sent to the model (newest kept).</summary>
    public const int DefaultMaxOutboundMessages = 20;

    /// <summary>
    /// 2026-10-23 live fix: model output that carries tool-protocol markup
    /// (a complete invoke block the tag adapter already consumed, or a
    /// stray/partial tag) must never reach the visible chat as assistant
    /// text. The model's own message round-trips through the protocol as a
    /// structured tool_calls entry — its raw markup is not conversation
    /// content. Everything displayed (final text, error text, receipts)
    /// passes through this sanitizer: complete blocks are removed with the
    /// surrounding text kept, stray openers replace the tag with a short
    /// notice so the protocol grammar never renders raw.
    /// </summary>
    internal static string SanitizeModelTextForDisplay(string text)
    {
        if (string.IsNullOrEmpty(text) || !ToolTagProtocol.ContainsToolMarker(text))
            return text;
        var scan = ToolTagProtocol.Scan(text);
        if (scan.ProtocolError == null)
        {
            if (scan.ToolCall != null)
            {
                // A complete invoke block. CleanText keeps any prose around
                // it; when that prose itself still carries residue (e.g.
                // stray parameter tags), scrub it too.
                string prose = scan.CleanText ?? "";
                if (ToolTagProtocol.ContainsToolMarker(prose))
                    prose = ToolTagProtocol.StripInvokeMarkup(prose);
                return prose.Length > 0
                    ? prose
                    : "[已省略：此条回复中的工具调用内容不作为聊天文本显示]";
            }
            // ContainsToolMarker true but Scan found no block: stray
            // parameter tags without a wrapper — strip them.
            string scrubbed = ToolTagProtocol.StripInvokeMarkup(text);
            if (string.IsNullOrWhiteSpace(scrubbed))
                return "[模型返回了无法解析的工具调用内容，已省略原始协议文本]";
            return scrubbed.Contains("parameter") &&
                   ToolTagProtocol.ContainsToolMarker(scrubbed)
                ? scrubbed + "\n[此条回复中的工具调用标记已省略]"
                : scrubbed;
        }
        // Malformed block (unclosed, multi, junk): replace the markup with
        // a concise notice, keep any surrounding prose.
        string prose2 = ToolTagProtocol.StripInvokeMarkup(text);
        if (string.IsNullOrWhiteSpace(prose2))
            return "[模型返回了无法解析的工具调用内容，已省略原始协议文本]";
        return prose2 + "\n[此条回复中的工具调用标记已省略]";
    }

    /// <summary>
    /// Model responses allowed per user turn. Phase 7.3.2: the cap now
    /// covers a limited serial chain — response 1 may request a tool call
    /// (e.g. create_sketch), response 2 another single tool call fed the
    /// first tool's real result (e.g. create_extrude with the controlled
    /// sketch reference), response 3 is the final text reply and may not
    /// trigger execution. Every round still executes at most ONE tool,
    /// strictly serially (each awaits the previous tool's real result),
    /// through the same registry/parser/authorization path. The cap keeps
    /// the chain finite — a longer workflow requires another user turn.
    /// </summary>
    public const int MaxModelResponsesPerTurn = 3;

    /// <summary>Modeling executions allowed per user turn. Aligned with
    /// MaxToolCallsPerMessage so one "create these four solids" request can
    /// complete in a single round; anything beyond the cap answered with an
    /// explicit refusal (never silently dropped).</summary>
    public const int MaxToolExecutionsPerTurn = 4;

    /// <summary>Silent revolve-axis self-corrections allowed per user turn.
    /// One is the working case (sketch → infeasible axis → corrected retry);
    /// a second rewrite would mean the model is loop-guessing, which the
    /// correction must not paper over.</summary>
    public const int MaxRevolveAxisCorrectionsPerTurn = 1;

    private readonly ModelService _modelService;
    private readonly ChatLog _chatLog;
    private readonly Func<(string apiBaseUrl, string apiKey, string modelName)?> _configLoader;
    private readonly ChatRequestDelegate _requestSender;
    private readonly int _maxOutboundMessages;
    private readonly INxToolExecutor? _toolExecutor;
    private readonly ToolConfirmationDelegate _confirmationHandler;
    private readonly ToolRegistry _toolRegistry;
    private readonly Func<NxContextSnapshot?>? _contextProvider;
    private readonly ExecutionModeManager? _modeManager;
    private readonly ExecutionModePolicy _modePolicy = new();
    private readonly IModelingExecutionLedger? _ledger;

    /// <summary>Snapshot captured when the current turn started (null when no
    /// provider is wired or capture failed). Consumed by face-relative tools.</summary>
    public NxContextSnapshot? CurrentTurnContext { get; private set; }

    private CancellationTokenSource? _cts;
    private bool _inFlight;
    private bool _detached;

    /// <summary>(tool name, exact arguments JSON) of calls that failed
    /// PARAMETER validation this turn. A byte-identical resubmission is
    /// refused before parsing — the failure is deterministic, only a
    /// corrected call (different JSON) can proceed. Cleared each turn.</summary>
    private readonly HashSet<(string Tool, string Args)> _failedParameterCallsThisTurn = new();

    /// <summary>Raised when a request starts; the UI should enter its busy state.</summary>
    public event EventHandler? RequestStarted;

    /// <summary>Raised when a request ends (success, failure or cancel); the UI restores its idle state.</summary>
    public event EventHandler? RequestCompleted;

    public ChatSessionController(
        ModelService modelService,
        ChatLog chatLog,
        Func<(string apiBaseUrl, string apiKey, string modelName)?> configLoader,
        ChatRequestDelegate? requestSender = null,
        int maxOutboundMessages = DefaultMaxOutboundMessages,
        INxToolExecutor? toolExecutor = null,
        ToolConfirmationDelegate? confirmationHandler = null,
        Func<NxContextSnapshot?>? contextProvider = null,
        ExecutionModeManager? modeManager = null,
        IModelingExecutionLedger? executionLedger = null)
    {
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _chatLog = chatLog ?? throw new ArgumentNullException(nameof(chatLog));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _requestSender = requestSender ?? new ChatRequestDelegate((messages, tools, token) =>
            modelService.GetChatCompletionWithToolsAsync(messages, tools, cancellationToken: token));
        _maxOutboundMessages = maxOutboundMessages <= 0 ? DefaultMaxOutboundMessages : maxOutboundMessages;
        _toolExecutor = toolExecutor;
        if (toolExecutor != null && confirmationHandler == null)
        {
            throw new ArgumentException("启用工具执行器时必须提供确认回调。", nameof(confirmationHandler));
        }
        _contextProvider = contextProvider;
        _confirmationHandler = confirmationHandler ?? ((_, _) =>
            Task.FromResult<ToolConfirmationDecision?>(null));
        _modeManager = modeManager;
        _ledger = executionLedger != null && toolExecutor != null
            ? executionLedger
            : null;
        _toolRegistry = toolExecutor != null
            ? CreateBlockTool.CreateRegistry()
            : new ToolRegistry();
        // Wire the executor's edge-finish auto-target candidate to THIS
        // controller's session state: the most recent executor-verified
        // body reference lives here (new conversations start empty — the
        // target must be re-established from the live model, not memory).
        if (_toolExecutor is NxTargetResolutionSink sink)
        {
            sink.LastCreatedBodyRefProvider = () => LastCreatedBodyRef;
            sink.LastCreatedFeatureRefProvider = () => LastCreatedFeatureRef;
        }
    }

    /// <summary>
    /// Phase 9.5: the body reference (JournalIdentifier, e.g. "BLOCK(5)")
    /// of the most recent SUCCESSFUL body-creating tool result in THIS
    /// conversation — executor-verified only. Cleared on NewConversation
    /// (the model state may have changed; the target is re-established
    /// from the live part, never from stale memory). Null before any
    /// successful body-creating result.
    /// </summary>
    public string? LastCreatedBodyRef { get; private set; }

    /// <summary>
    /// Phase 9.6: the FEATURE reference (JournalIdentifier, e.g.
    /// "COUNTERSUNK SCREW HOLE(2)") of the most recent SUCCESSFUL
    /// feature-creating tool result in THIS conversation — executor-verified
    /// only. Feeds the linear-pattern FEATURE mode.
    /// </summary>
    public string? LastCreatedFeatureRef { get; private set; }

    /// <summary>Clears the remembered auto-target candidate (new
    /// conversation). The NEXT empty-selection request must re-resolve the
    /// target from the live model or be refused — never reuse stale refs.</summary>
    public void ResetTargetResolution()
    {
        LastCreatedBodyRef = null;
        LastCreatedFeatureRef = null;
    }

    public bool IsRequestInFlight => _inFlight;

    /// <summary>
    /// True after <see cref="Detach"/> (real window teardown): the controller
    /// no longer accepts new requests and drops any later results. Distinct
    /// from Dispose-level cancellation — a ✕-hidden window keeps its
    /// controller attached, so the session survives the close/re-open.
    /// </summary>
    public bool IsDetached => _detached;

    /// <summary>True when the currently loaded configuration can serve a request.</summary>
    public bool IsConfigurationReady => _modelService.IsConfigured;

    /// <summary>Model name of the last successfully loaded configuration, null when not configured.</summary>
    public string? CurrentModelName { get; private set; }

    /// <summary>True when a modeling executor is registered and tools are advertised to the model.</summary>
    public bool ToolsEnabled => _toolExecutor != null;

    /// <summary>
    /// The execution mode actually in effect at the backend dispatch stage
    /// (Confirm when no manager was wired — the pre-Phase-6 behavior).
    /// </summary>
    public ExecutionMode CurrentExecutionMode =>
        _modeManager?.CurrentMode ?? ExecutionMode.Confirm;

    /// <summary>
    /// Loads the configuration through the injected loader and applies it to the model
    /// service, so the next request uses it without restarting NX. A missing/unreadable
    /// config clears the in-memory configuration but never touches the file on disk.
    /// </summary>
    /// <returns>True when a usable configuration is active.</returns>
    public bool ReloadConfiguration()
    {
        var config = _configLoader();
        if (config.HasValue)
        {
            _modelService.SetConfiguration(config.Value.apiBaseUrl, config.Value.apiKey, config.Value.modelName);
            CurrentModelName = config.Value.modelName;
        }
        else
        {
            _modelService.SetConfiguration("", "", "");
            CurrentModelName = null;
        }
        return _modelService.IsConfigured;
    }

    /// <summary>
    /// Appends the user message to the log and starts an asynchronous request.
    /// Returns false (and starts nothing) when the text is empty, a request is
    /// already in flight, or the controller is detached.
    /// </summary>
    public bool StartSend(string userText)
    {
        if (_detached || _inFlight || string.IsNullOrWhiteSpace(userText))
            return false;

        // Capture the NX context snapshot before the request starts. The
        // provider must run on the NX UI thread — StartSend is only called
        // there (chat form input path), so a synchronous read is safe and
        // keeps the snapshot consistent with what the user sees.
        CurrentTurnContext = _contextProvider?.Invoke();
        if (CurrentTurnContext is { } captured)
        {
            // Make visible what was actually captured this turn — a missing
            // or empty selection must never fail silently.
            _chatLog.Add(ChatRole.Info, DescribeCapturedContext(captured));
        }

        _inFlight = true;
        _failedParameterCallsThisTurn.Clear();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _chatLog.Add(ChatRole.User, userText);
        RequestStarted?.Invoke(this, EventArgs.Empty);
        // RunRequestAsync handles all expected failures itself; this guard keeps
        // any unforeseen exception from becoming an unobserved-task crash.
        _ = RunRequestAsync(cts.Token).ContinueWith(
            static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        return true;
    }

    /// <summary>
    /// Runs the context provider once now (NX UI thread) and returns the
    /// snapshot; also refreshes CurrentTurnContext so the next send uses it.
    /// Null when no provider is configured or the provider reports nothing.
    /// </summary>
    public NxContextSnapshot? CaptureContextNow()
    {
        var snapshot = _contextProvider?.Invoke();
        if (snapshot != null)
            CurrentTurnContext = snapshot;
        return snapshot;
    }

    /// <summary>Asks the in-flight request to abort; safe to call any time.</summary>
    public void CancelActiveRequest()
    {
        if (_detached)
            return;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The owning window is closing/disposing: stop touching its chat log and controls
    /// and abort any in-flight request. Results arriving after Detach are dropped.
    /// </summary>
    public void Detach()
    {
        if (_detached)
            return;
        _detached = true;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Detach();
        try
        {
            _cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        _cts = null;
    }

    private async Task RunRequestAsync(CancellationToken cancellationToken)
    {
        var protocol = new List<OutboundMessage>(BuildOutboundMessages());
        // Inject this turn's NX state as a clearly-labeled system message so
        // the model knows the current selection/part context (state data, not
        // user instructions). Kept only for this turn.
        var turnContext = CurrentTurnContext;
        if (turnContext != null)
        {
            var summary = turnContext.ToContextSummary();
            if (summary.Length > 0)
            {
                protocol.Add(new OutboundMessage(OutboundMessage.RoleSystem, summary));
            }
        }
        string? finalText = null;
        string? errorText = null;
        var canceled = false;
        // Structured receipts of every real tool execution this turn, in call
        // order — the only trusted record of what actually happened to the NX
        // model. The model's final wording is never evidence of success.
        var turnExecutions = new List<(string ToolName, CreateBlockExecutionResult Result)>();
        // sketchRef → the typed parameters the sketch was created with (this
        // turn only). Feeds the revolve axis advisor: the axis precondition
        // is decidable from the sketch's plane/shape without touching NX.
        var sketchesThisTurn = new Dictionary<string, RevolveAxisAdvisor.SketchFacts>(StringComparer.Ordinal);
        // Bounded self-correction: at most MaxRevolveAxisCorrectionsPerTurn
        // silent axis rewrites per turn. Beyond that the model's requested
        // axis goes through unchanged (and the runtime check has the final
        // word) — no silent-correction loop.
        var revolveAxisCorrections = new ScopedInt();

        try
        {
            for (int round = 1; round <= MaxModelResponsesPerTurn; round++)
            {
                // Tools are advertised while the chain still has execution
                // budget; the final round exists to let the model phrase the
                // accumulated results, not to trigger more modeling.
                IReadOnlyList<ToolDefinition>? tools =
                    round < MaxModelResponsesPerTurn && ToolsEnabled ? _toolRegistry.All : null;

                var result = await _requestSender(protocol, tools, cancellationToken);

                if (!result.Success)
                {
                    errorText = result.ErrorMessage;
                    break;
                }

                if (result.ToolCalls.Count == 0)
                {
                    finalText = result.Reply;
                    break;
                }

                // One assistant message may carry multiple tool calls. The
                // protocol entry is ONE assistant message with ALL the
                // calls; each call then gets its own tool result message.
                protocol.Add(new OutboundMessage(OutboundMessage.RoleAssistant, null,
                    result.ToolCalls.Select(c =>
                        new OutboundToolCall(c.Id, c.FunctionName, c.ArgumentsJson)).ToList()));

                // The final round must not execute anything: refuse every
                // call there with a real tool result, never silently.
                if (round == MaxModelResponsesPerTurn)
                {
                    foreach (var refused in result.ToolCalls)
                    {
                        _chatLog.Add(ChatRole.Info,
                            $"模型在最终回复阶段请求工具调用（{refused.FunctionName}），已拒绝：本轮建模执行已结束。");
                        protocol.Add(new OutboundMessage(OutboundMessage.RoleTool,
                            BuildToolResultPayload(CreateBlockExecutionResult.Failure(
                                ToolExecutionStatus.ExecutionFailed,
                                "工具调用回合数已达上限，本次未执行建模操作。请重新发送请求。")),
                            ToolCallId: refused.Id));
                    }
                    errorText = "工具调用回合数已达上限，本次不再执行建模操作。请重新发送请求。";
                    break;
                }

                // Execute every call of this message in order; each gets its
                // own structured tool result keyed by its tool_call_id so the
                // model can match results to requests. Executions beyond the
                // turn's serial-chain budget are refused with a real result
                // as well — never silently dropped.
                foreach (var call in result.ToolCalls)
                {
                    if (turnExecutions.Count >= MaxToolExecutionsPerTurn)
                    {
                        protocol.Add(new OutboundMessage(OutboundMessage.RoleTool,
                            BuildToolResultPayload(CreateBlockExecutionResult.Failure(
                                ToolExecutionStatus.ExecutionFailed,
                                "本轮建模执行次数已达上限，该调用未执行。")),
                            ToolCallId: call.Id));
                        _chatLog.Add(ChatRole.Info,
                            $"本轮建模执行次数已达上限，工具 {call.FunctionName} 未执行。");
                        continue;
                    }

                    var execution = await ProcessToolCallAsync(call, cancellationToken, sketchesThisTurn, revolveAxisCorrections);
                    turnExecutions.Add((call.FunctionName, execution));
                    // Deterministic failures are remembered as (tool, exact
                    // args) so an identical resubmission in this turn is
                    // refused before it runs again:
                    //   - InvalidParameters: the call itself is malformed;
                    //   - VerificationFailed/ExecutionFailed WITH rolledBack:
                    //     the executor already undid its work — the part is
                    //     byte-for-byte back at the pre-call state (the
                    //     2026-10-23 T1 second failure: the model retried the
                    //     identical call after a rolled-back NRE and failed
                    //     identically 40s later). A retry is legitimate only
                    //     after a REAL precondition change, which inside one
                    //     turn cannot come from the model — the user must act
                    //     (new turn) or the parameters must change.
                    if (execution.Status == ToolExecutionStatus.InvalidParameters ||
                        ((execution.Status == ToolExecutionStatus.VerificationFailed ||
                          execution.Status == ToolExecutionStatus.ExecutionFailed) &&
                         execution.RolledBack))
                        _failedParameterCallsThisTurn.Add((call.FunctionName, call.ArgumentsJson));
                    TrackSketchForRevolveAdvice(call.FunctionName, execution, sketchesThisTurn);
                    protocol.Add(new OutboundMessage(OutboundMessage.RoleTool,
                        BuildToolResultPayload(execution), ToolCallId: call.Id));
                    _chatLog.Add(ChatRole.Info, $"建模执行结果：{execution.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (Exception ex)
        {
            errorText = $"请求发生意外错误：{ex.Message}";
        }

        _inFlight = false;
        _cts = null;
        if (_detached)
            return;

        // The turn ended without the model's own final text (cancel, request
        // error, or blank reply). If real executions happened this turn, say
        // so explicitly: a bare "已取消当前请求" would read as if the geometry
        // was cancelled with the request, which it was not. The request-level
        // error still gets its own Error card first (an HTTP/cap error has a
        // separate message the user should see as-is).
        if (string.IsNullOrWhiteSpace(finalText) &&
            (canceled || errorText != null) &&
            turnExecutions.Count > 0)
        {
            if (errorText != null)
                _chatLog.Add(ChatRole.Error, SanitizeModelTextForDisplay(errorText));
            _chatLog.Add(ChatRole.Info,
                DescribeInterruptedTurnOutcome(turnExecutions, canceled, null));
            _chatLog.Add(ChatRole.Assistant,
                "[执行记录]" + DescribeInterruptedTurnOutcome(turnExecutions, canceled, null) +
                "\n以系统记录的执行结果为准。请基于真实部件状态继续，不要重复已完成的建模。");
            RequestCompleted?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (canceled)
        {
            _chatLog.Add(ChatRole.Info, "已取消当前请求。");
        }
        else if (errorText != null)
        {
            // The error card is user-visible: any model-supplied fragment
            // inside the message is scrubbed of protocol markup first.
            _chatLog.Add(ChatRole.Error, SanitizeModelTextForDisplay(errorText));
        }
        else if (!string.IsNullOrWhiteSpace(finalText))
        {
            // Modeling-claim guard (2026-10-21 root cause): a turn with NO
            // tool executions whose text still claims modeling success
            // (e.g. reusing a prior SUBTRACT(3)) must not become the
            // displayed fact; a turn whose executions ALL failed must not be
            // overwritten into success. Decisions come only from this
            // turn's structured receipts — history is never evidence.
            var guardVerdict = ModelingClaimGuard.Evaluate(
                finalText,
                turnExecutions.Select(e => (e.ToolName, e.Result.Status.ToWireName())).ToList());
            if (guardVerdict.ShouldBlock)
            {
                _chatLog.Add(ChatRole.Info, "[系统核实] 模型文本声称建模成功，但与本轮真实执行记录不符，已拦截展示。");
                _chatLog.Add(ChatRole.Assistant, guardVerdict.ReplacementMessage ?? "");
                // Never drop the real failure reasons: append the structured
                // failure summary when the turn had failed executions (the
                // zero-execution case has no failures to summarize).
                var failureSummary = DescribeTurnExecutions(turnExecutions);
                if (failureSummary.Contains("未执行/失败"))
                    _chatLog.Add(ChatRole.Assistant, failureSummary);
                RequestCompleted?.Invoke(this, EventArgs.Empty);
                return;
            }
            // Model's own text is shown sanitized (tool-protocol markup
            // stripped — 2026-10-23): the raw invoke block is protocol
            // content, not chat prose. When this turn executed tools, the
            // controller appends its own assistant-role record afterwards so
            // a contradicting model text can never overwrite what really ran.
            _chatLog.Add(ChatRole.Assistant, SanitizeModelTextForDisplay(finalText));
            var receipt = DescribeTurnExecutions(turnExecutions);
            if (receipt.Length > 0)
                _chatLog.Add(ChatRole.Assistant, receipt);
        }
        else
        {
            _chatLog.Add(ChatRole.Error, "模型返回了空白回复。");
        }

        RequestCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// One-line status built solely from this turn's structured execution
    /// receipts (empty string when the turn executed nothing). Used to anchor
    /// the visible outcome to what actually happened, regardless of how the
    /// model phrased its reply.
    /// </summary>
    internal static string DescribeTurnExecutions(
        IReadOnlyList<(string ToolName, CreateBlockExecutionResult Result)> executions)
    {
        if (executions.Count == 0)
            return "";
        var success = new List<string>();
        var failed = new List<string>();
        foreach (var (toolName, execution) in executions)
        {
            if (execution.Status == ToolExecutionStatus.Success)
                success.Add(toolName);
            else
                failed.Add($"{toolName}（{execution.Status.ToWireName()}）");
        }
        var parts = new List<string>();
        if (success.Count > 0)
            parts.Add($"已执行 {string.Join("、", success)}");
        if (failed.Count > 0)
            parts.Add($"未执行/失败：{string.Join("、", failed)}");
        return "[执行记录]" + string.Join("；", parts) + "。";
    }

    /// <summary>
    /// Status line for a turn whose model requests did not finish normally
    /// after real tool executions. Built only from structured execution
    /// results — never from model text. Distinguishes confirmed successes from
    /// genuine failures, and marks an uncertain execution explicitly so no
    /// reader (user or model) is told geometry was committed when it may not be.
    /// </summary>
    internal static string DescribeInterruptedTurnOutcome(
        IReadOnlyList<(string ToolName, CreateBlockExecutionResult Result)> executions,
        bool canceled, string? errorText)
    {
        var success = new List<string>();
        var failed = new List<string>();
        ToolExecutionStatus? uncertainStatus = null;
        foreach (var (toolName, execution) in executions)
        {
            switch (execution.Status)
            {
                case ToolExecutionStatus.Success:
                    success.Add(toolName);
                    break;
                case ToolExecutionStatus.ExecutionUncertain:
                    uncertainStatus = execution.Status;
                    failed.Add($"{toolName}（{ToolExecutionStatusNames.ExecutionUncertain}）");
                    break;
                default:
                    failed.Add($"{toolName}（{execution.Status.ToWireName()}）");
                    break;
            }
        }

        var parts = new List<string>();
        if (success.Count > 0)
            parts.Add($"其中 {success.Count} 步已成功执行（{string.Join("、", success)}），几何已创建且未回滚");
        if (failed.Count > 0)
            parts.Add($"失败/未执行的步骤：{string.Join("、", failed)}");
        if (uncertainStatus != null)
            parts.Add("存在结果不确定的步骤，部件状态需人工核实后再继续");

        string head = canceled
            ? "本轮最终回复被用户取消"
            : "本轮最终回复未完成";
        if (errorText != null)
            head += $"：{errorText}";
        if (parts.Count > 0)
            head += "。" + string.Join("；", parts) + "。";
        else
            head += "；本轮没有已执行的建模步骤。";
        return head;
    }

    /// <summary>
    /// Validates the requested tool call and, only after user confirmation,
    /// executes it once. Every rejection path returns a structured result the
    /// model will receive; the executor is never called without confirmation,
    /// and at most once per user turn by construction (round gating above).
    /// </summary>
    private async Task<CreateBlockExecutionResult> ProcessToolCallAsync(
        ParsedToolCall call, CancellationToken cancellationToken,
        Dictionary<string, RevolveAxisAdvisor.SketchFacts> sketchesThisTurn,
        ScopedInt revolveAxisCorrections)
    {
        // 2026-10-23 live fix: a PARAMETER error is deterministic — the same
        // call with byte-identical arguments fails the same way. Re-running
        // it inside one turn can only repeat the failure (the live incident:
        // an unknown "target" field refused, then resubmitted unchanged).
        // The guard keys on (tool, exact arguments) and refuses the repeat
        // with an explicit correction demand; a genuinely different retry
        // has different JSON and passes. Non-parameter failures (target
        // resolution, NX state) are NOT deduplicated: the model may retry
        // after a legitimate state change (e.g. the user selects a body).
        if (_failedParameterCallsThisTurn.Contains((call.FunctionName, call.ArgumentsJson)))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.InvalidParameters,
                $"工具 {call.FunctionName} 已在本轮用完全相同的参数失败过（确定性错误不会因原样重试而改变；" +
                "上次失败的操作已回滚，部件未改变）。请修正参数后重新调用，或直接向用户说明问题；" +
                "用户下一步操作后（新回合）可重新尝试。");
        }
        if (!_toolRegistry.IsRegistered(call.FunctionName))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.UnknownTool,
                $"工具 {call.FunctionName} 未注册，本次请求未执行。当前可用工具：" +
                string.Join("、", AutonomousToolAllowlist.Tools) + "。");
        }

        if (call.FunctionName != CreateBlockTool.Name &&
            call.FunctionName != CreateCylinderTool.Name &&
            call.FunctionName != CreateConeTool.Name &&
            call.FunctionName != CreateSphereTool.Name &&
            call.FunctionName != CreateSketchTool.Name &&
            call.FunctionName != CreateExtrudeTool.Name &&
            call.FunctionName != CreateRevolveTool.Name &&
            call.FunctionName != BooleanOperationTool.Name &&
            call.FunctionName != CreateHoleTool.Name &&
            call.FunctionName != EdgeFinishToolContract.FilletName &&
            call.FunctionName != EdgeFinishToolContract.ChamferName &&
            call.FunctionName != LinearPatternToolContract.Name &&
            call.FunctionName != CircularPatternToolContract.Name &&
            call.FunctionName != MirrorToolContract.Name &&
            !StandardHoleFamilyForTool(call.FunctionName).HasValue)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.UnknownTool,
                $"工具 {call.FunctionName} 没有对应的执行器，本次请求未执行。");
        }

        ToolConfirmationRequest request;
        string requestDescription;
        if (call.FunctionName == CreateBlockTool.Name)
        {
            var blockParameters = new CreateBlockRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (blockParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            requestDescription =
                $"长方体：长 {FormatNumber(blockParameters.Length)} × " +
                $"宽 {FormatNumber(blockParameters.Width)} × 高 {FormatNumber(blockParameters.Height)} 毫米" +
                (blockParameters.OriginX == 0 && blockParameters.OriginY == 0 && blockParameters.OriginZ == 0
                    ? "，原点 (0, 0, 0)。"
                    : $"，原点 ({FormatNumber(blockParameters.OriginX)}, {FormatNumber(blockParameters.OriginY)}, {FormatNumber(blockParameters.OriginZ)})。");
            request = ToolConfirmationRequest.ForBlock(call.Id, call.FunctionName, blockParameters);
        }
        else if (call.FunctionName == CreateCylinderTool.Name)
        {
            var cylinderParameters = new CreateCylinderRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (cylinderParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            requestDescription =
                $"圆柱体：直径 {FormatNumber(cylinderParameters.Diameter)} × " +
                $"高 {FormatNumber(cylinderParameters.Height)} 毫米，轴向 +Z" +
                (cylinderParameters.OriginX == 0 && cylinderParameters.OriginY == 0 && cylinderParameters.OriginZ == 0
                    ? "，底面圆心 (0, 0, 0)。"
                    : $"，底面圆心 ({FormatNumber(cylinderParameters.OriginX)}, {FormatNumber(cylinderParameters.OriginY)}, {FormatNumber(cylinderParameters.OriginZ)})。");
            request = ToolConfirmationRequest.ForCylinder(call.Id, call.FunctionName, cylinderParameters);
        }
        else if (call.FunctionName == CreateConeTool.Name)
        {
            var coneParameters = new CreateConeRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (coneParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            string coneShape = coneParameters.TopDiameter > 0
                ? $"圆台：底面直径 {FormatNumber(coneParameters.BaseDiameter)} × 顶部直径 {FormatNumber(coneParameters.TopDiameter)} × 高 {FormatNumber(coneParameters.Height)} 毫米"
                : $"圆锥体：底面直径 {FormatNumber(coneParameters.BaseDiameter)} × 高 {FormatNumber(coneParameters.Height)} 毫米（锥顶收敛）";
            requestDescription =
                $"{coneShape}，轴向 +Z" +
                (coneParameters.OriginX == 0 && coneParameters.OriginY == 0 && coneParameters.OriginZ == 0
                    ? "，底面圆心 (0, 0, 0)。"
                    : $"，底面圆心 ({FormatNumber(coneParameters.OriginX)}, {FormatNumber(coneParameters.OriginY)}, {FormatNumber(coneParameters.OriginZ)})。");
            request = ToolConfirmationRequest.ForCone(call.Id, call.FunctionName, coneParameters);
        }
        else if (call.FunctionName == CreateSphereTool.Name)
        {
            var sphereParameters = new CreateSphereRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (sphereParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            requestDescription =
                $"球体：直径 {FormatNumber(sphereParameters.Diameter)} 毫米" +
                (sphereParameters.CenterX == 0 && sphereParameters.CenterY == 0 && sphereParameters.CenterZ == 0
                    ? "，球心 (0, 0, 0)。"
                    : $"，球心 ({FormatNumber(sphereParameters.CenterX)}, {sphereParameters.CenterY:F3}, {sphereParameters.CenterZ:F3})。");
            request = ToolConfirmationRequest.ForSphere(call.Id, call.FunctionName, sphereParameters);
        }
        else if (call.FunctionName == CreateSketchTool.Name)
        {
            var sketchParameters = new CreateSketchRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (sketchParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            string profileText = sketchParameters.ProfileType == SketchProfileType.Rectangle
                ? $"矩形 {FormatNumber(sketchParameters.RectangleWidth)} × {FormatNumber(sketchParameters.RectangleHeight)} 毫米"
                : $"圆形直径 {FormatNumber(sketchParameters.CircleDiameter)} 毫米";
            string planeText = sketchParameters.PlaneMode switch
            {
                SketchPlaneMode.SelectedPlane => "用户选中的平面/基准面",
                SketchPlaneMode.WcsXY => "绝对坐标 XY 平面",
                SketchPlaneMode.WcsXZ => "绝对坐标 XZ 平面",
                _ => "绝对坐标 YZ 平面",
            };
            requestDescription =
                $"草图：{profileText}，平面 {planeText}" +
                (sketchParameters.CenterU == 0 && sketchParameters.CenterV == 0
                    ? "，轮廓中心位于平面原点。"
                    : $"，轮廓中心偏移 (u {FormatNumber(sketchParameters.CenterU)}, v {FormatNumber(sketchParameters.CenterV)}) 毫米（草图局部坐标）。");
            request = ToolConfirmationRequest.ForSketch(call.Id, call.FunctionName, sketchParameters);
        }
        else if (call.FunctionName == CreateExtrudeTool.Name)
        {
            var extrudeParameters = new CreateExtrudeRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (extrudeParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            requestDescription =
                $"拉伸：草图 {extrudeParameters.SketchRef}，距离 {FormatNumber(extrudeParameters.Distance)} 毫米，" +
                $"{(extrudeParameters.Direction == ExtrudeDirection.Reverse ? "沿法向反向" : "沿草图法向")}。";
            request = ToolConfirmationRequest.ForExtrude(call.Id, call.FunctionName, extrudeParameters);
        }
        else if (call.FunctionName == CreateRevolveTool.Name)
        {
            var revolveParameters = new CreateRevolveRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (revolveParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            // Pre-execution axis check (this turn's sketch facts + the user's
            // request text, no NX calls). Two replacement reasons:
            //   - InfeasibleAxis: the axis cannot lie in the sketch plane —
            //     firing it is a guaranteed preconditions_changed;
            //   - ScaleOutlier: the axis is legal but the expected revolve
            //     dwarfs the profile (the live case: a 20×10 rectangle at
            //     u=150 becoming a ~320 mm ring when a ~30 mm body matched
            //     the request's evident intent).
            // A replacement happens ONLY in autonomous mode AND only when the
            // axis was model-inferred: an explicitly user-specified axis
            // (wording detected in the current user turn) is never rewritten
            // — in confirm mode the card shows the model's axis and the user
            // approves it. The unknown-outcome gate (part state uncertain)
            // disables any rewrite. Ambiguous cases keep the requested axis
            // with the uncertainty stated.
            sketchesThisTurn.TryGetValue(revolveParameters.SketchRef,
                out var sketchFacts);
            string currentUserTurnText = FindCurrentUserTurnText();
            // Deterministic three-way intent classification (requirement 三):
            // the MODEL's axis_mode is NOT the user's intent — a model fills
            // axis_mode=selected_axis as its DEFAULT when the request names
            // no axis (the live failure: "绕轴旋转360°" + model-default
            // selected_axis + empty selection → hard stop demanding a
            // selection). The user's TEXT decides the path.
            var intent = RevolveAxisAdvisor.ClassifyAxisIntent(currentUserTurnText);
            bool axisUserSpecified = intent != RevolveAxisAdvisor.AxisIntentKind.Unspecified;
            var advice = RevolveAxisAdvisor.Assess(sketchFacts, revolveParameters.AxisMode);
            bool autonomousRevolve = !_modePolicy.RequiresConfirmation(
                _modeManager?.CurrentMode ?? ExecutionMode.Confirm,
                CreateRevolveTool.Name);
            bool unknownGateForRevolve = _ledger != null &&
                _ledger.HasUnacknowledgedAttempt(CreateRevolveTool.Name);

            // PATH 2 (unified, cross-plane): the request names NO axis at
            // all — the model's returned axis (selected_axis OR wcs_x/y/z)
            // is merely its GUESS, never the user's decision. Normalize to
            // the advisor's best-ranked candidate regardless of which axis
            // the model guessed and which plane the sketch sits on: the
            // advisor evaluates all three WCS axes through each plane's REAL
            // world frame (TryGetWcsPlaneFrame + world-space profile math),
            // so the choice is plane-independent by construction. The old
            // SelectedAxis-only trigger let a model's wcs_* guess slip
            // through un-evaluated whenever it happened to be scale-
            // reasonable — plane-dependent behavior (the cross-plane gap).
            // Autonomous mode only — confirm mode shows the model's request
            // on the card and the user decides.
            if (intent == RevolveAxisAdvisor.AxisIntentKind.Unspecified &&
                autonomousRevolve &&
                !unknownGateForRevolve &&
                revolveAxisCorrections.Value < MaxRevolveAxisCorrectionsPerTurn &&
                advice.RankedCandidates.Count > 0 &&
                advice.RankedCandidates[0].Mode != revolveParameters.AxisMode &&
                !_detached &&
                !cancellationToken.IsCancellationRequested)
            {
                var chosen = advice.RankedCandidates[0];
                revolveAxisCorrections.Value++;
                _chatLog.Add(ChatRole.Info,
                    $"自主模式轴规划：请求未指定旋转轴，已根据草图平面和轮廓尺度自动选择 " +
                    $"{DescribeRevolveAxis(chosen.Mode)}（{advice.Explanation}）。" +
                    "如需其他轴请在下一条消息明确说明。");
                revolveParameters = revolveParameters with { AxisMode = chosen.Mode };
            }
            else if (advice.CorrectionKind is RevolveAxisCorrectionKind.InfeasibleAxis
                    or RevolveAxisCorrectionKind.ScaleOutlier &&
                advice.SuggestedReplacement is { } replacement &&
                autonomousRevolve &&
                !axisUserSpecified &&
                !unknownGateForRevolve &&
                revolveAxisCorrections.Value < MaxRevolveAxisCorrectionsPerTurn &&
                !_detached &&
                !cancellationToken.IsCancellationRequested)
            {
                revolveAxisCorrections.Value++;
                string kindText = advice.CorrectionKind == RevolveAxisCorrectionKind.InfeasibleAxis
                    ? "轴修正"
                    : "尺度修正";
                _chatLog.Add(ChatRole.Info,
                    $"自主模式{kindText}：{advice.Explanation} " +
                    $"已改用 {DescribeRevolveAxis(replacement.Mode)}（基于草图位置和尺寸的自主选择），" +
                    "如需其他轴请在下一条消息明确说明。");
                revolveParameters = revolveParameters with { AxisMode = replacement.Mode };
            }
            else if (advice.CorrectionKind != RevolveAxisCorrectionKind.None)
            {
                // Keep the model's request (confirmation flow, user-specified
                // axis, ambiguity, or budget spent): surface the advice so a
                // later failure arrives with an actionable explanation.
                _chatLog.Add(ChatRole.Info, $"旋转轴预检：{advice.Explanation}");
            }

            requestDescription =
                $"旋转体：草图 {revolveParameters.SketchRef}，轴 {DescribeRevolveAxis(revolveParameters.AxisMode)}，{FormatNumber(revolveParameters.AngleDeg)} 度。";
            request = ToolConfirmationRequest.ForRevolve(call.Id, call.FunctionName, revolveParameters);
        }
        else if (call.FunctionName == BooleanOperationTool.Name)
        {
            var booleanParameters = new BooleanOperationRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (booleanParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未修改任何几何。{parseError}");
            }

            string opText = booleanParameters.Operation switch
            {
                BooleanOperationType.Unite => "并集",
                BooleanOperationType.Subtract => "差集",
                _ => "交集",
            };
            requestDescription =
                $"布尔{opText}：目标体 {booleanParameters.TargetBodyRef}，工具体 {booleanParameters.ToolBodyRef}" +
                "（运算后工具体被消耗，结果归属目标体）。";
            request = ToolConfirmationRequest.ForBoolean(call.Id, call.FunctionName, booleanParameters);
        }
        else if (call.FunctionName == CreateHoleTool.Name)
        {
            var holeParameters = new CreateHoleRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (holeParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            string locationText = holeParameters.LocationU == 0 && holeParameters.LocationV == 0
                ? "面中心"
                : $"面中心偏移 (u {FormatNumber(holeParameters.LocationU)}, v {FormatNumber(holeParameters.LocationV)}) 毫米";
            requestDescription =
                $"简单盲孔：直径 {FormatNumber(holeParameters.Diameter)} × 深 {FormatNumber(holeParameters.Depth)} 毫米" +
                $"（钻尖角 {FormatNumber(holeParameters.TipAngleDeg)}°，孔位 {locationText}），" +
                $"放置面 {holeParameters.PlacementFaceRef}。";
            request = ToolConfirmationRequest.ForHole(call.Id, call.FunctionName, holeParameters);
        }
        else if (StandardHoleFamilyForTool(call.FunctionName) is { } stdFamily)
        {
            var stdParameters = new CreateStandardHoleRequestParser(call.FunctionName, stdFamily)
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (stdParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            string familyText = stdFamily switch
            {
                StandardHoleFamily.Threaded => "螺纹孔",
                StandardHoleFamily.SimpleClearance => "螺纹间隙孔",
                StandardHoleFamily.Counterbored => "沉头孔（Counterbored）",
                _ => "埋头孔（Countersink）",
            };
            string depthText = stdFamily switch
            {
                StandardHoleFamily.Threaded when stdParameters.HoleDepthMm is { } hd && stdParameters.ThreadDepthMm is { } td =>
                    $"，孔深 {FormatNumber(hd)} × 螺纹深 {FormatNumber(td)} 毫米",
                StandardHoleFamily.Threaded => "，深度用标准表参考值",
                StandardHoleFamily.SimpleClearance when stdParameters.HoleDepthMm is { } sd =>
                    $"，孔深 {FormatNumber(sd)} 毫米",
                StandardHoleFamily.SimpleClearance => "，孔径/倒角/尖角按标准表",
                _ => "，全部尺寸按标准表",
            };
            string locationText = stdParameters.PlacementMode == StandardHolePlacementMode.SelectedPoint
                ? "选定点（孔心=该点，放置面由宿主解析）"
                : stdParameters.LocationU == 0 && stdParameters.LocationV == 0
                    ? "面中心"
                    : $"面中心偏移 (u {FormatNumber(stdParameters.LocationU)}, v {FormatNumber(stdParameters.LocationV)}) 毫米";
            string placementText = stdParameters.PlacementMode == StandardHolePlacementMode.SelectedPoint
                ? "放置：选定点"
                : $"放置面 {stdParameters.PlacementFaceRef}";
            requestDescription =
                $"标准{familyText}：规格 {stdParameters.Size}{depthText}（孔位 {locationText}），{placementText}。";
            request = ToolConfirmationRequest.ForStandardHole(call.Id, call.FunctionName, stdParameters);
        }
        else if (call.FunctionName == EdgeFinishToolContract.FilletName)
        {
            var filletParameters = new CreateFilletRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (filletParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未修改任何几何。{parseError}");
            }
            requestDescription =
                $"边倒圆：半径 {FormatNumber(filletParameters.RadiusMm)} 毫米，" +
                "目标为当前选中的实体边（执行时重新验证，确认卡由宿主显示实际边数）。";
            request = ToolConfirmationRequest.ForEdgeFinish(call.Id, call.FunctionName, null, filletParameters);
        }
        else if (call.FunctionName == EdgeFinishToolContract.ChamferName)
        {
            var chamferParameters = new CreateChamferRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (chamferParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未修改任何几何。{parseError}");
            }
            requestDescription =
                $"倒斜角：对称距离 {FormatNumber(chamferParameters.DistanceMm)} 毫米（C 形等距倒角），" +
                "目标为当前选中的实体边（执行时重新验证，确认卡由宿主显示实际边数）。";
            request = ToolConfirmationRequest.ForEdgeFinish(call.Id, call.FunctionName, chamferParameters, null);
        }
        else if (call.FunctionName == LinearPatternToolContract.Name)
        {
            var patternParameters = new CreateLinearPatternRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (patternParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }
            string modeText = patternParameters.Mode == LinearPatternMode.Feature
                ? "阵列特征（最近创建的特征，或从部件特征树重新识别）"
                : "阵列几何体（最近创建的实体）";
            string dirText = patternParameters.Direction switch
            {
                LinearPatternDirection.PlusX => "+X",
                LinearPatternDirection.MinusX => "-X",
                LinearPatternDirection.PlusY => "+Y",
                LinearPatternDirection.MinusY => "-Y",
                LinearPatternDirection.PlusZ => "+Z",
                _ => "-Z",
            };
            if (patternParameters.Direction2 != null)
            {
                string dir2Text = patternParameters.Direction2 switch
                {
                    LinearPatternDirection.PlusX => "+X",
                    LinearPatternDirection.MinusX => "-X",
                    LinearPatternDirection.PlusY => "+Y",
                    LinearPatternDirection.MinusY => "-Y",
                    LinearPatternDirection.PlusZ => "+Z",
                    _ => "-Z",
                };
                requestDescription =
                    $"线性阵列（双向）：{modeText}，方向 {dirText} {patternParameters.Count} 个 × " +
                    $"方向 {dir2Text} {patternParameters.Count2} 个" +
                    $"（间距 {FormatNumber(patternParameters.PitchMm)} / {FormatNumber(patternParameters.Pitch2Mm ?? 0)} 毫米，" +
                    $"一次原生矩形阵列共 {patternParameters.Count * patternParameters.Count2!.Value} 个实例）。" +
                    "目标由宿主自动解析并重新验证。";
            }
            else
            {
                requestDescription =
                    $"线性阵列：{modeText}，方向 {dirText}，共 {patternParameters.Count} 个实例" +
                    $"（间距 {FormatNumber(patternParameters.PitchMm)} 毫米）。目标由宿主自动解析并重新验证。";
            }
            request = ToolConfirmationRequest.ForLinearPattern(call.Id, call.FunctionName, patternParameters);
        }
        else if (call.FunctionName == CircularPatternToolContract.Name)
        {
            var circularParameters = new CreateCircularPatternRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (circularParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }
            string cirModeText = circularParameters.Mode == CircularPatternMode.Feature
                ? "环形阵列特征（最近创建的特征，或从部件特征树重新识别）"
                : "环形阵列几何体（最近创建的实体，或当前选中的实体）";
            string cirAxisText = circularParameters.Axis switch
            {
                CircularPatternAxis.PlusX => "+X 轴（过原点）",
                CircularPatternAxis.PlusY => "+Y 轴（过原点）",
                CircularPatternAxis.PlusZ => "+Z 轴（过原点）",
                CircularPatternAxis.DatumAxis => "当前选中的基准轴",
                _ => "当前选中的直线（边或直线特征）",
            };
            string cirAngleText = circularParameters.TotalAngleDeg is { } deg
                ? $"总角度 {deg:0.###}°（相邻实例角距约 {deg / (circularParameters.Count - 1):0.###}°）"
                : $"完整圆周 360°（相邻实例角距 {360.0 / circularParameters.Count:0.###}°）";
            requestDescription =
                $"环形阵列：{cirModeText}，旋转轴 {cirAxisText}，共 {circularParameters.Count} 个实例（含原始实例），{cirAngleText}。" +
                "目标与轴对象由宿主解析并重新验证（绝对轴/选中的基准轴/选中的直线边）。";
            request = ToolConfirmationRequest.ForCircularPattern(call.Id, call.FunctionName, circularParameters);
        }
        else if (call.FunctionName == MirrorToolContract.Name)
        {
            var mirrorParameters = new CreateMirrorRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (mirrorParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }
            string mirModeText = mirrorParameters.Mode == MirrorMode.Geometry
                ? "镜像几何体（选中的实体，可多个；原实体保留）"
                : "镜像特征（选中的建模特征，可多个；历史保留）";
            requestDescription =
                $"镜像：{mirModeText}。待镜像对象与镜像面（基准面或平面面）均由宿主从当前选区解析" +
                "（对象与镜像面按角色分离，执行时重新验证）。";
            request = ToolConfirmationRequest.ForMirror(call.Id, call.FunctionName, mirrorParameters);
        }
        else
        {
            // The tool-name gate above guarantees every registered modeling
            // tool has a branch; this is unreachable in practice.
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.UnknownTool,
                $"工具 {call.FunctionName} 没有对应的执行器，本次请求未执行。");
        }

        // Authorization decision at the backend dispatch stage — read the
        // effective mode here, never from the UI. Confirm mode (and any
        // tool not on the autonomous allowlist) always takes the existing
        // confirmation path.
        var effectiveMode = _modeManager?.CurrentMode ?? ExecutionMode.Confirm;

        // Unknown-outcome gate: a previous execution of this tool entered the
        // executor and never reported a terminating result (window torn down
        // mid-turn, or an exception the executor could not attribute).
        //   - AUTONOMOUS mode: NO human confirmation dialog. A read-only NX
        //     probe decides: committed geometry found → the pending attempt
        //     is recovered as a real fact (and the turn stops so the user
        //     sees the truth); no trace → the attempt is terminally failed
        //     and THIS call may proceed on its own merit; cannot judge →
        //     report the unknown state and stop the turn — never retry
        //     blind, never a modal.
        //   - CONFIRM mode: unchanged — the existing confirmation flow (and
        //     its unknown-state notice inside the dialog) applies.
        var unknownGateActive = _ledger != null &&
            _ledger.HasUnacknowledgedAttempt(call.FunctionName);
        if (!_modePolicy.RequiresConfirmation(effectiveMode, call.FunctionName) &&
            unknownGateActive)
        {
            var recovery = ResolveUnknownOutcome(call);
            if (recovery.Stop)
            {
                // Unknown state, committed-but-recovered, or unjudgeable:
                // the turn ends here with a clear report. Nothing executes.
                return recovery.Outcome;
            }
            // recovery.Stop == false: the probe proved no committed trace —
            // the attempt was terminally failed, so THIS call runs through
            // the normal autonomous path below (no dialog). Re-read the
            // gate: the pending entries were just terminally resolved.
            unknownGateActive = _ledger != null &&
                _ledger.HasUnacknowledgedAttempt(call.FunctionName);
            _chatLog.Add(ChatRole.Info,
                "只读核查确认：先前的未决执行未在部件中留下任何几何结果。" +
                "本次请求按正常流程继续。");
        }
        if (!_modePolicy.RequiresConfirmation(effectiveMode, call.FunctionName) &&
            !unknownGateActive)
        {
            _chatLog.Add(ChatRole.Info, $"自主模式：正在创建{requestDescription}");
            // Re-check just before mutating the model: a mode switch between
            // the decision and this point must fall back to the confirmation
            // flow — never straight into execution.
            var recheckMode = _modeManager!.CurrentMode;
            if (recheckMode != ExecutionMode.Autonomous)
            {
                _chatLog.Add(ChatRole.Info,
                    "执行前检测到模式已切换回确认模式，本次操作需确认后才会执行。");
            }
            else if (_detached || cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            else
            {
                return ExecuteConfirmedTool(request, expectedWorkPartFullPath: null);
            }
        }
        else
        {
            _chatLog.Add(ChatRole.Info, $"模型请求创建{requestDescription}等待你的确认。");
        }

        var decision = await _confirmationHandler(request, cancellationToken);
        if (decision == null)
        {
            // Confirmation flow aborted (window closing / request cancelled):
            // cancel the turn; nothing must execute afterwards.
            throw new OperationCanceledException(cancellationToken);
        }
        if (!decision.Confirmed)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.UserCancelled,
                "用户取消了本次建模操作，未对部件做任何修改。");
        }
        if (_detached)
        {
            // The window closed while the confirmation was in flight: a late
            // "confirm" must not execute after Detach.
            throw new OperationCanceledException(cancellationToken);
        }

        // The user just reviewed this exact tool's pending state and chose to
        // proceed: that decision is the explicit human verification the
        // unknown-outcome gate required.
        AcknowledgeUnknownForTool(call.FunctionName);

        return ExecuteConfirmedTool(request, decision.ExpectedWorkPartFullPath);
    }

    private readonly record struct UnknownOutcomeRecovery(bool Stop, CreateBlockExecutionResult Outcome);

    /// <summary>Per-attempt pre-execution feature snapshots (ledger id →
    /// snapshot + the attempt's structured boolean operation type), consumed
    /// by the unknown-state probe for attribution. Bounded by the ledger's
    /// own lifetime; stale entries are harmless.</summary>
    private readonly record struct AttemptSnapshot(
        IReadOnlySet<string>? FeatureIds, BooleanOperationType? BooleanOperation);
    private readonly Dictionary<string, AttemptSnapshot> _attemptSnapshots = new(StringComparer.Ordinal);

    /// <summary>
    /// Autonomous-mode unknown-outcome recovery, driven ONLY by a read-only
    /// NX probe (never by model text, never by a user dialog):
    ///   1. Probe finds committed geometry of this tool's kind → the prior
    ///      attempt DID produce a result. The pending ledger entry is
    ///      recovered as a Recorded fact (Success fact for modeling tools is
    ///      NOT claimed — the probe cannot verify parameters — so the entry
    ///      carries the probe's finding as an ExecutionFailed-with-message
    ///      terminating record meaning "geometry exists, verify in NX"), the
    ///      chat is told the truth, and the turn STOPS: repeating the
    ///      request would risk duplicate geometry.
    ///   2. Probe proves no trace → the pending attempt is terminally
    ///      failed (RecordResult with ExecutionFailed + rolledBack=false
    ///      is honest: the part carries nothing). Stop=false → this call
    ///      continues through the normal path.
    ///   3. Probe cannot judge (null) → report and STOP. No retry, no
    ///      dialog, no ledger downgrade.
    /// </summary>
    private UnknownOutcomeRecovery ResolveUnknownOutcome(ParsedToolCall call)
    {
        string summary = $"工具 {call.FunctionName}";
        var pending = _ledger!.Snapshot()
            .Where(e => e.ToolName == call.FunctionName &&
                        e.State == ModelingExecutionState.Attempting && !e.Acknowledged)
            .ToList();
        if (pending.Count > 0)
            summary = pending[^1].OperationSummary;

        // Attribution snapshot: prefer the newest pending attempt's own
        // pre-execution snapshot (each attempt snapshotted the part before
        // BeginAttempt — features absent from it are attributable to that
        // attempt).
        IReadOnlySet<string>? snapshot = null;
        BooleanOperationType? pendingBooleanOperation = null;
        foreach (var entry in Enumerable.Reverse(pending))
        {
            if (_attemptSnapshots.TryGetValue(entry.LedgerId, out var snap))
            {
                // Use the newest pending attempt's snapshot even when it is
                // an EMPTY set (a valid baseline: no matching features
                // existed before the attempt). Only a missing entry (null
                // struct) is skipped.
                snapshot = snap.FeatureIds;
                pendingBooleanOperation = snap.BooleanOperation;
                break;
            }
        }

        ModelFeatureProbeResult? probe = null;
        try
        {
            probe = _toolExecutor?.ProbeCommittedFeature(
                call.FunctionName, summary, snapshot, pendingBooleanOperation);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("probe.committed", $"controller probe FAILED: {ex.GetType().Name}: {ex.Message}");
            probe = null;
        }

        if (probe == null)
        {
            string message = "存在未确认的上一轮执行结果，且只读核查无法判断部件是否已被修改。" +
                "为避免重复建模，本轮自动流程在此停止；请在 NX 中核实部件状态后重新发送请求。";
            _chatLog.Add(ChatRole.Error, message);
            return new UnknownOutcomeRecovery(true, CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionUncertain, message));
        }

        if (probe.Committed)
        {
            // Recover the pending attempts as terminating facts: geometry
            // EXISTS. The probe cannot verify exact parameters, so the
            // recorded status is a structured failure carrying the finding
            // (never a claimed Success) — enough to close the unknown state
            // and to tell the model the truth.
            foreach (var entry in pending)
            {
                _ledger.RecordResult(entry.LedgerId, CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.ExecutionFailed,
                    probe.Message, rolledBack: false));
            }
            _chatLog.Add(ChatRole.Error,
                "存在未确认的上一轮执行结果，且只读核查发现部件中已有该类已提交特征。" +
                probe.Message +
                "为避免重复建模，本轮自动流程在此停止；请核实该特征是否即为目标结果，" +
                "或撤销后重新发送请求。");
            return new UnknownOutcomeRecovery(true, CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "先前的执行已在部件中留下几何结果，继续执行会重复建模。请先核实或撤销后再试。"));
        }

        // No committed trace: the attempt terminally failed — record so.
        foreach (var entry in pending)
        {
            _ledger.RecordResult(entry.LedgerId, CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"只读核查确认未在部件中产生几何结果：{probe.Message}",
                rolledBack: false));
        }
        return new UnknownOutcomeRecovery(false, CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.ExecutionFailed, probe.Message));
    }

    private CreateBlockExecutionResult ExecuteConfirmedTool(
        ToolConfirmationRequest request, string? expectedWorkPartFullPath)
    {
        // Pre-attempt feature-identity snapshot (READ-ONLY): taken BEFORE
        // BeginAttempt so a later unknown-state probe can attribute a
        // matching feature to THIS attempt — pre-existing same-kind
        // features are excluded from the evidence. For a boolean attempt
        // the snapshot is scoped to the attempt's STRUCTURED operation type
        // (unite→UNITE / subtract→SUBTRACT / intersect→INTERSECT — never
        // all three). Null snapshot (no work part) simply disables
        // attribution; the probe then fails safe.
        IReadOnlySet<string>? featureSnapshot = null;
        BooleanOperationType? booleanOperationType =
            request.ParametersKind == ToolParametersKind.Boolean
                ? request.BooleanParameters!.Operation
                : null;
        try
        {
            string? featureKind = booleanOperationType switch
            {
                BooleanOperationType.Unite => "UNITE",
                BooleanOperationType.Subtract => "SUBTRACT",
                BooleanOperationType.Intersect => "INTERSECT",
                _ => null,
            };
            featureSnapshot = _toolExecutor?.ProbeFeatureSnapshot(
                request.ToolName, featureKind);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("probe.snapshot", $"controller snapshot FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        // Register BEFORE the executor runs; the entry stays Attempting until
        // a terminating result arrives. If Detach/exception cuts this call
        // short of RecordResult, the entry is the durable "result unknown"
        // fact — never auto-downgraded to "not executed".
        string? ledgerId = _ledger?.BeginAttempt(
            request.ToolName, DescribePendingOperation(request));
        _attemptSnapshots[ledgerId ?? ""] = new AttemptSnapshot(featureSnapshot, booleanOperationType);
        try
        {
            var result = request.ParametersKind switch
            {
                ToolParametersKind.Block => _toolExecutor!.ExecuteCreateBlock(
                    request.BlockParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Cylinder => _toolExecutor!.ExecuteCreateCylinder(
                    request.CylinderParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Cone => _toolExecutor!.ExecuteCreateCone(
                    request.ConeParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Sphere => _toolExecutor!.ExecuteCreateSphere(
                    request.SphereParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Sketch => _toolExecutor!.ExecuteCreateSketch(
                    request.SketchParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Extrude => _toolExecutor!.ExecuteCreateExtrude(
                    request.ExtrudeParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Revolve => _toolExecutor!.ExecuteCreateRevolve(
                    request.RevolveParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Hole => _toolExecutor!.ExecuteCreateHole(
                    request.HoleParameters!, expectedWorkPartFullPath),
                ToolParametersKind.StandardHole => _toolExecutor!.ExecuteCreateStandardHole(
                    request.StandardHoleParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Fillet => _toolExecutor!.ExecuteCreateFillet(
                    request.FilletParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Chamfer => _toolExecutor!.ExecuteCreateChamfer(
                    request.ChamferParameters!, expectedWorkPartFullPath),
                ToolParametersKind.LinearPattern => _toolExecutor!.ExecuteCreateLinearPattern(
                    request.LinearPatternParameters!, expectedWorkPartFullPath),
                ToolParametersKind.CircularPattern => _toolExecutor!.ExecuteCreateCircularPattern(
                    request.CircularPatternParameters!, expectedWorkPartFullPath),
                ToolParametersKind.Mirror => _toolExecutor!.ExecuteCreateMirror(
                    request.MirrorParameters!, expectedWorkPartFullPath),
                _ => _toolExecutor!.ExecuteBooleanOperation(
                    request.BooleanParameters!, expectedWorkPartFullPath),
            };
            // A returned result is always a terminating fact (success OR
            // structured failure); record it immediately, never later in the
            // turn, so a Detach cannot erase it.
            ledgerIdAndResultCheck(ledgerId, result);
            // Phase 9.5: remember the most recent SUCCESSFUL body-creating
            // result's body reference (JournalIdentifier form, e.g.
            // "BLOCK(5)") for the edge-finish auto-target resolution. Only
            // real executor-verified refs are recorded — never model text.
            if (result.Status == ToolExecutionStatus.Success)
            {
                string? createdRef = ExtractDetailsString(result.DetailsJson, "newBodyRef");
                if (!string.IsNullOrWhiteSpace(createdRef))
                    LastCreatedBodyRef = createdRef;
                // Phase 9.6: feature refs (hole/blend/chamfer features) feed
                // the linear-pattern FEATURE mode; body refs feed GEOMETRY.
                string? featureRef = ExtractDetailsString(result.DetailsJson, "featureRef");
                if (!string.IsNullOrWhiteSpace(featureRef))
                    LastCreatedFeatureRef = featureRef;
            }
            return result;
        }
        catch
        {
            // No recording here: if the executor threw, we do NOT know whether
            // geometry was committed. The Attempting entry stays as the honest
            // "result unknown" state.
            throw;
        }
    }

    private void ledgerIdAndResultCheck(string? ledgerId, CreateBlockExecutionResult result)
    {
        if (ledgerId != null)
            _ledger!.RecordResult(ledgerId, result);
    }

    internal static string DescribePendingOperation(ToolConfirmationRequest request) =>
        request.ParametersKind switch
        {
            ToolParametersKind.Block => $"长方体 {Format(request.BlockParameters!.Length)}×{Format(request.BlockParameters.Width)}×{Format(request.BlockParameters.Height)}mm",
            ToolParametersKind.Cylinder => $"圆柱 直径{Format(request.CylinderParameters!.Diameter)} 高{Format(request.CylinderParameters.Height)}mm",
            ToolParametersKind.Cone => $"圆台/圆锥 底{Format(request.ConeParameters!.BaseDiameter)} 高{Format(request.ConeParameters.Height)}mm",
            ToolParametersKind.Sphere => $"球 直径{Format(request.SphereParameters!.Diameter)}mm",
            ToolParametersKind.Sketch => $"草图 {request.SketchParameters!.ProfileType}",
            ToolParametersKind.Extrude => $"拉伸 {Format(request.ExtrudeParameters!.Distance)}mm",
            ToolParametersKind.Revolve => $"旋转 {Format(request.RevolveParameters!.AngleDeg)}度",
            ToolParametersKind.Hole => $"孔 直径{Format(request.HoleParameters!.Diameter)} 深{Format(request.HoleParameters.Depth)}mm",
            ToolParametersKind.StandardHole => $"标准孔 {request.StandardHoleParameters!.Family} {request.StandardHoleParameters.Size}",
            ToolParametersKind.Fillet => $"圆角 R{Format(request.FilletParameters!.RadiusMm)}mm",
            ToolParametersKind.Chamfer => $"倒角 C{Format(request.ChamferParameters!.DistanceMm)}mm",
            ToolParametersKind.LinearPattern => $"线性阵列 {request.LinearPatternParameters!.Count}×{Format(request.LinearPatternParameters.PitchMm)}mm",
            ToolParametersKind.CircularPattern => $"环形阵列 {request.CircularPatternParameters!.Count}×{request.CircularPatternParameters.Axis}",
            ToolParametersKind.Mirror => $"镜像 {request.MirrorParameters!.Mode}",
            _ => $"布尔 {request.BooleanParameters!.Operation}",
        };

    /// <summary>Maps a standard-hole tool name to its family; null for any
    /// other tool name (the single dispatch gate for the four tools).</summary>
    private static StandardHoleFamily? StandardHoleFamilyForTool(string toolName) => toolName switch
    {
        StandardHoleToolContract.ThreadedName => StandardHoleFamily.Threaded,
        StandardHoleToolContract.ClearanceName => StandardHoleFamily.SimpleClearance,
        StandardHoleToolContract.CounterboredName => StandardHoleFamily.Counterbored,
        StandardHoleToolContract.CountersunkName => StandardHoleFamily.Countersunk,
        _ => null,
    };

    private static string Format(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string DescribeRevolveAxis(RevolveAxisMode mode) => mode switch
    {
        RevolveAxisMode.SelectedAxis => "用户选中的直线/基准轴",
        RevolveAxisMode.WcsX => "绝对坐标 X 轴",
        RevolveAxisMode.WcsY => "绝对坐标 Y 轴",
        _ => "绝对坐标 Z 轴",
    };

    /// <summary>
    /// The current user turn's request text: the newest User message in the
    /// chat log (StartSend appends it before any tool call runs). Empty when
    /// none — the advisor then treats the axis as model-inferred.
    /// </summary>
    private string FindCurrentUserTurnText()
    {
        for (int i = _chatLog.Messages.Count - 1; i >= 0; i--)
        {
            if (_chatLog.Messages[i].Role == ChatRole.User)
                return _chatLog.Messages[i].Text;
        }
        return "";
    }

    /// <summary>Minimal mutable counter shared across a turn's tool calls
    /// (locals of RunRequestAsync passed by reference into the async call
    /// chain). Kept trivially small on purpose.</summary>
    private sealed class ScopedInt
    {
        public int Value;
    }

    /// <summary>
    /// After each successful create_sketch this turn, remember its typed
    /// parameters under the reference the executor returned. The revolve
    /// advisor then decides axis feasibility from these facts alone — no NX
    /// round-trip. A failed or reference-less sketch adds nothing.
    /// </summary>
    private static void TrackSketchForRevolveAdvice(
        string toolName,
        CreateBlockExecutionResult execution,
        Dictionary<string, RevolveAxisAdvisor.SketchFacts> sketchesThisTurn)
    {
        if (toolName != CreateSketchTool.Name || execution.Status != ToolExecutionStatus.Success)
            return;
        string? sketchRef = ExtractDetailsString(execution.DetailsJson, "sketchRef");
        var parameters = ExtractDetailsSketchFacts(execution.DetailsJson);
        if (!string.IsNullOrWhiteSpace(sketchRef) && parameters != null)
            sketchesThisTurn[sketchRef] = parameters;
    }

    private static string? ExtractDetailsString(string? detailsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(propertyName, out var element) &&
                element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return element.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// Rebuilds the sketch facts from the executor's DetailsJson. The payload
    /// carries the profile geometry (profileType/width/height/diameter/
    /// centerU/centerV) plus the plane mode — written by ExecuteCreateSketch
    /// so the controller needs no NX access to reason about the sketch.
    /// </summary>
    private static RevolveAxisAdvisor.SketchFacts? ExtractDetailsSketchFacts(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("planeMode", out var planeEl) ||
                planeEl.ValueKind != System.Text.Json.JsonValueKind.String ||
                !root.TryGetProperty("profileType", out var profileEl) ||
                profileEl.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }
            if (!Enum.TryParse<SketchPlaneMode>(planeEl.GetString(), ignoreCase: true, out var plane) ||
                !Enum.TryParse<SketchProfileType>(profileEl.GetString(), ignoreCase: true, out var profile))
            {
                return null;
            }
            double ReadNumber(string name)
            {
                if (root.TryGetProperty(name, out var el) &&
                    el.ValueKind == System.Text.Json.JsonValueKind.Number &&
                    el.TryGetDouble(out var value) && double.IsFinite(value))
                {
                    return value;
                }
                return 0;
            }
            return new RevolveAxisAdvisor.SketchFacts(
                plane, profile,
                ReadNumber("width"), ReadNumber("height"), ReadNumber("diameter"),
                ReadNumber("centerU"), ReadNumber("centerV"));
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void AcknowledgeUnknownForTool(string toolName)
    {
        if (_ledger == null)
            return;
        foreach (var entry in _ledger.Snapshot())
        {
            if (entry.ToolName == toolName &&
                entry.State == ModelingExecutionState.Attempting &&
                !entry.Acknowledged)
            {
                _ledger.Acknowledge(entry.LedgerId);
            }
        }
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// One-line, user-facing summary of what the current turn's context
    /// capture actually observed (selection count and types; for a single
    /// planar face also its center and normal). Failures are stated as
    /// failures — never silently omitted.
    /// </summary>
    internal static string DescribeCapturedContext(NxContextSnapshot snapshot)
    {
        switch (snapshot.Status)
        {
            case "no_active_part":
                return "NX 上下文：当前没有活动工作部件。";
            case "error":
            {
                var reason = snapshot.Unavailable.Count > 0
                    ? snapshot.Unavailable[0]
                    : $"状态 {snapshot.Status}";
                return $"NX 上下文读取失败：{reason}";
            }
        }

        var kindText = DescribeSelectionKinds(snapshot.Selection);
        var head = snapshot.Selection.Count == 0
            ? "NX 上下文：当前没有选中对象。"
            : $"NX 上下文：已选择 {snapshot.Selection.Count} 个对象{kindText}。";

        // When exactly one face with a full geometry summary was captured,
        // surface its center/normal so the user can sanity-check the data the
        // model will see (e.g. before a face-relative cylinder request).
        var facesWithGeometry = snapshot.Selection
            .Where(s => s.Kind == "face" && s.Face is { } f &&
                f.CenterX is not null && f.CenterY is not null && f.CenterZ is not null &&
                f.NormalX is not null && f.NormalY is not null && f.NormalZ is not null)
            .ToList();
        if (facesWithGeometry.Count == 1 && facesWithGeometry[0].Face is { } face)
        {
            head += $"\n面中心 ({Fmt(face.CenterX)}, {Fmt(face.CenterY)}, {Fmt(face.CenterZ)})，" +
                    $"法向 ({Fmt(face.NormalX)}, {Fmt(face.NormalY)}, {Fmt(face.NormalZ)})";
        }

        if (snapshot.Unavailable.Count > 0)
            head += $"\n未获取：{string.Join("；", snapshot.Unavailable.Take(2))}";
        return head;
    }

    private static string Fmt(double? v) => v is { } d
        ? d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        : "未知";

    private static string DescribeSelectionKinds(IReadOnlyList<NxSelectionItem> selection)
    {
        if (selection.Count == 0)
            return "";
        var groups = selection
            .GroupBy(s => s.Kind switch
            {
                "face" => s.Subtype != null ? $"面（{s.Subtype}）" : "面",
                "body" => s.Body is { IsSheetBody: true } ? "片体" : "实体",
                "curve" => s.Subtype ?? "曲线",
                _ => s.Kind,
            })
            .Select(g => g.Count() == 1 ? $"1 个{g.Key}" : $"{g.Count()} 个{g.Key}");
        return "（" + string.Join("、", groups) + "）";

    }

    /// <summary>
    /// Serializes the execution outcome for the model. Contains no secrets and
    /// no machine paths beyond the work part identity the user already sees.
    /// Executor-provided DetailsJson (NXHost-managed references such as
    /// sketchRef/newBodyRef) is merged into the top-level payload object.
    /// </summary>
    internal static string BuildToolResultPayload(CreateBlockExecutionResult result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["status"] = result.Status.ToWireName(),
            ["message"] = result.Message,
            ["rolledBack"] = result.RolledBack,
        };
        if (result.Status == ToolExecutionStatus.Success)
        {
            payload["verifiedDimensionsMm"] = new Dictionary<string, double>
            {
                ["length"] = result.VerifiedLengthMm ?? 0,
                ["width"] = result.VerifiedWidthMm ?? 0,
                ["height"] = result.VerifiedHeightMm ?? 0,
            };
        }
        if (!string.IsNullOrWhiteSpace(result.DetailsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.DetailsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in doc.RootElement.EnumerateObject())
                    {
                        payload[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed details are never sent; the core fields above
                // remain the source of truth.
            }
        }
        return JsonSerializer.Serialize(payload);
    }

    private IReadOnlyList<OutboundMessage> BuildOutboundMessages()
    {
        // Only real conversation roles are sent; local Info/Error status entries never
        // reach the model. Count-capped so long chats cannot grow the request unbounded.
        var sendable = _chatLog.Messages
            .Where(m => m.Role is ChatRole.User or ChatRole.Assistant or ChatRole.System)
            .ToList();
        if (sendable.Count > _maxOutboundMessages)
            sendable = sendable.Skip(sendable.Count - _maxOutboundMessages).ToList();
        return sendable.Select(OutboundMessage.FromChatMessage).ToList();
    }
}
