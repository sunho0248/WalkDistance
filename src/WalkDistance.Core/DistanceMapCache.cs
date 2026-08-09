namespace WalkDistance.Core;

public readonly record struct GridCell(int Col, int Row);
public sealed record CachedRoot(GridCell Cell, WorldPoint Contact, Segment? ExitSegment);

public sealed record DistanceMapCache(
    int Rows,
    int Cols,
    double[] Distances,
    GridCell?[] Predecessors,
    GridCell? FarthestCell,
    double MaxDistance,
    int UnreachableCellCount,
    List<CachedRoot> Roots,
    List<List<DistanceSource>> SourceGroups,
    int[] WinningGroupIndexes,
    WorldPoint? QueryPoint = null,
    double? QueryDistance = null,
    List<WorldPoint>? FarthestPath = null,
    List<WorldPoint>? QueryPath = null,
    BodyProfile? BodyProfile = null)
{
    public static DistanceMapCache Create(
        WalkabilityGrid grid,
        DistanceMapResult result,
        WorldPoint? queryPoint,
        double? queryDistance,
        IReadOnlyList<WorldPoint>? farthestPath,
        IReadOnlyList<WorldPoint>? queryPath,
        BodyProfile? bodyProfile = null) => new(
            grid.Rows,
            grid.Cols,
            Flatten(result.Distances),
            Flatten(result.Predecessor),
            result.FarthestCell is { } farthest ? new GridCell(farthest.Col, farthest.Row) : null,
            result.MaxDistance,
            result.UnreachableCellCount,
            result.Roots.Select(pair => new CachedRoot(
                new GridCell(pair.Key.Col, pair.Key.Row), pair.Value.Contact, pair.Value.ExitSegment)).ToList(),
            result.SourceGroups.Select(group => group.ToList()).ToList(),
            result.WinningGroupIndexes is { } winners ? Flatten(winners) : [],
            queryPoint,
            queryDistance,
            farthestPath?.ToList(),
            queryPath?.ToList(),
            bodyProfile);

    public bool IsCompatibleWith(WalkabilityGrid grid) =>
        BodyProfile is null && grid.ClearanceRadius == 0 && HasExpectedDimensions(grid);

    public bool IsCompatibleWith(WalkabilityGrid grid, BodyProfile bodyProfile) =>
        BodyProfile == bodyProfile && bodyProfile.IsValid &&
        grid.ClearanceRadius == bodyProfile.ClearanceRadius && HasExpectedDimensions(grid);

    public bool IsSane
    {
        get
        {
            if (Rows <= 0 || Cols <= 0 || Distances is null || Predecessors is null ||
                Roots is null || SourceGroups is null || WinningGroupIndexes is null)
            {
                return false;
            }

            try
            {
                int count = checked(Rows * Cols);
                return Distances.Length == count && Predecessors.Length == count &&
                       (WinningGroupIndexes.Length == 0 || WinningGroupIndexes.Length == count);
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }

    public CachedAnalysis Restore(WalkabilityGrid grid)
    {
        int count = checked(Rows * Cols);
        if (grid.Rows != Rows || grid.Cols != Cols || Distances.Length != count ||
            Predecessors.Length != count || (WinningGroupIndexes.Length != 0 && WinningGroupIndexes.Length != count))
            throw new InvalidDataException("저장된 거리 맵의 격자 크기가 프로젝트와 일치하지 않습니다.");

        var result = new DistanceMapResult(
            Expand(Distances),
            FarthestCell is { } farthest ? (farthest.Col, farthest.Row) : null,
            MaxDistance,
            UnreachableCellCount,
            Expand(Predecessors))
        {
            Roots = Roots.ToDictionary(root => (root.Cell.Col, root.Cell.Row),
                root => new DistanceRoot(root.Contact, root.ExitSegment)),
            SourceGroups = SourceGroups.Select(group => (IReadOnlyList<DistanceSource>)group).ToList(),
            WinningGroupIndexes = WinningGroupIndexes.Length == 0 ? null : Expand(WinningGroupIndexes),
        };
        return new CachedAnalysis(result, QueryPoint, QueryDistance, FarthestPath, QueryPath);
    }

    private static T[] Flatten<T>(T[,] values)
    {
        var flat = new T[values.Length];
        Buffer.BlockCopy(values, 0, flat, 0, Buffer.ByteLength(values));
        return flat;
    }

    private static GridCell?[] Flatten((int Col, int Row)?[,] values) =>
        values.Cast<(int Col, int Row)?>()
            .Select(value => value is { } cell ? (GridCell?)new GridCell(cell.Col, cell.Row) : null).ToArray();

    private T[,] Expand<T>(IReadOnlyList<T> values)
    {
        var result = new T[Rows, Cols];
        for (int i = 0; i < values.Count; i++) result[i / Cols, i % Cols] = values[i];
        return result;
    }

    private (int Col, int Row)?[,] Expand(IReadOnlyList<GridCell?> values)
    {
        var result = new (int Col, int Row)?[Rows, Cols];
        for (int i = 0; i < values.Count; i++)
            result[i / Cols, i % Cols] = values[i] is { } cell ? (cell.Col, cell.Row) : null;
        return result;
    }

    private bool HasExpectedDimensions(WalkabilityGrid grid) =>
        IsSane && grid.Rows == Rows && grid.Cols == Cols;
}

public sealed record CachedAnalysis(
    DistanceMapResult Result,
    WorldPoint? QueryPoint,
    double? QueryDistance,
    IReadOnlyList<WorldPoint>? FarthestPath,
    IReadOnlyList<WorldPoint>? QueryPath);
