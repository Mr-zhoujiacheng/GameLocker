using System.IO.Compression;

namespace GameLocker.Services;

internal static class SaveRestoreTransaction
{
    private sealed record Operation(string Destination, string Replacement, string? Original);

    public static int Commit(IReadOnlyList<(ZipArchiveEntry Entry, string Destination)> files)
    {
        var operations = new List<Operation>(files.Count);
        var createdDirectories = new Stack<string>();
        var preservedOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var committed = 0;
        try
        {
            // Prepare every replacement on the target volume before changing live files.
            foreach (var (entry, destination) in files)
            {
                var directory = Path.GetDirectoryName(destination)!;
                CreateDirectory(directory, createdDirectories);
                var temporary = Path.Combine(directory, $".gamelocker-restore-{Guid.NewGuid():N}");
                var operation = new Operation(destination, temporary + ".tmp",
                    File.Exists(destination) ? temporary + ".bak" : null);
                operations.Add(operation);
                entry.ExtractToFile(operation.Replacement);
                if (operation.Original is not null) File.Copy(destination, operation.Original);
            }

            for (; committed < operations.Count; committed++)
            {
                var operation = operations[committed];
                File.Move(operation.Replacement, operation.Destination, overwrite: operation.Original is not null);
            }
            return committed;
        }
        catch (Exception restoreError)
        {
            var rollbackErrors = new List<string>();
            for (var i = committed - 1; i >= 0; i--)
            {
                var operation = operations[i];
                try
                {
                    if (operation.Original is null) File.Delete(operation.Destination);
                    else File.Move(operation.Original, operation.Destination, overwrite: true);
                }
                catch (Exception ex)
                {
                    if (operation.Original is not null) preservedOriginals.Add(operation.Original);
                    rollbackErrors.Add($"{operation.Destination}: {ex.Message}");
                }
            }
            if (rollbackErrors.Count != 0)
                throw new IOException($"恢复失败，自动回滚未完成：{string.Join("；", rollbackErrors)}", restoreError);
            throw;
        }
        finally
        {
            foreach (var operation in operations)
            {
                TryDelete(operation.Replacement);
                if (operation.Original is not null && !preservedOriginals.Contains(operation.Original))
                    TryDelete(operation.Original);
            }
            if (committed < files.Count)
            {
                foreach (var directory in createdDirectories)
                {
                    try { Directory.Delete(directory, recursive: false); } catch { }
                }
            }
        }
    }

    private static void CreateDirectory(string directory, Stack<string> createdDirectories)
    {
        if (Directory.Exists(directory)) return;
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null) CreateDirectory(parent, createdDirectories);
        Directory.CreateDirectory(directory);
        createdDirectories.Push(directory);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
