namespace NetCraft.ModBuild.Diagnostics;

//Similarity 名字之间的相近程度 did-you-mean 靠它挑最近的候选
internal static class Similarity
{
    //Closest 在候选里挑最接近的那个 没有够近的算瞎猜 返回 null
    //bound 之外的差太远 列出来只会把人带偏
    public static string? Closest(string name, IEnumerable<string> candidates, int? bound = null)
    {
        var limit = bound ?? Math.Max(2, name.Length / 2);
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate == name)
                continue;

            var distance = Distance(name, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return bestDistance <= limit ? best : null;
    }

    //Rank 把候选按接近程度排好 取前几个
    public static List<string> Rank(string name, IEnumerable<string> candidates, int take, int? bound = null)
    {
        var limit = bound ?? Math.Max(2, name.Length / 2);
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && candidate != name)
            .Distinct(StringComparer.Ordinal)
            .Select(candidate => (Candidate: candidate, Distance: Distance(name, candidate)))
            .Where(item => item.Distance <= limit)
            .OrderBy(item => item.Distance)
            .Take(take)
            .Select(item => item.Candidate)
            .ToList();
    }

    //Distance 两个名字的编辑距离 大小写不计
    public static int Distance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var column = 0; column <= b.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= a.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= b.Length; column++)
            {
                var cost = char.ToLowerInvariant(a[row - 1]) == char.ToLowerInvariant(b[column - 1]) ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
