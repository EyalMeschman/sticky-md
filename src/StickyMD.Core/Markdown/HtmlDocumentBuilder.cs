using System.Security.Cryptography;
using System.Text;
using StickyMD.Core.Theming;

namespace StickyMD.Core.Markdown;

/// <param name="Theme">
/// From <see cref="NotePalette"/>. The same instance the WPF chrome uses, so a
/// note's window and its content cannot disagree about what "yellow" is.
/// </param>
/// <param name="AllowRemoteImages">
/// The EFFECTIVE setting -- the global preference OR this note's per-session
/// opt-in. It changes the CSP, which means changing it requires a fresh
/// navigation; see <see cref="HtmlDocumentBuilder"/>.
/// </param>
/// <param name="FontSizePx">
/// The note's own text size. Per note, not per app: it lives in
/// <c>notes.json</c> beside the colour and the opacity, and
/// <c>AppSettings.DefaultFontSizePx</c> is only what a NEW note starts at.
/// </param>
public sealed record HtmlShellOptions(
    NoteTheme Theme,
    bool AllowRemoteImages,
    int FontSizePx = HtmlDocumentBuilder.DefaultFontSizePx);

/// <summary>
/// Builds the static page each note's WebView2 navigates to exactly once.
/// Rendered Markdown arrives afterwards by postMessage.
/// </summary>
/// <remarks>
/// DELIVERY. The shell goes in via NavigateToString rather than a file. A file
/// in the notes root is forbidden -- nothing app-owned goes there -- and a
/// second virtual-host mapping onto an app asset folder would buy a second
/// origin and an installed-file dependency for nothing. The resulting document
/// has an opaque origin, which costs nothing here: images are absolute
/// https://note.local/... URLs by the time the renderer is done, so the missing
/// baseURI is never consulted.
///
/// CSP LIFETIME. A meta-tag CSP is fixed at parse time. So content updates go
/// by postMessage with no navigation, but a change to the remote-image setting
/// requires rebuilding this shell and navigating again. That is the reading
/// that satisfies both "the CSP is built per render from the effective
/// remote-image setting" and "navigate once to a static shell": the CSP is
/// per-SHELL, and a new CSP means a new shell. Enabling remote images is a
/// deliberate click, so one frame of flash there is acceptable; an external
/// edit arriving every 200ms is not, which is why content never re-navigates.
///
/// NONCE, NOT UNSAFE-INLINE. The shell's own style and script are the only
/// inline code the page ever runs, so they get a per-shell nonce. Markdig runs
/// with DisableHtml(), so nothing the user typed can reach the DOM as markup --
/// but 'unsafe-inline' would remove the guarantee that stays true if that ever
/// changes.
/// </remarks>
public static class HtmlDocumentBuilder
{
    /// <summary>
    /// The one starting text size, in CSS pixels.
    /// </summary>
    /// <remarks>
    /// Declared HERE, and referenced by both <see cref="HtmlShellOptions"/>'s
    /// default and <c>AppSettings.DefaultFontSizePx</c>, rather than written
    /// three times. Two numbers both meaning "the default text size" is how the
    /// shell's CSS and the app's settings come to disagree -- the same drift
    /// NotePalette exists to prevent for colour.
    ///
    /// On this type rather than on the record because a record's
    /// primary-constructor default cannot reference a const in its own body.
    ///
    /// Every heading, code and blockquote size in the stylesheet is an
    /// <c>em</c> multiple of this, so changing it scales a note's whole
    /// typographic scale rather than only its body text.
    /// </remarks>
    public const int DefaultFontSizePx = 16;

    /// <summary>
    /// The virtual host WebView2 maps onto the note's directory. The one
    /// definition: the renderer builds image URLs against it, the CSP allows
    /// it, the host maps it, and the navigation policy refuses to navigate to it.
    /// </summary>
    public const string VirtualHost = "note.local";

    private const string FontFamily =
        "'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif";

    public static string BuildCsp(bool allowRemoteImages, string nonce)
    {
        // Blocking remote images by default matters because these files sync. A
        // shared note containing ![](https://example.com/tracker?id=123) must
        // not make a network request just because StickyMD rendered it.
        var img = $"img-src https://{VirtualHost} data:";
        if (allowRemoteImages) img += " https:";

        return string.Join("; ",
            "default-src 'none'",
            img,
            $"style-src 'nonce-{nonce}'",
            $"script-src 'nonce-{nonce}'",
            "font-src 'none'",
            "connect-src 'none'",
            "object-src 'none'",
            "form-action 'none'",
            "frame-ancestors 'none'",
            "base-uri 'none'");
    }

