using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace Theoistic.PDF;

/// <summary>
/// Marshals every wkhtmltox call onto one dedicated thread, because the native library keeps
/// global Qt state and is not thread safe.
/// </summary>
internal sealed class ThreadSafeHTMLToPDFConverter : IDisposable
{
    private readonly PdfTools Tools = new PdfTools();
    private readonly BlockingCollection<ConversionRequest> conversions = new BlockingCollection<ConversionRequest>();
    private readonly object startLock = new object();

    private Thread? conversionThread;
    private int disposed;

    // wkhtmltox stores the raw function pointers for the whole lifetime of a converter and calls
    // them from inside wkhtmltopdf_convert. Passing the method groups directly at the call site
    // would create throw-away delegates that the GC is free to collect while native code still
    // holds their thunks, which the runtime reports as
    // "A callback was made on a garbage collected delegate" and turns into a FailFast.
    // Holding them in fields for the lifetime of this instance is what keeps the thunks alive.
    private readonly VoidCallback phaseChangedCallback;
    private readonly VoidCallback progressChangedCallback;
    private readonly IntCallback finishedCallback;
    private readonly StringCallback warningCallback;
    private readonly StringCallback errorCallback;

    /// <summary>State of the conversion running on the worker thread; only touched from that thread.</summary>
    private ConversionState? currentState;

    public IDocument? ProcessingDocument => currentState?.Document;

    public event EventHandler<PhaseChangedArgs>? PhaseChanged;
    public event EventHandler<ProgressChangedArgs>? ProgressChanged;
    public event EventHandler<FinishedArgs>? Finished;
    public event EventHandler<ErrorArgs>? Error;
    public event EventHandler<WarningArgs>? Warning;

    public ThreadSafeHTMLToPDFConverter()
    {
        phaseChangedCallback = OnPhaseChanged;
        progressChangedCallback = OnProgressChanged;
        finishedCallback = OnFinished;
        warningCallback = OnWarning;
        errorCallback = OnError;
    }

    public byte[] Convert(IDocument document) => ConvertAsync(document).GetAwaiter().GetResult();

    /// <summary>
    /// Queues a conversion on the wkhtmltox thread. <paramref name="cancellationToken"/> can only
    /// drop the request while it is still queued: a conversion that has reached native code cannot
    /// be interrupted.
    /// </summary>
    public Task<byte[]> ConvertAsync(IDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<byte[]>(cancellationToken);
        }

        if (!document.GetObjects().Any())
        {
            throw new ArgumentException(
                "No objects is defined in document that was passed. At least one object must be defined.",
                nameof(document));
        }

        StartThread();

        var request = new ConversionRequest(document, cancellationToken);

        try
        {
            conversions.Add(request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(ThreadSafeHTMLToPDFConverter), ex);
        }

