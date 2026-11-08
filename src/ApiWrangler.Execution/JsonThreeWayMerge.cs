using System.Text.Json.Nodes;

namespace ApiWrangler.Execution;

/// <summary>Builds a JSON merge proposal without changing any files or Git state.</summary>
public static class JsonThreeWayMerge
{
    public static JsonThreeWayMergeResult CreateProposal(JsonNode @base, JsonNode local, JsonNode incoming)
    {
        ArgumentNullException.ThrowIfNull(@base);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(incoming);

        var conflicts = new List<JsonMergeConflict>();
        var conflictPaths = new HashSet<string>(StringComparer.Ordinal);
        CollectDuplicateIdentityConflicts(@base, "", conflicts, conflictPaths);
        CollectDuplicateIdentityConflicts(local, "", conflicts, conflictPaths);
        CollectDuplicateIdentityConflicts(incoming, "", conflicts, conflictPaths);
        var proposal = MergeValue(@base, local, incoming, "", conflicts);
        // Root inputs are required to be non-null, so a root proposal cannot be absent.
        return new JsonThreeWayMergeResult(proposal!, conflicts);
    }

    private static JsonNode? MergeValue(JsonNode? @base, JsonNode? local, JsonNode? incoming, string path, List<JsonMergeConflict> conflicts)
    {
        if (@base is JsonArray baseWithDuplicates && local is JsonArray localWithDuplicates && incoming is JsonArray incomingWithDuplicates &&
            !IsOrderedHeadersPath(path) &&
            (HasDuplicateIdentity(baseWithDuplicates, out _, out _) ||
             HasDuplicateIdentity(localWithDuplicates, out _, out _) ||
             HasDuplicateIdentity(incomingWithDuplicates, out _, out _)))
            return local.DeepClone();

        if (JsonNode.DeepEquals(local, @base)) return incoming?.DeepClone();
        if (JsonNode.DeepEquals(incoming, @base) || JsonNode.DeepEquals(local, incoming)) return local?.DeepClone();

        if (@base is JsonObject baseObject && local is JsonObject localObject && incoming is JsonObject incomingObject)
        {
            var merged = new JsonObject();
            foreach (var key in baseObject.Select(p => p.Key).Concat(localObject.Select(p => p.Key)).Concat(incomingObject.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
            {
                var baseHas = baseObject.TryGetPropertyValue(key, out var baseValue);
                var localHas = localObject.TryGetPropertyValue(key, out var localValue);
                var incomingHas = incomingObject.TryGetPropertyValue(key, out var incomingValue);
                var child = MergePresence(baseHas, baseValue, localHas, localValue, incomingHas, incomingValue, ChildPath(path, key), conflicts);
                if (child.Present) merged[key] = child.Value;
            }
            return merged;
        }

        if (@base is JsonArray baseArray && local is JsonArray localArray && incoming is JsonArray incomingArray &&
            !IsOrderedHeadersPath(path) &&
            TryGetIdentityProperty(baseArray, localArray, incomingArray, out var identityProperty) &&
            TryGetIdentityMap(baseArray, identityProperty, out var baseMap) &&
            TryGetIdentityMap(localArray, identityProperty, out var localMap) &&
            TryGetIdentityMap(incomingArray, identityProperty, out var incomingMap))
        {
            return MergeIdentityArray(baseArray, baseMap, localMap, incomingMap, identityProperty, path, conflicts);
        }

        conflicts.Add(new JsonMergeConflict(DisplayPath(path), "Both versions changed this value."));
        return local?.DeepClone();
    }

    private static void CollectDuplicateIdentityConflicts(JsonNode? node, string path,
        List<JsonMergeConflict> conflicts, HashSet<string> conflictPaths)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
                CollectDuplicateIdentityConflicts(property.Value, ChildPath(path, property.Key), conflicts, conflictPaths);
        }
        else if (node is JsonArray array)
        {
            if (!IsOrderedHeadersPath(path) && HasDuplicateIdentity(array, out var property, out var id))
            {
                var conflictPath = ChildPath(path, $"{property}={id}");
                if (conflictPaths.Add(conflictPath))
                    conflicts.Add(new JsonMergeConflict(conflictPath, "Duplicate immutable IDs prevent a safe merge."));
            }

            for (var index = 0; index < array.Count; index++)
                CollectDuplicateIdentityConflicts(array[index], ChildPath(path, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), conflicts, conflictPaths);
        }
    }

    private static (bool Present, JsonNode? Value) MergePresence(
        bool baseHas, JsonNode? baseValue, bool localHas, JsonNode? localValue,
        bool incomingHas, JsonNode? incomingValue, string path, List<JsonMergeConflict> conflicts)
    {
        if (!baseHas) return localHas && incomingHas
            ? (true, MergeValue(null, localValue, incomingValue, path, conflicts))
            : localHas ? (true, localValue?.DeepClone()) : incomingHas ? (true, incomingValue?.DeepClone()) : (false, null);

        if (!localHas && !incomingHas) return (false, null);
        if (!localHas)
        {
            if (JsonNode.DeepEquals(incomingValue, baseValue)) return (false, null);
            conflicts.Add(new JsonMergeConflict(DisplayPath(path), "One version deleted this value while the other edited it."));
            return (true, incomingValue?.DeepClone());
        }
        if (!incomingHas)
        {
            if (JsonNode.DeepEquals(localValue, baseValue)) return (false, null);
            conflicts.Add(new JsonMergeConflict(DisplayPath(path), "One version deleted this value while the other edited it."));
            return (true, localValue?.DeepClone());
        }
        return (true, MergeValue(baseValue, localValue, incomingValue, path, conflicts));
    }

