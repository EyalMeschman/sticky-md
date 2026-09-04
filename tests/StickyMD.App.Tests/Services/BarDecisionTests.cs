using Shouldly;
using StickyMD.App.Services;

namespace StickyMD.App.Tests.Services;

public class BarDecisionTests
{
    [Fact]
    public void A_clean_buffer_reloads_without_asking()
    {
        // Spec: external change, clean buffer -> reload and re-render, scroll
        // preserved. There is nothing to lose, so asking would be noise.
        ExternalChangePolicy.Decide(
            bufferIsDirty: false, bufferHash: "AAA", diskHash: "BBB")
            .ShouldBe(ExternalChangeAction.Reload);
    }

    [Fact]
    public void A_dirty_buffer_asks_rather_than_choosing()
    {
        // Spec: never auto-clobber either side.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: "AAA", diskHash: "BBB")
            .ShouldBe(ExternalChangeAction.Ask);
    }

    [Fact]
    public void A_dirty_buffer_whose_content_already_matches_disk_just_reloads()
    {
        // Somebody else typed the same characters, or our own save arrived by
        // a path the ledger missed. There is no conflict to resolve, and a bar
        // here would be a question with one answer.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: "SAME", diskHash: "SAME")
            .ShouldBe(ExternalChangeAction.Reload);
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        // NoteFile.Sha256 returns uppercase hex; a hash from JSON may not be.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: "abc123", diskHash: "ABC123")
            .ShouldBe(ExternalChangeAction.Reload);
    }

    [Fact]
    public void A_dirty_buffer_with_an_unknown_hash_asks()
    {
        // Not knowing is not the same as matching. Asking risks a click;
        // guessing risks the text.
        ExternalChangePolicy.Decide(
            bufferIsDirty: true, bufferHash: null, diskHash: "BBB")
            .ShouldBe(ExternalChangeAction.Ask);
    }
}