        return request.Completion.Task;
    }

    private byte[] ConvertDocument(IDocument document)
    {
        var state = new ConversionState(document);
        currentState = state;

        Tools.Load();

        IntPtr converter = IntPtr.Zero;

        try
        {
            converter = CreateConverter(document);

            //register events - the delegates are instance fields, see the note on their declaration
            Tools.SetPhaseChangedCallback(converter, phaseChangedCallback);
            Tools.SetProgressChangedCallback(converter, progressChangedCallback);
            Tools.SetFinishedCallback(converter, finishedCallback);
            Tools.SetWarningCallback(converter, warningCallback);
            Tools.SetErrorCallback(converter, errorCallback);

            bool converted = Tools.DoConversion(converter);

            if (!converted)
            {
                throw new PdfConversionException(
                    BuildFailureMessage(state, Tools.GetHttpErrorCode(converter)),
                    state.Errors,
                    state.Warnings);
            }

            byte[] result = Tools.GetConversionResult(converter);

            // With GlobalSettings.Out set, wkhtmltox writes the file itself and leaves the output
            // buffer empty, so only an unrequested empty buffer means something went wrong.
            if (result.Length == 0 && !state.WritesToFile)
            {
                throw new PdfConversionException(
                    BuildFailureMessage(state, Tools.GetHttpErrorCode(converter)),
                    state.Errors,
                    state.Warnings);
            }

            return result;
        }
        finally
        {
            // Always released, including when CreateConverter or the conversion itself threw,
            // otherwise every failure leaks the native converter and its settings.
            Tools.DestroyConverter(converter);
            currentState = null;
        }
    }

    private static string BuildFailureMessage(ConversionState state, int httpErrorCode)
    {
        var message = "wkhtmltopdf failed to convert the document.";

        if (httpErrorCode != 0)
        {
            message += $" HTTP error code: {httpErrorCode}.";
        }

        if (state.Errors.Count > 0)
        {
            message += " Errors: " + string.Join("; ", state.Errors) + ".";
        }

        if (state.Warnings.Count > 0)
        {
            message += " Warnings: " + string.Join("; ", state.Warnings) + ".";
        }

        if (state.RejectedSettings.Count > 0)
        {
            message += " Settings rejected by wkhtmltox: " + string.Join("; ", state.RejectedSettings) + ".";
        }

        return message;
    }

    // Native code calls the five methods below. An exception escaping into a native frame is
    // undefined behaviour, so each one swallows everything a handler may throw.

    private void OnPhaseChanged(IntPtr converter)
    {
        try
        {
            var handler = PhaseChanged;
            if (handler == null)
            {
                return;
            }

            int currentPhase = Tools.GetCurrentPhase(converter);

            handler(this, new PhaseChangedArgs
            {
                Document = currentState?.Document,
                PhaseCount = Tools.GetPhaseCount(converter),
                CurrentPhase = currentPhase,
                Description = Tools.GetPhaseDescription(converter, currentPhase)
            });
        }
        catch
        {
            // never let a managed exception unwind into wkhtmltox
        }
    }

    private void OnProgressChanged(IntPtr converter)
    {
        try
        {
            var handler = ProgressChanged;
            if (handler == null)
            {
                return;
            }

            handler(this, new ProgressChangedArgs
            {
                Document = currentState?.Document,
                Description = Tools.GetProgressString(converter)
            });
        }
        catch
        {
        }
    }

    private void OnFinished(IntPtr converter, int success)
    {
        try
        {
            Finished?.Invoke(this, new FinishedArgs
            {
                Document = currentState?.Document,
                Success = success == 1
            });
        }
        catch
        {
        }
    }

    private void OnError(IntPtr converter, string message)
    {
        try
        {
            currentState?.Errors.Add(message);

            Error?.Invoke(this, new ErrorArgs
            {
                Document = currentState?.Document,
                Message = message
            });
        }
        catch
        {
        }
    }

    private void OnWarning(IntPtr converter, string message)
    {
        try
        {
            currentState?.Warnings.Add(message);

            Warning?.Invoke(this, new WarningArgs
            {
                Document = currentState?.Document,
                Message = message
            });
        }
        catch
        {
        }
    }

    private IntPtr CreateConverter(IDocument document)
    {
        IntPtr globalSettings = Tools.CreateGlobalSettings();
        IntPtr converter;

        try
        {
            ApplyConfig(globalSettings, document, true);

            // wkhtmltopdf_create_converter takes ownership of the global settings.
            converter = Tools.CreateConverter(globalSettings);
            globalSettings = IntPtr.Zero;
        }
        catch
        {
            Tools.DestroyGlobalSetting(globalSettings);
            throw;
        }

        if (converter == IntPtr.Zero)
        {
            throw new PdfConversionException("wkhtmltopdf_create_converter returned a null converter.");
        }

        try
        {
            foreach (var obj in document.GetObjects())
            {
                if (obj == null)
                {
                    continue;
                }

                IntPtr objectSettings = Tools.CreateObjectSettings();

                try
                {
                    ApplyConfig(objectSettings, obj, false);
                }
                catch
                {
                    Tools.DestroyObjectSetting(objectSettings);
                    throw;
                }

                // wkhtmltopdf_add_object takes ownership of the object settings.
                Tools.AddObject(converter, objectSettings, obj.GetContent());
            }
        }
        catch
        {
            Tools.DestroyConverter(converter);
            throw;
        }

        return converter;
    }

    private void ApplyConfig(IntPtr config, ISettings? settings, bool isGlobal)
    {
        if (settings == null)
        {
            return;
        }

        foreach (var accessor in SettingsMetadata.For(settings.GetType()))
        {
            object? propValue = accessor.Property.GetValue(settings);

            if (propValue == null)
            {
                continue;
            }

            if (accessor.WkHtmlName != null)
            {
                Apply(config, accessor.WkHtmlName, propValue, isGlobal);
            }
            else if (propValue is ISettings nested)
            {
                ApplyConfig(config, nested, isGlobal);
            }
        }
    }

    private void Apply(IntPtr config, string name, object value, bool isGlobal)
    {
        switch (value)
        {
            case bool boolValue:
                SetSetting(config, name, boolValue ? "true" : "false", isGlobal);
                break;

            case double doubleValue:
                SetSetting(config, name, doubleValue.ToString("0.##", CultureInfo.InvariantCulture), isGlobal);
                break;

            case string stringValue:
                SetSetting(config, name, stringValue, isGlobal);
                break;

            case Dictionary<string, string> dictionary:
                int index = 0;

                foreach (var pair in dictionary)
                {
                    if (pair.Key == null || pair.Value == null)
                    {
                        continue;
                    }

                    //https://github.com/wkhtmltopdf/wkhtmltopdf/blob/c754e38b074a75a51327df36c4a53f8962020510/src/lib/reflect.hh#L192
                    SetSetting(config, name + ".append", null, isGlobal);
                    SetSetting(config, string.Format(CultureInfo.InvariantCulture, "{0}[{1}]", name, index), pair.Key + "\n" + pair.Value, isGlobal);

                    index++;
                }
                break;

            // Numbers must not pick up the ambient culture's sign or digit shapes.
            case IFormattable formattable:
                SetSetting(config, name, formattable.ToString(null, CultureInfo.InvariantCulture), isGlobal);
                break;

            default:
                SetSetting(config, name, value.ToString(), isGlobal);
                break;
        }
    }

    private void SetSetting(IntPtr config, string name, string? value, bool isGlobal)
    {
        int applied;

        if (isGlobal)
        {
            if (name == "out" && !string.IsNullOrEmpty(value) && currentState != null)
            {
                currentState.WritesToFile = true;
            }

            applied = Tools.SetGlobalSetting(config, name, value);
        }
        else
        {
            applied = Tools.SetObjectSetting(config, name, value);
        }

        // wkhtmltox answers 0 for a setting name it does not know or a value it cannot parse, and
        // then just carries on with the default. Recording it turns a silently ignored option
        // into something the caller can actually see when the conversion misbehaves.
        if (applied != 1)
        {
            currentState?.RejectedSettings.Add($"{name}={value ?? "<null>"}");
        }
    }

    private void StartThread()
    {
        if (Volatile.Read(ref conversionThread) != null)
        {
            return;
        }

        lock (startLock)
        {
            if (conversionThread == null)
            {
                var thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "wkhtmltopdf worker thread"
                };

                thread.Start();

                Volatile.Write(ref conversionThread, thread);
            }
        }
    }

    private void Run()
    {
        try
        {
            foreach (var request in conversions.GetConsumingEnumerable())
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.CancellationToken);
                    continue;
                }

                try
                {
                    request.Completion.TrySetResult(ConvertDocument(request.Document));
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
        finally
        {
            // Nothing else may be waiting on these, and init/deinit have to happen on this thread.
            FailPendingRequests();

            try
            {
                Tools.Dispose();
            }
            catch
            {
            }
        }
    }

    private void FailPendingRequests()
    {
        while (conversions.TryTake(out var pending))
        {
            pending.Completion.TrySetException(
                new ObjectDisposedException(nameof(ThreadSafeHTMLToPDFConverter),
                    "The PDF converter was disposed before this conversion could run."));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        conversions.CompleteAdding();

        Thread? thread = Volatile.Read(ref conversionThread);

        if (thread == null)
        {
            // The worker never started, so nothing will deinit wkhtmltox or drain the queue.
            FailPendingRequests();
            conversions.Dispose();
            return;
        }

        // A conversion already inside native code cannot be interrupted. Long enough for one in
        // flight to finish, short enough not to dominate host shutdown; if it does outlive the
        // wait the queue is left alone rather than disposed underneath the worker, and the thread
        // is a background thread so it will not hold the process open.
        if (thread.Join(TimeSpan.FromSeconds(10)))
        {
            conversions.Dispose();
        }
    }

    private sealed class ConversionRequest
    {
        public ConversionRequest(IDocument document, CancellationToken cancellationToken)
        {
            Document = document;
            CancellationToken = cancellationToken;
        }

        public IDocument Document { get; }

        public CancellationToken CancellationToken { get; }

        // Continuations must not run inline on the wkhtmltox thread.
        public TaskCompletionSource<byte[]> Completion { get; } =
            new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ConversionState
    {
        public ConversionState(IDocument document) => Document = document;

        public IDocument Document { get; }

        public List<string> Errors { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Settings wkhtmltox refused, which it would otherwise ignore in silence.</summary>
        public List<string> RejectedSettings { get; } = new List<string>();

        /// <summary>Set when GlobalSettings.Out asked wkhtmltox to write the file itself.</summary>
        public bool WritesToFile { get; set; }
    }

    /// <summary>
    /// Per-type cache of the settings properties wkhtmltox cares about. Without it every
    /// conversion re-walks the reflection metadata of every settings object.
    /// </summary>
    private static class SettingsMetadata
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, SettingAccessor[]> cache = new();

        public static SettingAccessor[] For(Type type) => cache.GetOrAdd(type, Build);

        private static SettingAccessor[] Build(Type type)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var accessors = new List<SettingAccessor>();

            foreach (var prop in type.GetProperties(Flags))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                // Looking the attribute up by type is order independent; indexing into
                // GetCustomAttributes() would miss it whenever the compiler emits its own
                // attributes (for example [Nullable]) ahead of it.
                var attribute = prop.GetCustomAttribute<WkHtmlAttribute>(inherit: true);

                if (attribute != null)
                {
                    accessors.Add(new SettingAccessor(prop, attribute.Name));
                }
                else if (typeof(ISettings).IsAssignableFrom(prop.PropertyType))
                {
                    accessors.Add(new SettingAccessor(prop, null));
                }
            }

            return accessors.ToArray();
        }
    }

    private readonly struct SettingAccessor
    {
        public SettingAccessor(PropertyInfo property, string? wkHtmlName)
        {
            Property = property;
            WkHtmlName = wkHtmlName;
        }

        public PropertyInfo Property { get; }

        /// <summary>The wkhtmltox setting name, or <see langword="null"/> for a nested settings object.</summary>
        public string? WkHtmlName { get; }
    }
}