    private static JsonArray MergeIdentityArray(JsonArray baseArray, Dictionary<string, JsonNode?> baseMap,
        Dictionary<string, JsonNode?> localMap, Dictionary<string, JsonNode?> incomingMap,
        string identityProperty, string path, List<JsonMergeConflict> conflicts)
    {
        var baseOrder = baseArray.Select(x => ReadIdentity(x, identityProperty)!).ToList();
        var localOrder = localMap.Keys.ToList();
        var incomingOrder = incomingMap.Keys.ToList();
        var sharedBaseIds = baseOrder.Where(id => localMap.ContainsKey(id) && incomingMap.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var commonBaseOrder = baseOrder.Where(sharedBaseIds.Contains).ToList();
        var localBaseOrder = localOrder.Where(sharedBaseIds.Contains).ToList();
        var incomingBaseOrder = incomingOrder.Where(sharedBaseIds.Contains).ToList();
        var localReordered = !localBaseOrder.SequenceEqual(commonBaseOrder, StringComparer.Ordinal);
        var incomingReordered = !incomingBaseOrder.SequenceEqual(commonBaseOrder, StringComparer.Ordinal);
        if (localReordered && incomingReordered && !localBaseOrder.SequenceEqual(incomingBaseOrder, StringComparer.Ordinal))
        {
            conflicts.Add(new JsonMergeConflict(DisplayPath(path), "Both versions reordered the same entities differently."));
        }

        var ids = localReordered ? localOrder : incomingReordered ? incomingOrder : baseOrder;
        ids.AddRange(localOrder.Where(id => !ids.Contains(id, StringComparer.Ordinal)));
        ids.AddRange(incomingOrder.Where(id => !ids.Contains(id, StringComparer.Ordinal)));
        ids.AddRange(baseOrder.Where(id => !ids.Contains(id, StringComparer.Ordinal)));
        var merged = new JsonArray();
        foreach (var id in ids)
        {
            var baseHas = baseMap.TryGetValue(id, out var baseValue);
            var localHas = localMap.TryGetValue(id, out var localValue);
            var incomingHas = incomingMap.TryGetValue(id, out var incomingValue);
            var itemPath = ChildPath(path, $"{identityProperty}={id}");
            var result = MergePresence(baseHas, baseValue, localHas, localValue, incomingHas, incomingValue, itemPath, conflicts);
            if (result.Present) merged.Add(result.Value);
        }
        return merged;
    }

    private static bool TryGetIdentityProperty(JsonArray baseArray, JsonArray localArray, JsonArray incomingArray, out string property)
    {
        property = "id";
        var arrays = new[] { baseArray, localArray, incomingArray }.Where(array => array.Count > 0).ToArray();
        var selected = new[] { "id", "_caseId" }.FirstOrDefault(candidate => arrays.Length > 0 && arrays.All(array => array.All(item => ReadIdentity(item, candidate) is not null)));
        if (selected is null) return false;
        property = selected;
        return true;
    }

    private static bool TryGetIdentityMap(JsonArray array, string identityProperty, out Dictionary<string, JsonNode?> map)
    {
        map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            var id = ReadIdentity(item, identityProperty);
            if (id is null) return false;
            if (!map.TryAdd(id, item)) return false;
        }
        return true;
    }

    private static bool HasDuplicateIdentity(JsonArray array, out string property, out string duplicateId)
    {
        property = "id";
        duplicateId = "";
        foreach (var candidate in new[] { "id", "_caseId" })
        {
            if (array.Count == 0 || !array.All(item => ReadIdentity(item, candidate) is not null)) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                var id = ReadIdentity(item, candidate)!;
                if (!seen.Add(id))
                {
                    property = candidate;
                    duplicateId = id;
                    return true;
                }
            }
        }
        return false;
    }

    private static string? ReadIdentity(JsonNode? node, string property) =>
        node is JsonObject obj && obj[property] is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id)
            ? id : null;

    private static string ChildPath(string path, string property) => $"{path}/{property.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
    private static bool IsOrderedHeadersPath(string path) => path.EndsWith("/headers", StringComparison.OrdinalIgnoreCase);
    private static string DisplayPath(string path) => string.IsNullOrEmpty(path) ? "/" : path;
}

public sealed record JsonMergeConflict(string Path, string Reason);

public sealed record JsonThreeWayMergeResult(JsonNode Proposal, IReadOnlyList<JsonMergeConflict> Conflicts)
{
    public bool IsConflictFree => Conflicts.Count == 0;
}
