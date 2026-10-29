using System.Text;
using Theoistic.PDF.Writing;

namespace Theoistic.PDF.Editing;

/// <summary>
/// Opens an existing PDF to draw on its pages and add pages after them.
/// <para>
/// Nothing in the original file is rewritten. <see cref="Save"/> returns the original bytes,
/// unchanged, followed by one incremental update holding everything new - the standard way to
/// extend a PDF, and the only way that leaves a signature already in it verifying for the
/// revision it signed.
/// </para>
/// <para>
/// Drawing uses the ordinary <see cref="PdfPage"/> API, so text, TrueType fonts, images with
/// transparency and paths work exactly as they do in a new document.
/// </para>
/// </summary>
public sealed class PdfEditor
{
    private readonly PdfFile file;
    private readonly List<PageSlot> slots = new List<PageSlot>();
    private readonly int originalPageCount;

    // Everything drawn - on existing pages and on added ones - goes into an ordinary document
    // first. At save time that document is written, read back, and its pages become Form
    // XObjects or new pages in the update, so the editor reuses the writer's fonts, subsetting
    // and image handling instead of duplicating them.
    private readonly PdfDocument scratch = new PdfDocument();

    private PdfEditor(PdfFile file, List<PdfPageNode> pages)
    {
        this.file = file;

        foreach (PdfPageNode page in pages)
        {
            slots.Add(PageSlot.Existing(page, file, isOriginal: true));
        }

        originalPageCount = pages.Count;
    }

    /// <summary>
    /// Parses <paramref name="pdf"/>. Throws <see cref="PdfFormatException"/> when it is not a
    /// readable PDF, and when it is encrypted.
    /// </summary>
    public static PdfEditor Open(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        PdfFile file = PdfFile.Open(pdf);
        List<PdfPageNode> pages = PageTree.Read(file);

        if (pages.Count == 0)
        {
            throw new PdfFormatException("The PDF has no pages.");
        }

        return new PdfEditor(file, pages);
    }

    /// <summary>
    /// Every page as a viewer displays it - the crop box, falling back to the media box, turned by
    /// /Rotate - so a portrait page rotated by 90 degrees reports a landscape width and height.
    /// Pages added with <see cref="AddPage(double, double)"/> and <see cref="AppendPages"/> are
    /// included, at the index they will have in the saved document.
    /// </summary>
    public IReadOnlyList<PdfPageInfo> Pages =>
        slots.Select((slot, index) => new PdfPageInfo(index, slot.Width, slot.Height, slot.Rotation)).ToList();

