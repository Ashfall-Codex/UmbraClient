using UmbraSync.API.Data;

namespace UmbraSync.Services;

public sealed class RpCustomFieldGroup(string name)
{
    public string Name { get; set; } = name;
    public List<RpCustomField> Fields { get; } = [];
    public bool IsDefault => Name.Length == 0;
}

public static class RpCustomFieldGroups
{
    public const int MaxSectionNameLength = 30;
    public const int MaxFieldsPerSection = 10;
    public const int MaxNamedSections = 6;

    public static string NormalizeSection(string? section)
    {
        var trimmed = (section ?? string.Empty).Trim();
        return trimmed.Length <= MaxSectionNameLength ? trimmed : trimmed[..MaxSectionNameLength];
    }

    public static List<RpCustomFieldGroup> Group(IEnumerable<RpCustomField> fields, bool includeEmptyDefault)
    {
        var defaultGroup = new RpCustomFieldGroup(string.Empty);
        var groups = new List<RpCustomFieldGroup> { defaultGroup };

        foreach (var field in fields.OrderBy(f => f.Order))
        {
            var section = NormalizeSection(field.Section);
            var group = groups.Find(g => string.Equals(g.Name, section, StringComparison.Ordinal));
            if (group == null)
            {
                group = new RpCustomFieldGroup(section);
                groups.Add(group);
            }
            group.Fields.Add(field);
        }

        if (!includeEmptyDefault && defaultGroup.Fields.Count == 0)
            groups.Remove(defaultGroup);
        return groups;
    }

    public static void Flatten(List<RpCustomFieldGroup> groups, List<RpCustomField> target)
    {
        target.Clear();
        foreach (var group in groups)
        {
            foreach (var field in group.Fields)
            {
                field.Section = group.Name;
                field.Order = target.Count;
                target.Add(field);
            }
        }
    }
}
