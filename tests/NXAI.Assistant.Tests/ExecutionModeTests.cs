using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the execution-mode domain: defaults, persistence
/// round-trip, malformed-file handling, the autonomous allowlist and the
/// pure authorization policy. All fakes — no NX, no WinForms, no %APPDATA%
/// access (the store always gets an isolated temp path).
/// </summary>
public class ExecutionModeTests : IDisposable
{
    private readonly string _tempDir;

    public ExecutionModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nxai-mode-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string FilePath => Path.Combine(_tempDir, "execution-mode.json");

    // ------------------------------------------------------------ defaults

    [Fact]
    public void Manager_WithoutStore_DefaultsToConfirmMode()
    {
        var manager = new ExecutionModeManager();
        Assert.Equal(ExecutionMode.Confirm, manager.CurrentMode);
    }

    [Fact]
    public void Manager_MissingFile_DefaultsToConfirmMode()
    {
        var manager = new ExecutionModeManager(new JsonFileExecutionModeStore(FilePath));
        Assert.Equal(ExecutionMode.Confirm, manager.CurrentMode);
    }

    // ------------------------------------------------------------ store

    [Fact]
    public void Store_RoundTripsBothModes()
    {
        var store = new JsonFileExecutionModeStore(FilePath);
        Assert.Null(store.Load());

        store.Save(ExecutionMode.Autonomous);
        Assert.Equal(ExecutionMode.Autonomous, store.Load());

        store.Save(ExecutionMode.Confirm);
        Assert.Equal(ExecutionMode.Confirm, store.Load());
    }

    [Fact]
    public void Store_MalformedJson_LoadsAsNull()
    {
        File.WriteAllText(FilePath, "{ not valid json");
        var store = new JsonFileExecutionModeStore(FilePath);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Store_UnknownModeValue_LoadsAsNull()
    {
        File.WriteAllText(FilePath, """{"executionMode":"delete_everything"}""");
        var store = new JsonFileExecutionModeStore(FilePath);
        Assert.Null(store.Load());
    }

    [Fact]
    public void Store_NonObjectPayload_LoadsAsNull()
    {
        File.WriteAllText(FilePath, "[1,2,3]");
        var store = new JsonFileExecutionModeStore(FilePath);
        Assert.Null(store.Load());
    }

    // ------------------------------------------------------------ manager

    [Fact]
    public void Manager_RestoresStoredAutonomousModeOnStartup()
    {
        new JsonFileExecutionModeStore(FilePath).Save(ExecutionMode.Autonomous);
        var manager = new ExecutionModeManager(new JsonFileExecutionModeStore(FilePath));
        Assert.Equal(ExecutionMode.Autonomous, manager.CurrentMode);
    }

    [Fact]
    public void Manager_SetMode_PersistsAndRaisesEvent()
    {
        var store = new JsonFileExecutionModeStore(FilePath);
        var manager = new ExecutionModeManager(store);
        int events = 0;
        manager.ModeChanged += (_, _) => events++;

        manager.SetMode(ExecutionMode.Autonomous);

        Assert.Equal(ExecutionMode.Autonomous, manager.CurrentMode);
        Assert.Equal(ExecutionMode.Autonomous, store.Load());
        Assert.Equal(1, events);
    }

    [Fact]
    public void Manager_SetSameMode_DoesNotWriteOrRaise()
    {
        var store = new JsonFileExecutionModeStore(FilePath);
        var manager = new ExecutionModeManager(store);
        int events = 0;
        manager.ModeChanged += (_, _) => events++;

        manager.SetMode(ExecutionMode.Confirm);

        Assert.Equal(ExecutionMode.Confirm, manager.CurrentMode);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(0, events);
    }

    [Fact]
    public void Manager_PersistFailure_KeepsPreviousModeInMemory()
    {
        // A directory in place of the target file makes any write fail.
        string blockedPath = Path.Combine(_tempDir, "blocked", "execution-mode.json");
        Directory.CreateDirectory(blockedPath);
        var manager = new ExecutionModeManager(new JsonFileExecutionModeStore(blockedPath));

        Assert.ThrowsAny<Exception>(() => manager.SetMode(ExecutionMode.Autonomous));

        Assert.Equal(ExecutionMode.Confirm, manager.CurrentMode);
    }

    // ------------------------------------------------------------ allowlist

    [Fact]
    public void Allowlist_ContainsExactlyTheEightRegisteredModelingTools()
    {
        Assert.Equal(8, AutonomousToolAllowlist.Tools.Count);
        Assert.Contains(CreateBlockTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(CreateCylinderTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(CreateConeTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(CreateSphereTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(CreateSketchTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(CreateExtrudeTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(CreateRevolveTool.Name, AutonomousToolAllowlist.Tools);
        Assert.Contains(BooleanOperationTool.Name, AutonomousToolAllowlist.Tools);
    }

    [Theory]
    [InlineData("delete_body")]
    [InlineData("save_part")]
    [InlineData("create_block_evil")]
    [InlineData("CREATE_BLOCK")]
    [InlineData("")]
    [InlineData(null)]
    public void Allowlist_RejectsNonAllowlistedNames(string? toolName)
    {
        Assert.False(AutonomousToolAllowlist.Contains(toolName!));
    }

    // ------------------------------------------------------------ policy

    [Fact]
    public void Policy_ConfirmMode_RequiresConfirmationForEveryTool()
    {
        var policy = new ExecutionModePolicy();
        foreach (var tool in AutonomousToolAllowlist.Tools)
            Assert.True(policy.RequiresConfirmation(ExecutionMode.Confirm, tool));
    }

    [Fact]
    public void Policy_AutonomousMode_AllowsOnlyAllowlistedTools()
    {
        var policy = new ExecutionModePolicy();
        foreach (var tool in AutonomousToolAllowlist.Tools)
            Assert.False(policy.RequiresConfirmation(ExecutionMode.Autonomous, tool));
    }

    [Theory]
    [InlineData("delete_body")]
    [InlineData("save_part")]
    [InlineData("unknown_tool")]
    public void Policy_AutonomousMode_NeverAutoExecutesUnknownOrUnauthorizedTools(string toolName)
    {
        var policy = new ExecutionModePolicy();
        Assert.True(policy.RequiresConfirmation(ExecutionMode.Autonomous, toolName));
    }

    // ------------------------------------------------------------ naming

    [Fact]
    public void ModeNames_AreStableForFileAndDisplay()
    {
        Assert.Equal("confirm", ExecutionMode.Confirm.ToFileName());
        Assert.Equal("autonomous", ExecutionMode.Autonomous.ToFileName());
        Assert.Equal(ExecutionMode.Confirm, ExecutionModeExtensions.FromFileName("confirm"));
        Assert.Equal(ExecutionMode.Autonomous, ExecutionModeExtensions.FromFileName("autonomous"));
        Assert.Null(ExecutionModeExtensions.FromFileName("Confirm"));
        Assert.Null(ExecutionModeExtensions.FromFileName(null));
        Assert.Equal("确认模式", ExecutionMode.Confirm.DisplayName());
        Assert.Equal("自主模式", ExecutionMode.Autonomous.DisplayName());
    }
}
