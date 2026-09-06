namespace Wildpinkler.App.Models;

public enum ModListGrade
{
    Reproducible,
    Guided,
    Unavailable
}

public sealed record ModListCatalogEntry(string Key, string FilePath, ModListManifest Manifest, ModListGrade Grade)
{
    public string RowSubtitle => $"Revision {Manifest.Revision} · {Grade}";
}
