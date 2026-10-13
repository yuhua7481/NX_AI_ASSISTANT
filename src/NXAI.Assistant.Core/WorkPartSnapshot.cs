namespace NXAI.Assistant.Core;

/// <summary>
/// Read-only description of the NX active work part. Shared layer model:
/// carries no NXOpen types so Core/UI/tests never reference NXOpen assemblies.
/// Fields that could not be read are null; <see cref="StatusMessage"/> explains why.
/// </summary>
public sealed record WorkPartSnapshot(
    bool HasActivePart,
    string? DisplayName,
    string? FullPath,
    string? Units,
    string? StatusMessage)
{
    public static WorkPartSnapshot None() =>
        new(false, null, null, null, "当前 NX 会话中没有打开的活动工作部件。");

    public static WorkPartSnapshot FromError(string message) =>
        new(false, null, null, null, $"读取活动部件失败：{message}");
}
