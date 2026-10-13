using NXAI.Assistant.Core;
using NXOpen;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Reads the current NX session's active work part through in-process NXOpen.
/// Read-only: never creates, modifies, or saves parts.
/// Must be called on the NX UI thread (menu callback / form events qualify).
/// </summary>
public sealed class NxSessionReader : IActivePartReader
{
    public WorkPartSnapshot ReadActivePart()
    {
        try
        {
            var session = Session.GetSession();
            var work = session.Parts.Work;

            if (work is null)
            {
                return WorkPartSnapshot.None();
            }

            return new WorkPartSnapshot(
                HasActivePart: true,
                DisplayName: work.Name,
                FullPath: work.FullPath,
                Units: work.PartUnits.ToString(),
                StatusMessage: "已从当前 NX 会话读取活动工作部件。");
        }
        catch (Exception ex)
        {
            return WorkPartSnapshot.FromError(ex.Message);
        }
    }
}
