using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace DeficitSnatcher.Api.Ai;

/// <summary>An element the AI can act on: a stable selector for it, and what a person would see.</summary>
public sealed record PageElement(
    string Id,
    string Selector,
    string? Frame,
    string Tag,
    string? Type,
    string? Label,
    bool Filled,
    bool? Checked,
    bool Disabled,
    string[]? Options);

/// <summary>A page as the AI sees it.</summary>
public sealed record PageState(string Url, string Title, string Text, List<PageElement> Elements, byte[]? Screenshot)
{
    /// <summary>The page for the AI's question, as text (the screenshot goes separately).</summary>
    public string Describe(int maxText = 3500)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"URL: {Url.Split('?', '#')[0]}");
        sb.AppendLine($"Title: {Title}");
        sb.AppendLine("Visible text (start):");
        sb.AppendLine(Text.Length > maxText ? Text[..maxText] + " …" : Text);
        sb.AppendLine();
        sb.AppendLine("Elements you can act on (use the selector, and frame when given, exactly as written):");
        foreach (var e in Elements)
        {
            sb.Append($"- {e.Id}: <{e.Tag}{(e.Type is { Length: > 0 } t ? $" type={t}" : "")}> \"{e.Label}\" selector={e.Selector}");
            if (e.Frame != null)
                sb.Append($" frame={e.Frame}");
            if (e.Filled)
                sb.Append(" [has a value]");
            if (e.Checked == true)
                sb.Append(" [checked]");
            if (e.Disabled)
                sb.Append(" [disabled]");
            if (e.Options is { Length: > 0 } o)
                sb.Append($" options: {string.Join(" | ", o.Take(15))}{(o.Length > 15 ? " …" : "")}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>Reads a page for the AI: visible text and the elements that can be clicked or filled, in every frame (card
/// fields are usually in iframes). Values typed into fields are never read, only whether there is one.</summary>
public static partial class PageReader
{
    [System.Text.RegularExpressions.GeneratedRegex(@"[-_](?=[a-z0-9]*\d)[a-z0-9]{6,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex RandomTail();

    // Runs in each frame: visible interactive elements with the most stable selector found for each.
    private const string Script = """
        () => {
          const visible = el => {
            const r = el.getBoundingClientRect();
            const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0';
          };
          const unique = sel => { try { return document.querySelectorAll(sel).length === 1; } catch { return false; } };
          const q = v => JSON.stringify(v);
          const clean = t => (t || '').replace(/\s+/g, ' ').trim().slice(0, 80);
          const labelOf = el => {
            if (el.labels && el.labels.length) return clean(el.labels[0].innerText);
            return clean(el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.getAttribute('title')
              || el.innerText || el.value && (el.type === 'submit' || el.type === 'button') && el.value || el.getAttribute('name') || '');
          };
          const selectorOf = el => {
            const tag = el.tagName.toLowerCase();
            if (el.id && !/^\d/.test(el.id) && !/[:\[\]]/.test(el.id) && unique('#' + CSS.escape(el.id))) return '#' + CSS.escape(el.id);
            for (const a of ['data-testid', 'data-test', 'data-qa', 'name', 'autocomplete', 'aria-label', 'placeholder']) {
              const v = el.getAttribute(a);
              if (v && unique(`${tag}[${a}=${q(v)}]`)) return `${tag}[${a}=${q(v)}]`;
            }
            const text = clean(el.innerText);
            if (text && (tag === 'button' || tag === 'a' || el.getAttribute('role') === 'button')) {
              const sel = `${tag}:has-text(${q(text.slice(0, 40))})`;
              return sel;
            }
            // A path from the nearest element with an id.
            const parts = [];
            let node = el;
            while (node && node.nodeType === 1 && parts.length < 6) {
              if (node !== el && node.id && unique('#' + CSS.escape(node.id))) { parts.unshift('#' + CSS.escape(node.id)); break; }
              const p = node.parentElement;
              const same = p ? [...p.children].filter(c => c.tagName === node.tagName) : [];
              parts.unshift(node.tagName.toLowerCase() + (same.length > 1 ? `:nth-of-type(${same.indexOf(node) + 1})` : ''));
              node = p;
            }
            return parts.join(' > ');
          };
          const els = [...document.querySelectorAll(
            'input:not([type=hidden]), select, textarea, button, a[href], [role=button], [role=checkbox], [role=radio], [role=combobox], [role=option], [contenteditable=true]')];
          const out = [];
          for (const el of els) {
            if (!visible(el) || out.length >= 120) continue;
            const tag = el.tagName.toLowerCase();
            out.push({
              selector: selectorOf(el),
              tag,
              type: el.getAttribute('type') || el.getAttribute('role') || null,
              label: labelOf(el),
              filled: (tag === 'input' || tag === 'textarea') && !['checkbox', 'radio', 'submit', 'button'].includes(el.type) ? !!el.value : false,
              checked: el.type === 'checkbox' || el.type === 'radio' ? !!el.checked : null,
              disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true',
              options: tag === 'select' ? [...el.options].map(o => clean(o.text)).filter(Boolean).slice(0, 40) : null,
            });
          }
          return { title: document.title, text: (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n').slice(0, 8000), elements: out };
        }
        """;

    public static async Task<PageState> ReadAsync(IPage page, bool screenshot, CancellationToken ct = default)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
        }

        var elements = new List<PageElement>();
        string title = "", text = "";
        var n = 0;
        foreach (var frame in page.Frames)
        {
            string? frameSelector = null;
            if (frame != page.MainFrame)
            {
                frameSelector = await FrameSelectorAsync(frame);
                if (frameSelector == null)
                    continue;
            }
            JsonElement result;
            try
            {
                result = await frame.EvaluateAsync<JsonElement>(Script);
            }
            catch (PlaywrightException)
            {
                continue; // A frame that went away or can't run script.
            }
            if (frame == page.MainFrame)
            {
                title = result.GetProperty("title").GetString() ?? "";
                text = result.GetProperty("text").GetString() ?? "";
            }
            foreach (var e in result.GetProperty("elements").EnumerateArray())
            {
                if (elements.Count >= 150)
                    break;
                elements.Add(new PageElement(
                    $"e{++n}",
                    e.GetProperty("selector").GetString() ?? "",
                    frameSelector,
                    e.GetProperty("tag").GetString() ?? "",
                    e.GetProperty("type").GetString(),
                    e.GetProperty("label").GetString(),
                    e.GetProperty("filled").GetBoolean(),
                    e.GetProperty("checked").ValueKind == JsonValueKind.True ? true : e.GetProperty("checked").ValueKind == JsonValueKind.False ? false : null,
                    e.GetProperty("disabled").GetBoolean(),
                    e.GetProperty("options").ValueKind == JsonValueKind.Array ? e.GetProperty("options").EnumerateArray().Select(o => o.GetString() ?? "").ToArray() : null));
            }
        }

        byte[]? shot = null;
        if (screenshot)
        {
            try
            {
                shot = await page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 55, Timeout = 8000 });
            }
            catch (Exception)
            {
                // The AI can do without it.
            }
        }
        return new PageState(page.Url, title, text, elements, shot);
    }

    /// <summary>A selector for the iframe element that holds this frame (name, title, id, or part of its address).</summary>
    private static async Task<string?> FrameSelectorAsync(IFrame frame)
    {
        try
        {
            var el = await frame.FrameElementAsync();
            if (!await el.IsVisibleAsync())
                return null;
            foreach (var attr in new[] { "name", "title", "id" })
                if (await el.GetAttributeAsync(attr) is { Length: > 0 } v)
                {
                    // Names like "card-fields-number-x7f3k2a9" change every visit: match the start only.
                    var stable = RandomTail().Replace(v, "");
                    return stable.Length >= 4 && stable != v
                        ? $"iframe[{attr}^={JsonSerializer.Serialize(stable)}]"
                        : $"iframe[{attr}={JsonSerializer.Serialize(v)}]";
                }
            if (await el.GetAttributeAsync("src") is { Length: > 0 } src && Uri.TryCreate(src, UriKind.Absolute, out var u))
                return $"iframe[src*={JsonSerializer.Serialize(u.Host + u.AbsolutePath)}]";
        }
        catch (PlaywrightException)
        {
        }
        return null;
    }
}
