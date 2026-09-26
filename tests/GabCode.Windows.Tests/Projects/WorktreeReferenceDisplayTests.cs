using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class WorktreeReferenceDisplayTests
{
    [Fact]
    public void Markdown_file_name_preserves_extension_with_middle_ellipsis()
    {
        var text = WorktreeReferenceDisplay.MarkdownFileName("C:/references/this-is-a-very-long-worktree-reference-document-name.md", 24);

        Assert.Equal("this-is-a-…ument-name.md", text);
    }

    [Fact]
    public void Markdown_file_name_keeps_short_names_intact()
    {
        Assert.Equal("notes.md", WorktreeReferenceDisplay.MarkdownFileName("C:/references/notes.md"));
    }
}