    /// <summary>
    /// A drawing surface over a page, in displayed coordinates: the origin is the bottom-left
    /// corner of the page as it is displayed, after the crop box and rotation are applied, and
    /// the width and height are those in <see cref="Pages"/>. Calling this again for the same page
    /// returns the same surface.
    /// </summary>
    public PdfPage DrawOn(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= slots.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageIndex),
                pageIndex,
                $"The document has {slots.Count} page(s); page indexes start at 0.");
        }

        PageSlot slot = slots[pageIndex];

        if (slot.Drawing is null)
        {
            slot.Drawing = scratch.AddPage(slot.Width, slot.Height);
            slot.ScratchIndex = scratch.Pages.Count - 1;
        }

        return slot.Drawing;
    }

    /// <summary>Appends a blank page of the given size in points, drawn on with the normal API.</summary>
    public PdfPage AddPage(double width, double height)
    {
        PdfPage page = scratch.AddPage(width, height);

        slots.Add(PageSlot.Blank(page, scratch.Pages.Count - 1));

        return page;
    }

    /// <summary>Appends a blank page of a standard paper size.</summary>
    public PdfPage AddPage(PdfPaperSize size, PdfOrientation orientation = PdfOrientation.Portrait)
    {
        (double width, double height) = size.ToPoints();

        if (orientation == PdfOrientation.Landscape)
        {
            (width, height) = (height, width);
        }

        return AddPage(width, height);
    }

    /// <summary>
    /// Appends every page of another PDF - the output of <c>FlowDocument.ToArray()</c>, say - after
    /// the pages already there. Each page brings its content, fonts and images; annotations and
    /// links on appended pages are not carried over.
    /// </summary>
    public void AppendPages(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        PdfFile source = PdfFile.Open(pdf);
        List<PdfPageNode> pages = PageTree.Read(source);

        if (pages.Count == 0)
        {
            throw new PdfFormatException("The PDF to append has no pages.");
        }

        foreach (PdfPageNode page in pages)
        {
            slots.Add(PageSlot.Existing(page, source, isOriginal: false));
        }
    }

    /// <summary>
    /// The original bytes followed by one incremental update holding everything drawn and added.
    /// When nothing was, the original bytes come back unchanged.
    /// </summary>
    public byte[] Save()
    {
        bool appends = slots.Count > originalPageCount;
        bool overlays = slots.Any(slot => slot.IsOriginal && HasDrawing(slot));

        if (!appends && !overlays)
        {
            return (byte[])file.Data.Clone();
        }

        var update = new IncrementalUpdate(file);
        var session = new SaveSession(update);

        if (slots.Any(slot => slot.Drawing is not null))
        {
            // Read back exactly as any other PDF would be, which is also a running check that
            // what the writer produces can be parsed.
            session.ScratchFile = PdfFile.Open(scratch.ToArray());
            session.ScratchPages = PageTree.Read(session.ScratchFile);
        }

        PdfReference? pagesRoot = null;
        var appended = new List<PdfReference>();

        if (appends)
        {
            pagesRoot = file.Catalog["Pages"] as PdfReference ?? throw new PdfFormatException(
                "Pages cannot be appended: the document's page tree root is not an indirect object.");
        }

        foreach (PageSlot slot in slots)
        {
            if (slot.IsBlank)
            {
                PdfPageNode node = session.ScratchPages[slot.ScratchIndex];

                appended.Add(AppendPage(session, session.ScratchImporter, node, pagesRoot!, overlay: null));

                continue;
            }

            PdfReference? overlay = HasDrawing(slot) ? CreateOverlay(session, slot) : null;
            PdfPageNode page = slot.Node!;

            if (slot.IsOriginal)
            {
                if (overlay is null)
                {
                    continue;
                }

                PdfReference reference = page.Reference ?? throw new PdfFormatException(
                    $"Page {slots.IndexOf(slot)} cannot be drawn on: it is not an indirect object.");

                // A copy of the page with every entry it had, so /Parent, /Annots and the rest are
                // untouched, and only its resources and contents changed.
                PdfDictionary rewritten = CopyOf(page.Dictionary);

                Compose(session, rewritten, page, file, value => value, overlay);
                update.Replace(reference, rewritten);

                continue;
            }

            if (!session.Importers.TryGetValue(slot.Source!, out ObjectImporter? importer))
            {
                importer = new ObjectImporter(slot.Source!, update);
                session.Importers[slot.Source!] = importer;
            }

            appended.Add(AppendPage(session, importer, page, pagesRoot!, overlay));
        }

        if (appended.Count > 0)
        {
            PdfDictionary root = CopyOf(file.Resolve(pagesRoot) as PdfDictionary);
            var kids = new PdfArray(file.Resolve(root["Kids"]) as PdfArray ?? new PdfArray());

            foreach (PdfReference reference in appended)
            {
                kids.Add(reference);
            }

            root.Set("Kids", kids);
            root.Set("Count", originalPageCount + appended.Count);
            update.Replace(pagesRoot!, root);
        }

        return update.Write();
    }

    /// <summary>
    /// Writes a page brought in from another file - an appended document, or the scratch document
    /// for a page made by <see cref="AddPage(double, double)"/>.
    /// </summary>
    private static PdfReference AppendPage(
        SaveSession session,
        ObjectImporter importer,
        PdfPageNode node,
        PdfReference parent,
        PdfReference? overlay)
    {
        // Everything inheritable is set explicitly, so nothing on the root of this document's
        // page tree - a /Rotate, a shared /Resources - can leak onto the page and change it.
        var page = new PdfDictionary()
            .Set("Type", PdfName.PageType)
            .Set("Parent", parent)
            .Set("MediaBox", PdfArray.Of(node.MediaBox))
            .Set("CropBox", PdfArray.Of(node.CropBox))
            .Set("Rotate", node.Rotate);

        // Entries that describe how the page renders come along. Annotations do not: their
        // destinations and form fields belong to the other document.
        foreach (string key in new[] { "Group", "UserUnit", "BleedBox", "TrimBox", "ArtBox" })
        {
            if (node.Dictionary[key] is PdfObject value)
            {
                page.Set(key, importer.Import(value));
            }
        }

        Compose(session, page, node, importer.Source, importer.Import, overlay);

        return session.Update.Add(page);
    }

    /// <summary>
    /// Gives a page its resources and contents, wrapping the existing contents so the overlay is
    /// drawn on top of them in a clean graphics state.
    /// </summary>
    /// <param name="bring">
    /// Turns a value from <paramref name="source"/> into one valid in the update: unchanged for the
    /// document's own pages, an import for pages from another file.
    /// </param>
    private static void Compose(
        SaveSession session,
        PdfDictionary page,
        PdfPageNode node,
        PdfFile source,
        Func<PdfObject, PdfObject> bring,
        PdfReference? overlay)
    {
        // The page always gets its own direct resource dictionary, copied from whatever it used -
        // its own, inherited from an ancestor, or a dictionary shared with other pages. A shared
        // dictionary is never changed in place, because every page using it would change too.
        PdfDictionary resources = CopyOf(source.Resolve(node.Resources) as PdfDictionary);
        PdfDictionary? xobjects = null;

        if (overlay is not null)
        {
            xobjects = CopyOf(source.Resolve(resources["XObject"]) as PdfDictionary);
            resources.Set("XObject", xobjects);
        }

        var brought = (PdfDictionary)bring(resources);
        List<PdfObject> parts = ContentParts(node.Dictionary["Contents"], source).Select(bring).ToList();

        page.Set("Resources", brought);

        if (overlay is null)
        {
            if (parts.Count > 0)
            {
                page.Set("Contents", parts.Count == 1 ? parts[0] : new PdfArray(parts));
            }
            else
            {
                page["Contents"] = null;
            }

            return;
        }

        string name = UniqueName(xobjects!, "TheoisticOverlay");

        ((PdfDictionary)brought["XObject"]!).Set(name, overlay);

        // q ... Q around the existing contents restores whatever state they leave behind - a
        // transform, a clip, a colour - so the overlay starts from the page's initial state. The
        // matrix then maps the displayed page, which is what the caller drew on, onto user space.
        PdfMatrix matrix = node.DisplayToUser;
        string transform = string.Join(' ', new[] { matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F }.Select(PdfWriter.FormatReal));
        string overlayOperators = $"\nQ\nq\n{transform} cm\n/{name} Do\nQ\n";

        var contents = new PdfArray();

        contents.Add(session.SaveState());

        foreach (PdfObject part in parts)
        {
            contents.Add(part);
        }

        contents.Add(session.Update.Add(new PdfStream(Encoding.ASCII.GetBytes(overlayOperators), compress: false)));

        page.Set("Contents", contents);
    }

    /// <summary>
    /// Turns one drawn scratch page into a Form XObject in the update: the page's content stream,
    /// still compressed, with its fonts and images copied across and renumbered.
    /// </summary>
    private static PdfReference CreateOverlay(SaveSession session, PageSlot slot)
    {
        PdfPageNode node = session.ScratchPages[slot.ScratchIndex];
        PdfFile scratchFile = session.ScratchFile!;

        if (scratchFile.Resolve(node.Dictionary["Contents"]) is not PdfStream content)
        {
            throw new InvalidOperationException("A drawn page has no content stream, which should be impossible.");
        }

        var form = new PdfStream(content.Data, compress: false);

        form.Set("Type", PdfName.XObject)
            .SetName("Subtype", "Form")
            .Set("BBox", PdfArray.Of(PdfRectangle.Page(node.MediaBox.Width, node.MediaBox.Height)))
            .Set("Resources", session.ScratchImporter.Import(scratchFile.Resolve(node.Resources) ?? new PdfDictionary()));

        foreach (string key in new[] { "Filter", "DecodeParms" })
        {
            if (content[key] is PdfObject value)
            {
                form.Set(key, value);
            }
        }

        return session.Update.Add(form);
    }

    private static bool HasDrawing(PageSlot slot) =>
        slot.Drawing is not null && slot.Drawing.Content.ToArray().Length > 0;

    /// <summary>The content streams of a page, which may be one stream or an array of them.</summary>
    private static List<PdfObject> ContentParts(PdfObject? contents, PdfFile source)
    {
        PdfObject? resolved = source.Resolve(contents);

        if (resolved is PdfArray array)
        {
            return array.Where(item => item is PdfReference).ToList();
        }

        return resolved is PdfStream && contents is PdfReference reference
            ? new List<PdfObject> { reference }
            : new List<PdfObject>();
    }

    private static string UniqueName(PdfDictionary dictionary, string prefix)
    {
        for (int n = 1; ; n++)
        {
            string name = prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!dictionary.ContainsKey(name))
            {
                return name;
            }
        }
    }

    /// <summary>A shallow copy: a new dictionary with the same entries, safe to change.</summary>
    internal static PdfDictionary CopyOf(PdfDictionary? dictionary)
    {
        var copy = new PdfDictionary();

        if (dictionary is not null)
        {
            foreach (KeyValuePair<string, PdfObject> entry in dictionary)
            {
                copy.Set(entry.Key, entry.Value);
            }
        }

        return copy;
    }

    /// <summary>A page as the editor tracks it: where it came from, and what has been drawn on it.</summary>
    private sealed class PageSlot
    {
        private PageSlot()
        {
        }

        /// <summary>The page as found in its file; null for a page made by AddPage.</summary>
        public PdfPageNode? Node { get; private init; }

        public PdfFile? Source { get; private init; }

        public bool IsOriginal { get; private init; }

        public bool IsBlank => Node is null;

        public double Width { get; private init; }

        public double Height { get; private init; }

        public int Rotation { get; private init; }

        /// <summary>What has been drawn on the page, if anything; for a blank page, the page itself.</summary>
        public PdfPage? Drawing { get; set; }

        public int ScratchIndex { get; set; } = -1;

        public static PageSlot Existing(PdfPageNode node, PdfFile source, bool isOriginal) => new PageSlot
        {
            Node = node,
            Source = source,
            IsOriginal = isOriginal,
            Width = node.DisplayWidth,
            Height = node.DisplayHeight,
            Rotation = node.Rotate,
        };

        public static PageSlot Blank(PdfPage page, int scratchIndex) => new PageSlot
        {
            Width = page.Width,
            Height = page.Height,
            Rotation = 0,
            Drawing = page,
            ScratchIndex = scratchIndex,
        };
    }

    /// <summary>State shared by everything written during one save.</summary>
    private sealed class SaveSession
    {
        private ObjectImporter? scratchImporter;
        private PdfReference? saveState;

        public SaveSession(IncrementalUpdate update) => Update = update;

        public IncrementalUpdate Update { get; }

        public PdfFile? ScratchFile { get; set; }

        public List<PdfPageNode> ScratchPages { get; set; } = new List<PdfPageNode>();

        /// <summary>One importer per source file, so a font used on several pages is copied once.</summary>
        public Dictionary<PdfFile, ObjectImporter> Importers { get; } = new Dictionary<PdfFile, ObjectImporter>();

        public ObjectImporter ScratchImporter => scratchImporter ??= new ObjectImporter(ScratchFile!, Update);

        /// <summary>A stream holding just <c>q</c>, shared by every page given an overlay.</summary>
        public PdfReference SaveState() =>
            saveState ??= Update.Add(new PdfStream("q\n"u8.ToArray(), compress: false));
    }
}

/// <summary>
/// A page's size as a viewer displays it, in points, and its rotation: 0, 90, 180 or 270 degrees
/// clockwise.
/// </summary>
public sealed record PdfPageInfo(int Index, double Width, double Height, int Rotation);
