public interface ILocalUploadPaths
{
    string ChunkDir(Guid uploadId);
    string FinalFilePath(Guid uploadId, string fileName);
}

public sealed class LocalUploadPaths : ILocalUploadPaths
{
    private readonly string _root;

    public LocalUploadPaths()
    {
        _root = Path.Combine(Path.GetTempPath(), "uploads");
    }

    public string ChunkDir(Guid uploadId) => Path.Combine(_root, uploadId.ToString(), "chunks");

    public string FinalFilePath(Guid uploadId, string fileName) =>
        Path.Combine(_root, uploadId.ToString(), fileName);
}
