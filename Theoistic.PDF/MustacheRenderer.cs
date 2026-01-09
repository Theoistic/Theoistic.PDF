using System.Collections;
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

    public string Render(string template, object model)
    {
        return RenderTemplate(template, model);
    }

    private string RenderTemplate(string template, object model)
    {
        // First, handle sections
        template = RenderSections(template, model);

        // Next, handle simple tags
        template = RenderVariables(template, model);

        return template;
    }

    private string RenderVariables(string template, object model)
    {
        return TagPattern.Replace(template, match =>
        {
            string tagContent = match.Groups[1].Value.Trim();

            // If it's a section tag, we skip here. Sections are handled separately.
            if (tagContent.StartsWith("#") || tagContent.StartsWith("^") || tagContent.StartsWith("/"))
            {
                return match.Value;
            }

            // It's a normal variable interpolation.
            return LookupValue(model, tagContent);
        });
    }

    private string RenderSections(string template, object model)
    {
        // A basic approach: find sections {{#section}}...{{/section}} and {{^section}}...{{/section}},
        // recursively render them.
        // We'll do this iteratively until no more sections are found.

        // Regex to find the outermost section:
        // We'll look for a pattern: {{#key}}(.*?){{/key}} or {{^key}}(.*?){{/key}} 
        // in a non-greedy manner, and attempt to handle nesting by repeatedly applying.
        var sectionPattern = new Regex(@"{{[#^]([A-Za-z0-9_\.]+)}}(.*?){{/\1}}", RegexOptions.Singleline);

        string rendered = template;
        bool foundSection = true;

        while (foundSection)
        {
            foundSection = false;
            var matches = sectionPattern.Matches(rendered);
            if (matches.Count == 0) break;

            foreach (Match match in matches)
            {
                foundSection = true;
                string sectionName = match.Groups[1].Value;
                string sectionContent = match.Groups[2].Value;
                bool inverted = match.Value.StartsWith("{{^");

                string replacement = RenderSection(sectionName, sectionContent, model, inverted);
                rendered = rendered.Substring(0, match.Index) + replacement + rendered.Substring(match.Index + match.Length);
                break; // After replacement, indices shift, so break and start again
            }
        }

        return rendered;
    }

    private string RenderSection(string sectionName, string sectionContent, object model, bool inverted)
    {
        object value = ResolveModelValue(model, sectionName);

        bool shouldRender = ShouldRenderSection(value, inverted);
        if (!shouldRender)
        {
            return string.Empty;
        }

        // If value is a list, we iterate over each item as a new context
        if (value is IEnumerable && !(value is string))
        {
            var sb = new StringBuilder();
            foreach (var item in (IEnumerable)value)
            {
                sb.Append(RenderTemplate(sectionContent, item));
            }
            return sb.ToString();
        }
        else if (value is bool boolVal)
        {
            // For booleans:
            // true: render section content with the SAME context (don't change model)
            // false: would never get here, because we already returned if !shouldRender
            return boolVal ? RenderTemplate(sectionContent, model) : string.Empty;
        }
        else if (value != null)
        {
            // For non-boolean objects (like another model), use the object as the new context
            return RenderTemplate(sectionContent, value);
        }
        else
        {
            // Null value, but we made it here implies shouldRender is true for some reason.
            // Typically shouldn't happen if shouldRender is correct. Just return empty.
            return string.Empty;
        }
    }

    private bool ShouldRenderSection(object value, bool inverted)
    {
        bool isTruthy = false;
        if (value == null)
        {
            isTruthy = false;
        }
        else if (value is bool b)
        {
            isTruthy = b;
        }
        else if (value is IEnumerable en && !(value is string))
        {
            // For lists, truthy if not empty
            isTruthy = en.Cast<object>().Any();
        }
        else
        {
            // Any other non-null object is considered truthy
            isTruthy = true;
        }

        return inverted ? !isTruthy : isTruthy;
    }

    private string LookupValue(object model, string key)
    {
        if (model == null) return string.Empty;
        if (key == ".") return model.ToString();

        object val = ResolveModelValue(model, key);
        return val?.ToString() ?? string.Empty;
    }

    private object ResolveModelValue(object model, string key)
    {
        if (model == null) return null;

        // Support nested keys using dot notation
        string[] parts = key.Split('.');
        object current = model;
        foreach (var part in parts)
        {
            if (current == null) return null;

            var type = current.GetType();

            // Try property
            var prop = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null)
            {
                current = prop.GetValue(current);
                continue;
            }

            // Try field
            var field = type.GetField(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                current = field.GetValue(current);
                continue;
            }

            // If not found, return null
            return null;
        }

        return current;
    }
}