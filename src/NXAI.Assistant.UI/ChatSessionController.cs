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

    /// <summary>Modeling executions allowed per user turn (the serial chain
    /// length). Round gating guarantees at most this many executor calls.</summary>
    public const int MaxToolExecutionsPerTurn = MaxModelResponsesPerTurn - 1;

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

    /// <summary>Snapshot captured when the current turn started (null when no
    /// provider is wired or capture failed). Consumed by face-relative tools.</summary>
    public NxContextSnapshot? CurrentTurnContext { get; private set; }

    private CancellationTokenSource? _cts;
    private bool _inFlight;
    private bool _detached;

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
        ExecutionModeManager? modeManager = null)
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
        _toolRegistry = toolExecutor != null
            ? CreateBlockTool.CreateRegistry()
            : new ToolRegistry();
    }

    public bool IsRequestInFlight => _inFlight;

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

                // A tool call arrived. There must be room to feed a structured
                // result back; otherwise refuse instead of executing blind.
                if (round == MaxModelResponsesPerTurn)
                {
                    _chatLog.Add(ChatRole.Info,
                        $"模型在最终回复阶段请求工具调用（{result.ToolCalls[0].FunctionName}），" +
                        "已拒绝：本轮建模执行已结束。");
                    errorText = "工具调用回合数已达上限，本次不再执行建模操作。请重新发送请求。";
                    break;
                }

                var call = result.ToolCalls[0];
                protocol.Add(new OutboundMessage(OutboundMessage.RoleAssistant, null,
                    new[] { new OutboundToolCall(call.Id, call.FunctionName, call.ArgumentsJson) }));

                var execution = await ProcessToolCallAsync(call, cancellationToken);
                protocol.Add(new OutboundMessage(OutboundMessage.RoleTool,
                    BuildToolResultPayload(execution), ToolCallId: call.Id));
                _chatLog.Add(ChatRole.Info, $"建模执行结果：{execution.Message}");
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

        if (canceled)
        {
            _chatLog.Add(ChatRole.Info, "已取消当前请求。");
        }
        else if (errorText != null)
        {
            _chatLog.Add(ChatRole.Error, errorText);
        }
        else if (!string.IsNullOrWhiteSpace(finalText))
        {
            _chatLog.Add(ChatRole.Assistant, finalText);
        }
        else
        {
            _chatLog.Add(ChatRole.Error, "模型返回了空白回复。");
        }

        RequestCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Validates the requested tool call and, only after user confirmation,
    /// executes it once. Every rejection path returns a structured result the
    /// model will receive; the executor is never called without confirmation,
    /// and at most once per user turn by construction (round gating above).
    /// </summary>
    private async Task<CreateBlockExecutionResult> ProcessToolCallAsync(
        ParsedToolCall call, CancellationToken cancellationToken)
    {
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
            call.FunctionName != BooleanOperationTool.Name)
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

            string axisText = revolveParameters.AxisMode switch
            {
                RevolveAxisMode.SelectedAxis => "用户选中的直线/基准轴",
                RevolveAxisMode.WcsX => "绝对坐标 X 轴",
                RevolveAxisMode.WcsY => "绝对坐标 Y 轴",
                _ => "绝对坐标 Z 轴",
            };
            requestDescription =
                $"旋转体：草图 {revolveParameters.SketchRef}，轴 {axisText}，{FormatNumber(revolveParameters.AngleDeg)} 度。";
            request = ToolConfirmationRequest.ForRevolve(call.Id, call.FunctionName, revolveParameters);
        }
        else
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

        // Authorization decision at the backend dispatch stage — read the
        // effective mode here, never from the UI. Confirm mode (and any
        // tool not on the autonomous allowlist) always takes the existing
        // confirmation path.
        var effectiveMode = _modeManager?.CurrentMode ?? ExecutionMode.Confirm;
        if (!_modePolicy.RequiresConfirmation(effectiveMode, call.FunctionName))
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

        return ExecuteConfirmedTool(request, decision.ExpectedWorkPartFullPath);
    }

    private CreateBlockExecutionResult ExecuteConfirmedTool(
        ToolConfirmationRequest request, string? expectedWorkPartFullPath) =>
        request.ParametersKind switch
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
            _ => _toolExecutor!.ExecuteBooleanOperation(
                request.BooleanParameters!, expectedWorkPartFullPath),
        };

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
