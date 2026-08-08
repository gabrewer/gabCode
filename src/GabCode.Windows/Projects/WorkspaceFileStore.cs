using System.IO;
using System.Text;

namespace GabCode.Windows.Projects;

internal sealed class WorkspaceFileStore
{
    private readonly Func<string, Stream> createTemporaryStream;

    internal WorkspaceFileStore()
        : this(path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
    {
    }

    internal WorkspaceFileStore(Func<string, Stream> createTemporaryStream)
    {
        this.createTemporaryStream = createTemporaryStream ?? throw new ArgumentNullException(nameof(createTemporaryStream));
    }

    internal async Task SaveNewAsync(string workspacePath, WorkspaceDocument workspace, string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var fullPath = Path.GetFullPath(workspacePath);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(fullPath))
        {
            throw new IOException($"The workspace file already exists: {fullPath}");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = createTemporaryStream(temporaryPath))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false))
            {
                await writer.WriteAsync(workspace.ToJson(fullPath, folderPath).AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
