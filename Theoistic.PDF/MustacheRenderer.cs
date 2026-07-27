using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Theoistic.PDF;

/// <summary>
/// A simplified Mustache-like renderer. Supports:
/// - {{variable}} interpolation
/// - {{#section}} and {{/section}} for lists and bool checks
/// - {{^section}} and {{/section}} for inverted sections
/// - The "." key for current item in a list
/// Does not fully comply with Mustache spec.
/// </summary>
public class MustacheRenderer
{
    private static readonly Regex TagPattern = new Regex(@"{{(.*?)}}", RegexOptions.Compiled);

    private static readonly Regex SectionPattern =
        new Regex(@"{{[#^]([A-Za-z0-9_\.]+)}}(.*?){{/\1}}", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Guards against templates whose rendered output keeps producing section tags - for example
    /// when a model value itself contains "{{#x}}". Without it the section loop never terminates.
    /// </summary>
    private const int MaxSectionPasses = 10_000;

    private static readonly ConcurrentDictionary<(Type Type, string Name), MemberInfo?> MemberCache = new();

    public string Render(string template, object model)
    {
        ArgumentNullException.ThrowIfNull(template);

        return RenderTemplate(template, model);
    }

    private string RenderTemplate(string template, object? model)
    {
        // First, handle sections
        template = RenderSections(template, model);

        // Next, handle simple tags
        template = RenderVariables(template, model);

        return template;
    }

    private string RenderVariables(string template, object? model)
    {
        return TagPattern.Replace(template, match =>
        {
            string tagContent = match.Groups[1].Value.Trim();

            // If it's a section tag, we skip here. Sections are handled separately.
            if (tagContent.StartsWith('#') || tagContent.StartsWith('^') || tagContent.StartsWith('/'))
            {
                return match.Value;
            }

            // It's a normal variable interpolation.
            return LookupValue(model, tagContent);
        });
    }

    private string RenderSections(string template, object? model)
    {
        // Repeatedly replace the first section found. Matching one at a time avoids the full
        // scan that Matches().Count forces, and the indices stay valid for the single edit.
        string rendered = template;

        for (int pass = 0; pass < MaxSectionPasses; pass++)
        {
            Match match = SectionPattern.Match(rendered);

            if (!match.Success)
            {
                return rendered;
            }

            string sectionName = match.Groups[1].Value;
            string sectionContent = match.Groups[2].Value;
            bool inverted = match.Value.StartsWith("{{^", StringComparison.Ordinal);

            string replacement = RenderSection(sectionName, sectionContent, model, inverted);

            rendered = string.Concat(
                rendered.AsSpan(0, match.Index),
                replacement.AsSpan(),
                rendered.AsSpan(match.Index + match.Length));
        }

        throw new InvalidOperationException(
            $"Template did not stabilise after {MaxSectionPasses} section passes; it likely contains section tags that re-emerge from the model data.");
    }

    private string RenderSection(string sectionName, string sectionContent, object? model, bool inverted)
    {
        object? value = ResolveModelValue(model, sectionName);

        if (!ShouldRenderSection(value, inverted))
        {
            return string.Empty;
        }

        // An inverted section renders its body against the unchanged context.
        if (inverted)
        {
            return RenderTemplate(sectionContent, model);
        }

        // If value is a list, we iterate over each item as a new context
        if (value is IEnumerable enumerable && value is not string)
        {
            var sb = new StringBuilder();

            foreach (var item in enumerable)
            {
                sb.Append(RenderTemplate(sectionContent, item));
            }

            return sb.ToString();
        }

        if (value is bool)
        {
            // Only a true value reaches this point, and it renders with the SAME context.
            return RenderTemplate(sectionContent, model);
        }

        if (value != null)
        {
            // For non-boolean objects (like another model), use the object as the new context
            return RenderTemplate(sectionContent, value);
        }

        return string.Empty;
    }

    private bool ShouldRenderSection(object? value, bool inverted)
    {
        bool isTruthy = value switch
        {
            null => false,
            bool b => b,
            string => true,
            // For lists, truthy if not empty
            IEnumerable en => HasAny(en),
            // Any other non-null object is considered truthy
            _ => true
        };

        return inverted ? !isTruthy : isTruthy;
    }

    private static bool HasAny(IEnumerable source)
    {
        if (source is ICollection collection)
        {
            return collection.Count > 0;
        }

        IEnumerator enumerator = source.GetEnumerator();

        try
        {
            return enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    private string LookupValue(object? model, string key)
    {
        if (model == null) return string.Empty;
        if (key == ".") return model.ToString() ?? string.Empty;

        object? val = ResolveModelValue(model, key);
        return val?.ToString() ?? string.Empty;
    }

    private object? ResolveModelValue(object? model, string key)
    {
        if (model == null) return null;

        // Support nested keys using dot notation
        string[] parts = key.Split('.');
        object? current = model;

        foreach (var part in parts)
        {
            if (current == null) return null;

            current = ResolveMember(current, part);
        }

        return current;
    }

    private static object? ResolveMember(object instance, string name)
    {
        // Member lookup by name is the hot path of a render; resolving it once per (type, name)
        // keeps repeated renders off the reflection tables.
        MemberInfo? member = MemberCache.GetOrAdd((instance.GetType(), name), static key =>
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            PropertyInfo? prop = key.Type.GetProperty(key.Name, Flags);

            if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
            {
                return prop;
            }

            return key.Type.GetField(key.Name, Flags);
        });

        return member switch
        {
            PropertyInfo property => property.GetValue(instance),
            FieldInfo field => field.GetValue(instance),
            _ => null
        };
    }
}
