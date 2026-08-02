namespace WalkDistance.Core;

public static class DistanceContourAssembler
{
    public static IReadOnlyList<IReadOnlyList<DistanceContour>> Assemble(
        IReadOnlyList<DistanceContour> contours,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(contours);
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        foreach (var contour in contours)
        {
            if (!double.IsFinite(contour.Level) ||
                !IsFinite(contour.Start) || !IsFinite(contour.End))
                throw new ArgumentException("Contour values must be finite.", nameof(contours));
        }

        var unionFind = new UnionFind(contours.Count);
        var endpointsByCell = new Dictionary<Cell, List<Endpoint>>();
        for (int i = 0; i < contours.Count; i++)
        {
            AddEndpoint(contours[i], contours[i].Start, i, tolerance, endpointsByCell, unionFind);
            AddEndpoint(contours[i], contours[i].End, i, tolerance, endpointsByCell, unionFind);
        }

        var components = new List<List<DistanceContour>>();
        var componentByRoot = new Dictionary<int, int>();
        for (int i = 0; i < contours.Count; i++)
        {
            int root = unionFind.Find(i);
            if (!componentByRoot.TryGetValue(root, out int componentIndex))
            {
                componentIndex = components.Count;
                componentByRoot.Add(root, componentIndex);
                components.Add([]);
            }
            components[componentIndex].Add(contours[i]);
        }

        return components.Select(component => (IReadOnlyList<DistanceContour>)component).ToArray();
    }

    private static void AddEndpoint(
        DistanceContour contour,
        WorldPoint point,
        int contourIndex,
        double tolerance,
        Dictionary<Cell, List<Endpoint>> endpointsByCell,
        UnionFind unionFind)
    {
        long cellX = CellCoordinate(point.X, tolerance);
        long cellY = CellCoordinate(point.Y, tolerance);
        for (long y = cellY - 1; y <= cellY + 1; y++)
        for (long x = cellX - 1; x <= cellX + 1; x++)
        {
            var cell = new Cell(contour.Level, contour.IsThreshold, x, y);
            if (!endpointsByCell.TryGetValue(cell, out var endpoints))
                continue;
            foreach (var endpoint in endpoints)
            {
                if (WithinTolerance(point, endpoint.Point, tolerance))
                    unionFind.Union(contourIndex, endpoint.ContourIndex);
            }
        }

        var ownCell = new Cell(contour.Level, contour.IsThreshold, cellX, cellY);
        if (!endpointsByCell.TryGetValue(ownCell, out var ownEndpoints))
        {
            ownEndpoints = [];
            endpointsByCell.Add(ownCell, ownEndpoints);
        }
        ownEndpoints.Add(new Endpoint(contourIndex, point));
    }

    private static long CellCoordinate(double coordinate, double tolerance)
    {
        double scaled = Math.Floor(coordinate / tolerance);
        if (!double.IsFinite(scaled) || scaled <= long.MinValue || scaled >= long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(tolerance),
                "Tolerance is too small for the contour coordinate range.");
        return (long)scaled;
    }

    private static bool WithinTolerance(WorldPoint left, WorldPoint right, double tolerance)
    {
        double dx = Math.Abs(left.X - right.X);
        double dy = Math.Abs(left.Y - right.Y);
        if (dx > tolerance || dy > tolerance)
            return false;
        dx /= tolerance;
        dy /= tolerance;
        return (dx * dx) + (dy * dy) <= 1;
    }

    private static bool IsFinite(WorldPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private readonly record struct Cell(double Level, bool IsThreshold, long X, long Y);
    private readonly record struct Endpoint(int ContourIndex, WorldPoint Point);

    private sealed class UnionFind
    {
        private readonly int[] _parents;
        private readonly byte[] _ranks;

        public UnionFind(int count)
        {
            _parents = Enumerable.Range(0, count).ToArray();
            _ranks = new byte[count];
        }

        public int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }
            return item;
        }

        public void Union(int first, int second)
        {
            int firstRoot = Find(first);
            int secondRoot = Find(second);
            if (firstRoot == secondRoot)
                return;
            if (_ranks[firstRoot] < _ranks[secondRoot] ||
                _ranks[firstRoot] == _ranks[secondRoot] && firstRoot > secondRoot)
                (firstRoot, secondRoot) = (secondRoot, firstRoot);
            _parents[secondRoot] = firstRoot;
            if (_ranks[firstRoot] == _ranks[secondRoot])
                _ranks[firstRoot]++;
        }
    }
}
