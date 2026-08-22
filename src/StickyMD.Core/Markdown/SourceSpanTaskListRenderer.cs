using Markdig.Extensions.TaskLists;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace StickyMD.Core.Markdown;

/// <summary>
/// Emits each task checkbox carrying the source span of its "[ ]" token, so a
/// click can be applied to that exact range instead of an ordinal position.
/// Markdig's SourceSpan.End is INCLUSIVE.
/// </summary>
internal sealed class SourceSpanTaskListRenderer : HtmlObjectRenderer<TaskList>
{
    protected override void Write(HtmlRenderer renderer, TaskList task)
    {
        renderer.Write("<input type=\"checkbox\" data-span-start=\"");
        renderer.Write(task.Span.Start.ToString());
        renderer.Write("\" data-span-end=\"");
        renderer.Write(task.Span.End.ToString());
        renderer.Write('"');

        if (task.Checked) renderer.Write(" checked=\"checked\"");

        renderer.Write(" />");
    }
}
