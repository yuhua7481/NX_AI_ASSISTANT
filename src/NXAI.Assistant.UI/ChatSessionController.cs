using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>Performs one chat-completions request. Tests replace it with a fake.</summary>
public delegate Task<ModelChatResult> ChatRequestDelegate(
    IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);

/// <summary>
/// Coordinates sending between the chat window and <see cref="ModelService"/>:
/// validates input, appends the user message to the chat log, runs the request
/// asynchronously and writes the reply/error back into the log.
/// Lives on the chat form's UI thread: StartSend/Cancel/Detach are called there and
/// the request continuation is posted back to the captured WinForms
/// SynchronizationContext, so log writes and events never touch controls from a worker thread.
/// </summary>
public sealed class ChatSessionController : IDisposable
{
    /// <summary>Upper bound of User/Assistant/System messages sent to the model (newest kept).</summary>
    public const int DefaultMaxOutboundMessages = 20;

    private readonly ModelService _modelService;
    private readonly ChatLog _chatLog;
    private readonly Func<(string apiBaseUrl, string apiKey, string modelName)?> _configLoader;
    private readonly ChatRequestDelegate _requestSender;
    private readonly int _maxOutboundMessages;

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
        int maxOutboundMessages = DefaultMaxOutboundMessages)
    {
        _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));
        _chatLog = chatLog ?? throw new ArgumentNullException(nameof(chatLog));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _requestSender = requestSender ?? modelService.GetChatCompletionAsync;
        _maxOutboundMessages = maxOutboundMessages <= 0 ? DefaultMaxOutboundMessages : maxOutboundMessages;
    }

    public bool IsRequestInFlight => _inFlight;

    /// <summary>True when the currently loaded configuration can serve a request.</summary>
    public bool IsConfigurationReady => _modelService.IsConfigured;

    /// <summary>Model name of the last successfully loaded configuration, null when not configured.</summary>
    public string? CurrentModelName { get; private set; }

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
        _ = RunRequestAsync(cts.Token);
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
        ModelChatResult? result = null;
        var canceled = false;
        try
        {
            result = await _requestSender(BuildOutboundMessages(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }
        catch (OperationCanceledException ex)
        {
            result = ModelChatResult.Fail(ModelChatError.NetworkError, $"请求超时：{ex.Message}");
        }
        catch (Exception ex)
        {
            result = ModelChatResult.Fail(ModelChatError.NetworkError, $"请求发生意外错误：{ex.Message}");
        }

        _inFlight = false;
        _cts = null;
        if (_detached)
            return;

        if (canceled)
        {
            _chatLog.Add(ChatRole.Info, "已取消当前请求。");
        }
        else if (result!.Success)
        {
            // ModelService never succeeds with a blank reply, but keep ChatLog.Add
            // unconditionally safe: it throws on blank text.
            if (string.IsNullOrWhiteSpace(result.Reply))
                _chatLog.Add(ChatRole.Error, "模型返回了空白回复。");
            else
                _chatLog.Add(ChatRole.Assistant, result.Reply);
        }
        else
        {
            _chatLog.Add(ChatRole.Error, result.ErrorMessage);
        }

        RequestCompleted?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<ChatMessage> BuildOutboundMessages()
    {
        // Only real conversation roles are sent; local Info/Error status entries never
        // reach the model. Count-capped so long chats cannot grow the request unbounded.
        var sendable = _chatLog.Messages
            .Where(m => m.Role is ChatRole.User or ChatRole.Assistant or ChatRole.System)
            .ToList();
        if (sendable.Count <= _maxOutboundMessages)
            return sendable;
        return sendable.Skip(sendable.Count - _maxOutboundMessages).ToList();
    }
}
