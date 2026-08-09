using GabCode.Windows.Projects;

namespace GabCode.Windows.Tests.Projects;

public sealed class GitDiscoveryProgressTests
{
    [Fact]
    public void Progress_snapshot_exposes_phase_and_counts()
    {
        var progress = new GitDiscoveryProgress("Searching for Git repositories", 12, 1);
        Assert.Equal("Searching for Git repositories", progress.Phase);
        Assert.Equal(12, progress.FoldersScanned);
        Assert.Equal(1, progress.RepositoriesFound);
    }
}