    public static string BuildStyleBlock(NoteTheme theme, string nonce, int fontSizePx)
    {
        var variables = new StringBuilder();
        foreach (var (name, value) in NotePalette.ToCssVariables(theme))
            variables.Append("      ").Append(name).Append(": ").Append(value).AppendLine(";");

        return $$"""
            <style nonce="{{nonce}}">
              :root {
            {{variables.ToString().TrimEnd()}}
              }
              html, body {
                margin: 0;
                padding: 0;
                background: var(--note-content-bg);
                color: var(--note-content-fg);
                font-family: {{FontFamily}};
                font-size: {{fontSizePx}}px;
                line-height: 1.45;
                overflow-wrap: break-word;
              }
              #content { padding: 8px 12px 16px 12px; }
              #content > :first-child { margin-top: 0; }
              h1, h2, h3, h4, h5, h6 { margin: 0.8em 0 0.35em; line-height: 1.25; }
              h1 { font-size: 1.35em; }
              h2 { font-size: 1.2em; }
              h3 { font-size: 1.08em; }
              p, ul, ol, blockquote, pre, table { margin: 0.5em 0; }
              a { color: var(--note-accent); }
              hr { border: none; border-top: 1px solid var(--note-muted); }
              blockquote {
                margin-left: 0;
                padding-left: 10px;
                border-left: 3px solid var(--note-muted);
                color: var(--note-muted);
              }
              code, pre {
                font-family: 'Cascadia Mono', Consolas, monospace;
                font-size: 0.92em;
                background: var(--note-code-bg);
              }
              code { padding: 0.1em 0.3em; border-radius: 3px; }
              pre { padding: 8px 10px; border-radius: 4px; overflow-x: auto; }
              pre code { background: none; padding: 0; }
              table { border-collapse: collapse; }
              th, td { border: 1px solid var(--note-muted); padding: 3px 7px; }
              ul, ol { padding-left: 1.4em; }
              li { margin: 0.15em 0; }
              /* Task lists read as checklists, not as bulleted checkboxes. */
              li:has(> input[type="checkbox"]) { list-style: none; margin-left: -1.2em; }
              input[type="checkbox"] { accent-color: var(--note-accent); margin-right: 0.4em; }
              img { max-width: 100%; height: auto; }
              .blocked-image {
                display: inline-block;
                padding: 2px 8px;
                border: 1px dashed var(--note-muted);
                border-radius: 4px;
                color: var(--note-muted);
                font-size: 0.85em;
                cursor: default;
              }
              ::selection { background: var(--note-accent); color: var(--note-content-bg); }
            </style>
            """;
    }

    public static string BuildShell(HtmlShellOptions options)
    {
        // A fresh nonce per shell. Reusing one across notes would make the
        // value predictable, which is the whole point of a nonce.
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        var csp = BuildCsp(options.AllowRemoteImages, nonce);
        var style = BuildStyleBlock(options.Theme, nonce, options.FontSizePx);
        var script = BuildBridgeScript(nonce);

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{csp}">
            <title>note</title>
            {style}
            </head>
            <body>
            <div id="content"></div>
            {script}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// The host-page contract. Inbound: <c>render</c>. Outbound: <c>ready</c>,
    /// <c>toggleTask</c>, <c>link</c>, <c>requestEdit</c>.
    /// </summary>
    private static string BuildBridgeScript(string nonce) => $$"""
        <script nonce="{{nonce}}">
        (function () {
          var content = document.getElementById('content');
          var token = '';

          function post(message) {
            window.chrome.webview.postMessage(message);
          }

          // An image the resource policy refused. Its src carries an unknown
          // scheme, so it never reached the network -- replace it with a
          // placeholder so the note does not show a broken-image icon.
          function replaceBlockedImages() {
            var blocked = content.querySelectorAll('img[src^="{{MarkdownRenderer.BlockedScheme}}"]');
            for (var i = 0; i < blocked.length; i++) {
              var img = blocked[i];
              var span = document.createElement('span');
              span.className = 'blocked-image';
              span.textContent = 'image blocked';
              span.title = img.getAttribute('src').substring({{MarkdownRenderer.BlockedScheme.Length}});
              if (img.parentNode) img.parentNode.replaceChild(span, img);
            }
          }

          window.chrome.webview.addEventListener('message', function (event) {
            var message = event.data;
            if (!message || message.type !== 'render') return;

            // Preserve scroll across the swap. Re-navigating per update would
            // reset it, which is most noticeable when a long note reloads from
            // an external edit.
            var scroll = document.documentElement.scrollTop || document.body.scrollTop || 0;

            token = message.token;
            content.innerHTML = message.html;
            replaceBlockedImages();

            document.documentElement.scrollTop = scroll;
            document.body.scrollTop = scroll;
          });

          content.addEventListener('click', function (event) {
            var target = event.target;

            if (target && target.matches('input[type="checkbox"]')) {
              // Cancel the default toggle. The visual state must come from the
              // re-render the host sends back, or a refused click leaves a
              // checkbox showing something the file does not say.
              event.preventDefault();
              post({
                type: 'toggleTask',
                spanStart: parseInt(target.getAttribute('data-span-start'), 10),
                spanEnd: parseInt(target.getAttribute('data-span-end'), 10),
                token: token
              });
              return;
            }

            var anchor = target && target.closest ? target.closest('a[href]') : null;
            if (!anchor) return;

            event.preventDefault();
            var href = anchor.getAttribute('href');

            // In-note anchors are handled here. Fragment navigation inside an
            // opaque-origin document does not reliably raise NavigationStarting,
            // so routing it through the host would sometimes do nothing at all.
            if (href && href.charAt(0) === '#') {
              // decodeURIComponent throws URIError on a malformed escape, and
              // "#50%" is a perfectly ordinary heading anchor. Unguarded, that
              // throw escaped this handler: preventDefault had already run, so
              // the page did not scroll, nothing was posted to the host, and
              // there was no error anywhere the user could see. Falling back to
              // the raw text is right -- an un-decodable fragment was almost
              // certainly never encoded.
              var raw = href.substring(1);
              var anchorName;
              try { anchorName = decodeURIComponent(raw); } catch (e) { anchorName = raw; }
              var destination = document.getElementById(anchorName)
                || content.querySelector('[name="' + CSS.escape(anchorName) + '"]');
              if (destination) destination.scrollIntoView();
              return;
            }

            // The RAW attribute, never anchor.href. With an opaque origin,
            // anchor.href resolves "other.md" against about:blank and the host
            // receives something it cannot map back to a file.
            post({ type: 'link', href: href });
          });

          document.addEventListener('dblclick', function (event) {
            // Only empty space enters edit mode. Double-clicking rendered text,
            // a link, code, or a checkbox keeps native word selection.
            if (event.target !== document.body && event.target !== content) return;
            post({ type: 'requestEdit' });
          });

          post({ type: 'ready' });
        })();
        </script>
        """;
}
