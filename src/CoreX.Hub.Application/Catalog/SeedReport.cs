namespace CoreX.Hub.Application.Catalog;

/// <summary>Per-entity outcome of one configuration seed run.</summary>
public sealed record SeedCounts(string Entity, int Created, int Updated, int Unchanged)
{
    public int Total => Created + Updated + Unchanged;
}

/// <summary>
/// A persisted record that configuration no longer mentions. The seeder never deletes
/// (architecture section 21); it reports so that removal stays an explicit human action.
/// </summary>
public sealed record SeedOrphan(string Entity, string Identity);

public sealed record SeedReport(IReadOnlyList<SeedCounts> Counts, IReadOnlyList<SeedOrphan> Orphans)
{
    public int TotalCreated => Counts.Sum(c => c.Created);

    public int TotalUpdated => Counts.Sum(c => c.Updated);

    public int TotalUnchanged => Counts.Sum(c => c.Unchanged);

    /// <summary>True when the seed left the database byte-for-byte as it found it.</summary>
    public bool MadeNoChanges => TotalCreated == 0 && TotalUpdated == 0;
}
