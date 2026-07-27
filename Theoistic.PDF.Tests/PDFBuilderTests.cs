using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Theoistic.PDF.Tests;

/// <summary>
/// End to end coverage over the native wkhtmltox path: P/Invoke signatures, UTF-8 marshalling,
/// settings reflection and - most importantly - the lifetime of the conversion callbacks.
/// </summary>
[TestClass]
public sealed class PDFBuilderTests
{
    private const string Html = """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>t</title></head>
        <body><h1>Hello</h1><p>Some content to lay out.</p></body></html>
        """;

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        var services = new ServiceCollection();
        services.AddTheoisticPDF();
        services.BuildServiceProvider().UseTheoisticPDF();
    }

    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.IsTrue(bytes.Length > 0, "conversion produced no bytes");
        Assert.AreEqual("%PDF", Encoding.ASCII.GetString(bytes, 0, 4), "output is not a PDF");
    }

    private static async Task<TException> AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException expected)
        {
            return expected;
        }
        catch (Exception unexpected)
        {
            Assert.Fail($"Expected {typeof(TException).Name} but got {unexpected.GetType().Name}: {unexpected.Message}");
        }

        Assert.Fail($"Expected {typeof(TException).Name} but no exception was thrown.");
        return null!;
    }

    [TestMethod]
    public async Task BuildAsync_ProducesAPdf()
    {
        using var builder = new PDFBuilder();

        AssertIsPdf(await builder.BuildAsync(Html));
    }

    /// <summary>
    /// Guards the fix for "A callback was made on a garbage collected delegate of type
    /// 'Theoistic.PDF!Theoistic.PDF.VoidCallback::Invoke'". wkhtmltox holds the raw thunks for the
    /// whole conversion, so the delegates must be rooted for at least that long. Passing the method
    /// groups at the call site instead only survives while tier 0 codegen happens to keep the
    /// temporaries alive in the frame - once the method is re-jitted at tier 1 the delegates become
    /// collectible mid-conversion and the process is torn down by FailFast.
    /// <para>
    /// The stress test below is a smoke test, not a deterministic reproduction: reproducing on
    /// demand needs DOTNET_TieredCompilation=0 as well. This one pins the invariant directly.
    /// </para>
    /// </summary>
    [TestMethod]
    public void Converter_RootsItsNativeCallbacksForItsWholeLifetime()
    {
        var converterType = typeof(PDFBuilder).Assembly
            .GetType("Theoistic.PDF.ThreadSafeHTMLToPDFConverter", throwOnError: true)!;

        // Only the interop delegates matter here; the event backing fields are delegates too.
        var delegateFields = converterType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.FieldType.IsDefined(typeof(UnmanagedFunctionPointerAttribute), inherit: false))
            .ToArray();

        Assert.AreEqual(5, delegateFields.Length,
            "each wkhtmltox callback needs a field holding it alive; found: " +
            string.Join(", ", delegateFields.Select(f => f.Name)));

        var converter = Activator.CreateInstance(converterType, nonPublic: true)!;

        foreach (var field in delegateFields)
        {
            Assert.IsNotNull(field.GetValue(converter),
                $"{field.Name} must be assigned in the constructor, not at the registration call site");
        }
    }

    /// <summary>
    /// Runs real conversions while a second thread forces collections, so the callbacks have to
    /// survive a GC that lands mid-conversion.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_SurvivesGarbageCollectionDuringConversion()
    {
        using var pressure = new CancellationTokenSource();

        var allocator = Task.Run(() =>
        {
            while (!pressure.IsCancellationRequested)
            {
                for (int i = 0; i < 200; i++)
                {
                    _ = new byte[85_000]; // straight onto the LOH to provoke gen2 collections
                }

                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            }
        });

        try
        {
            for (int i = 0; i < 15; i++)
            {
                using var builder = new PDFBuilder();

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                AssertIsPdf(await builder.BuildAsync($"{Html}<!-- {i} -->"));
            }
        }
        finally
        {
            pressure.Cancel();
            await allocator;
        }
    }

    /// <summary>
    /// Conversions are serialised onto one native thread; concurrent callers must all get their
    /// own result back rather than deadlocking or crossing streams.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_HandlesConcurrentCallers()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var builder = new PDFBuilder();
            return await builder.BuildAsync($"<html><body><h1>Document {i}</h1></body></html>");
        }));

        foreach (var result in results)
        {
            AssertIsPdf(result);
        }
    }

    /// <summary>
    /// Content is handed to wkhtmltox as a NUL terminated UTF-8 buffer; a missing terminator or a
    /// wrong encoding shows up here as truncated or mangled output.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_HandlesNonAsciiContent()
    {
        using var builder = new PDFBuilder();

        var html = "<html><head><meta charset=\"utf-8\"></head><body><p>æøå ünïcode 日本語 — ✓</p></body></html>";

        AssertIsPdf(await builder.BuildAsync(html));
    }

    [TestMethod]
    public async Task BuildAsync_AppliesSettingsWithoutLosingDefaults()
    {
        using var builder = new PDFBuilder();

        builder.Settings(s =>
        {
            s.DPI = 300;
            s.Margins = new MarginSettings(10, 10, 10, 10);
        });

        var pdf = await builder.BuildAsync(Html, o =>
        {
            o.LoadSettings.LoadErrorHandling = ContentErrorHandling.Ignore;
            o.WebSettings.PrintMediaType = true;
        });

        AssertIsPdf(pdf);
    }

    /// <summary>
    /// A conversion that wkhtmltox refuses used to come back as a zero byte array that callers
    /// happily wrote out as a corrupt "PDF".
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_ThrowsWithNativeDiagnosticsWhenConversionFails()
    {
        using var builder = new PDFBuilder();

        builder.Settings(s => s.Out = Path.Combine("Z:", "definitely", "not", "here", "out.pdf"));

        var ex = await AssertThrowsAsync<PdfConversionException>(
            async () => await builder.BuildAsync(Html));

        Assert.IsTrue(ex.Errors.Count > 0, "the native error callback output should be captured");
        StringAssert.Contains(ex.Message, "Unable to write to destination");
    }

    /// <summary>
    /// wkhtmltox only recognises the lower case CLI spellings of loadErrorHandling; given the enum
    /// name verbatim it rejects the value and silently falls back to abort. Empty content is a load
    /// failure, so the two halves below only diverge when the value actually gets through.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_HonoursIgnoreLoadErrorHandling()
    {
        using (var aborting = new PDFBuilder())
        {
            await AssertThrowsAsync<PdfConversionException>(async () => await aborting.BuildAsync(""));
        }

        using var ignoring = new PDFBuilder();

        var pdf = await ignoring.BuildAsync("", o => o.LoadSettings.LoadErrorHandling = ContentErrorHandling.Ignore);

        AssertIsPdf(pdf);
    }

    /// <summary>
    /// With GlobalSettings.Out set wkhtmltox writes the file itself and hands back an empty
    /// buffer, which must not be mistaken for a failure.
    /// </summary>
    [TestMethod]
    public async Task BuildAsync_WritesToFileWhenOutIsSet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"theoistic-{Guid.NewGuid():N}.pdf");

        try
        {
            using var builder = new PDFBuilder();

            builder.Settings(s => s.Out = path);

            var result = await builder.BuildAsync(Html);

            Assert.AreEqual(0, result.Length, "output went to the file, not the buffer");
            Assert.IsTrue(File.Exists(path), "the PDF should have been written to disk");
            AssertIsPdf(await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public async Task BuildAsync_RejectsNullHtml()
    {
        using var builder = new PDFBuilder();

        await AssertThrowsAsync<ArgumentNullException>(async () => await builder.BuildAsync(null!));
    }

    [TestMethod]
    public async Task BuildAsync_CancelsWhileQueued()
    {
        using var builder = new PDFBuilder();
        using var cts = new CancellationTokenSource();

        cts.Cancel();

        await AssertThrowsAsync<OperationCanceledException>(
            async () => await builder.BuildAsync(Html, null, cts.Token));
    }
}
