namespace NXAI.Assistant.Core;

/// <summary>
/// Contract for reading the current NX active work part.
/// Implementations must be safe to call from the NX UI thread.
/// Read-only: implementations must not create, modify, or save parts.
/// </summary>
public interface IActivePartReader
{
    WorkPartSnapshot ReadActivePart();
}
