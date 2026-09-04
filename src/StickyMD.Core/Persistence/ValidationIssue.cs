namespace StickyMD.Core.Persistence;

/// <summary>
/// One correction made while loading persisted state.
/// </summary>
/// <param name="Scope">
/// What was being loaded -- a note path, or "settings.json". Enough for the
/// user to know which of their notes moved.
/// </param>
/// <param name="Field">The JSON property name, so it can be hand-corrected.</param>
/// <param name="Detail">What was found and what was used instead.</param>
/// <remarks>
/// This type exists because clamping silently would break the governing rule.
/// "Never die silently" covers quiet recovery too: a note that moves or
/// changes colour on its own, with nothing anywhere saying why, is a bug
/// report nobody can act on.
/// </remarks>
public sealed record ValidationIssue(string Scope, string Field, string Detail)
{
    public override string ToString() => $"{Scope}: {Field} -- {Detail}";
}
