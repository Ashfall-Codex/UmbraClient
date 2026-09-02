namespace UmbraSync.PlayerData.Data;
public static class UnorderedSetComparison
{
    public static bool SetsEqual(HashSet<string> first, HashSet<string> second)
    {
        if (first.Count != second.Count)
            return false;

        for (int i = 0; i < first.Count; i++)
        {
            if (!string.Equals(first.ElementAt(i), second.ElementAt(i), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
    
    public static int OrderIndependentHashCode<T>(IEnumerable<T> source) where T : notnull
    {
        int hash = 0;
        foreach (T element in source)
        {
            hash = unchecked(hash + EqualityComparer<T>.Default.GetHashCode(element));
        }

        return hash;
    }
}
