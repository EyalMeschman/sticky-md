namespace StickyMD.Core.Notes;

/// <summary>Injected so date-derived filenames are testable.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
