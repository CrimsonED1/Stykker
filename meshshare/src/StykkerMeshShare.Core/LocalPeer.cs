namespace StykkerMeshShare.Core;

// Eine Gegenstelle im selben Prozess: für Tests, und um den Abgleich ohne Netz zu prüfen.
public sealed class LocalPeer(MeshNode node) : IPeerSource
{
    public Task<ChangePage> GetChangesAsync(long since, CancellationToken ct) => Task.FromResult(node.GetChanges(since));

    public Task<Stream?> OpenFileAsync(string path, string hash, CancellationToken ct) => Task.FromResult(node.OpenFile(path, hash));
}
