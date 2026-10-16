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
    /// Model responses allowed per user turn: response 1 may request a tool
    /// call (which is confirmed and executed at most once), response 2 is the
    /// final text reply and may not trigger execution.
    /// </summary>
    public const int MaxModelResponsesPerTurn = 2;

    private readonly ModelService _modelService;
    private readonly ChatLog _chatLog;
    private readonly Func<(string apiBaseUrl, string apiKey, string modelName)?> _configLoader;
    private readonly ChatRequestDelegate _requestSender;
    private readonly int _maxOutboundMessages;
    private readonly INxToolExecutor? _toolExecutor;
    private readonly ToolConfirmationDelegate _confirmationHandler;
    private readonly ToolRegistry _toolRegistry;

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
        ToolConfirmationDelegate? confirmationHandler = null)
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
        _confirmationHandler = confirmationHandler ?? ((_, _) =>
            Task.FromResult<ToolConfirmationDecision?>(null));
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
        string? finalText = null;
        string? errorText = null;
        var canceled = false;

        try
        {
            for (int round = 1; round <= MaxModelResponsesPerTurn; round++)
            {
                // Tools are advertised only in round 1: the final round exists to
                // let the model phrase the result, not to trigger more modeling.
                IReadOnlyList<ToolDefinition>? tools =
                    round == 1 && ToolsEnabled ? _toolRegistry.All : null;

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
                $"工具 {call.FunctionName} 未注册，本次请求未执行。当前可用工具：create_block。");
        }

        if (call.FunctionName != CreateBlockTool.Name &&
            call.FunctionName != CreateCylinderTool.Name &&
            call.FunctionName != CreateConeTool.Name &&
            call.FunctionName != CreateSphereTool.Name)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.UnknownTool,
                $"工具 {call.FunctionName} 没有对应的执行器，本次请求未执行。");
        }

        ToolConfirmationRequest request;
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

            _chatLog.Add(ChatRole.Info,
                $"模型请求创建长方体：长 {FormatNumber(blockParameters.Length)} × " +
                $"宽 {FormatNumber(blockParameters.Width)} × 高 {FormatNumber(blockParameters.Height)} 毫米" +
                (blockParameters.OriginX == 0 && blockParameters.OriginY == 0 && blockParameters.OriginZ == 0
                    ? "，原点 (0, 0, 0)。"
                    : $"，原点 ({FormatNumber(blockParameters.OriginX)}, {FormatNumber(blockParameters.OriginY)}, {FormatNumber(blockParameters.OriginZ)})。") +
                "等待你的确认。");
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

            _chatLog.Add(ChatRole.Info,
                $"模型请求创建圆柱体：直径 {FormatNumber(cylinderParameters.Diameter)} × " +
                $"高 {FormatNumber(cylinderParameters.Height)} 毫米，轴向 +Z" +
                (cylinderParameters.OriginX == 0 && cylinderParameters.OriginY == 0 && cylinderParameters.OriginZ == 0
                    ? "，底面圆心 (0, 0, 0)。"
                    : $"，底面圆心 ({FormatNumber(cylinderParameters.OriginX)}, {FormatNumber(cylinderParameters.OriginY)}, {FormatNumber(cylinderParameters.OriginZ)})。") +
                "等待你的确认。");
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
            _chatLog.Add(ChatRole.Info,
                $"模型请求创建{coneShape}，轴向 +Z" +
                (coneParameters.OriginX == 0 && coneParameters.OriginY == 0 && coneParameters.OriginZ == 0
                    ? "，底面圆心 (0, 0, 0)。"
                    : $"，底面圆心 ({FormatNumber(coneParameters.OriginX)}, {FormatNumber(coneParameters.OriginY)}, {FormatNumber(coneParameters.OriginZ)})。") +
                "等待你的确认。");
            request = ToolConfirmationRequest.ForCone(call.Id, call.FunctionName, coneParameters);
        }
        else
        {
            var sphereParameters = new CreateSphereRequestParser()
                .ParseParameters(call.ArgumentsJson, out string? parseError);
            if (sphereParameters == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.InvalidParameters,
                    $"参数校验未通过，未创建任何几何。{parseError}");
            }

            _chatLog.Add(ChatRole.Info,
                $"模型请求创建球体：直径 {FormatNumber(sphereParameters.Diameter)} 毫米" +
                (sphereParameters.CenterX == 0 && sphereParameters.CenterY == 0 && sphereParameters.CenterZ == 0
                    ? "，球心 (0, 0, 0)。"
                    : $"，球心 ({FormatNumber(sphereParameters.CenterX)}, {sphereParameters.CenterY:F3}, {sphereParameters.CenterZ:F3})。") +
                "等待你的确认。");
            request = ToolConfirmationRequest.ForSphere(call.Id, call.FunctionName, sphereParameters);
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

        return request.ParametersKind switch
        {
            ToolParametersKind.Block => _toolExecutor!.ExecuteCreateBlock(
                request.BlockParameters!, decision.ExpectedWorkPartFullPath),
            ToolParametersKind.Cylinder => _toolExecutor!.ExecuteCreateCylinder(
                request.CylinderParameters!, decision.ExpectedWorkPartFullPath),
            ToolParametersKind.Cone => _toolExecutor!.ExecuteCreateCone(
                request.ConeParameters!, decision.ExpectedWorkPartFullPath),
            _ => _toolExecutor!.ExecuteCreateSphere(
                request.SphereParameters!, decision.ExpectedWorkPartFullPath),
        };
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Serializes the execution outcome for the model. Contains no secrets and
    /// no machine paths beyond the work part identity the user already sees.
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
