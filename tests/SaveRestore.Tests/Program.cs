using System.IO.Compression;
using GameLocker.Services;

using var temp = new TestDirectory();
using var zip = new ZipArchive(new MemoryStream(), ZipArchiveMode.Update);
var existing = Path.Combine(temp.Path, "old.sav");
var added = Path.Combine(temp.Path, "new-folder", "new.sav");
var secondExisting = Path.Combine(temp.Path, "second.sav");
File.WriteAllText(existing, "original");
File.WriteAllText(secondExisting, "original second");
var saveA = MakeEntry("a", "replacement");
var saveB = MakeEntry("b", "new content");
var saveC = MakeEntry("c", "replacement second");

// A move with overwrite fails on Windows while the destination is open without delete sharing.
if (OperatingSystem.IsWindows())
{
    using (File.Open(secondExisting, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        ExpectIo(() => SaveRestoreTransaction.Commit([(saveA, existing), (saveB, added), (saveC, secondExisting)]));
        Check(File.ReadAllText(existing) == "original", "First file rolled back");
        Check(!Directory.Exists(Path.GetDirectoryName(added)), "New file and its empty directory rolled back");
        Check(File.ReadAllText(secondExisting) == "original second", "Locked file unchanged");
        Check(!ArtifactsExist(), "Transaction artifacts cleaned up");
    }
    Console.WriteLine("PASS: Windows sharing violation rolls back earlier replacements and creations");
}

Check(SaveRestoreTransaction.Commit([(saveA, existing), (saveB, added), (saveC, secondExisting)]) == 3,
    "All replacements committed");
Check(File.ReadAllText(existing) == "replacement", "Existing file replaced");
Check(File.ReadAllText(added) == "new content", "New file created");
Check(File.ReadAllText(secondExisting) == "replacement second", "Second file replaced");
Check(!ArtifactsExist(), "No temporary files remain on success");
Console.WriteLine("PASS: successful transaction replaces and creates files");

var invalid = Path.Combine(existing, "cannot-be-a-child-of-file.sav");
ExpectIo(() => SaveRestoreTransaction.Commit([(saveC, secondExisting), (saveB, invalid)]));
Check(File.ReadAllText(secondExisting) == "replacement second", "Preparation failure leaves live files unchanged");
Check(!ArtifactsExist(), "No temporary files remain after preparation failure");
Console.WriteLine("PASS: preparation failure does not modify live files");

ZipArchiveEntry MakeEntry(string name, string value)
{
    var entry = zip.CreateEntry(name);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(value);
    return entry;
}

bool ArtifactsExist() => Directory.EnumerateFiles(temp.Path, ".gamelocker-restore-*", SearchOption.AllDirectories).Any();

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void ExpectIo(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
    throw new Exception("Expected a file-system exception");
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("GameLocker-RestoreTest-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
