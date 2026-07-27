using System.Runtime.InteropServices;
using System.Text;

namespace Theoistic.PDF;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void VoidCallback(IntPtr converter);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void StringCallback(IntPtr converter, [MarshalAs(UnmanagedType.LPStr)] string str);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void IntCallback(IntPtr converter, int integer);

/// <summary>
/// Thin managed wrapper over the native wkhtmltox API.
/// <para>
/// wkhtmltox is not thread safe: every member of this type must be called from the single
/// thread that called <see cref="Load"/>, and <see cref="Dispose"/> must run on that same
/// thread. <see cref="ThreadSafeHTMLToPDFConverter"/> owns that thread affinity.
/// </para>
/// </summary>
internal sealed class PdfTools : IDisposable
{
    public bool IsLoaded { get; private set; }

    private bool disposed;

    public void Load()
    {
        if (IsLoaded)
        {
            return;
        }

        if (WkHtmlToXBindings.wkhtmltopdf_init(0) != 1)
        {
            throw new InvalidOperationException(
                "wkhtmltopdf_init failed. The native libwkhtmltox library could not be initialised.");
        }

        IsLoaded = true;
    }

    public bool ExtendedQt() => WkHtmlToXBindings.wkhtmltopdf_extended_qt() == 1;

    public string GetLibraryVersion() =>
        Marshal.PtrToStringAnsi(WkHtmlToXBindings.wkhtmltopdf_version()) ?? string.Empty;

    public IntPtr CreateGlobalSettings() => WkHtmlToXBindings.wkhtmltopdf_create_global_settings();

    public int SetGlobalSetting(IntPtr settings, string name, string? value) =>
        WkHtmlToXBindings.wkhtmltopdf_set_global_setting(settings, name, value);

    public unsafe string GetGlobalSetting(IntPtr settings, string name)
    {
        //default const char * size is 2048 bytes
        byte[] buffer = new byte[2048];

        fixed (byte* tempBuffer = buffer)
        {
            WkHtmlToXBindings.wkhtmltopdf_get_global_setting(settings, name, tempBuffer, buffer.Length);
        }

        return GetString(buffer);
    }

    public void DestroyGlobalSetting(IntPtr settings)
    {
        if (settings != IntPtr.Zero)
        {
            WkHtmlToXBindings.wkhtmltopdf_destroy_global_settings(settings);
        }
    }

    public IntPtr CreateObjectSettings() => WkHtmlToXBindings.wkhtmltopdf_create_object_settings();

    public int SetObjectSetting(IntPtr settings, string name, string? value) =>
        WkHtmlToXBindings.wkhtmltopdf_set_object_setting(settings, name, value);

    public unsafe string GetObjectSetting(IntPtr settings, string name)
    {
        //default const char * size is 2048 bytes
        byte[] buffer = new byte[2048];

        fixed (byte* tempBuffer = buffer)
        {
            WkHtmlToXBindings.wkhtmltopdf_get_object_setting(settings, name, tempBuffer, buffer.Length);
        }

        return GetString(buffer);
    }

    public void DestroyObjectSetting(IntPtr settings)
    {
        if (settings != IntPtr.Zero)
        {
            WkHtmlToXBindings.wkhtmltopdf_destroy_object_settings(settings);
        }
    }

    public IntPtr CreateConverter(IntPtr globalSettings) =>
        WkHtmlToXBindings.wkhtmltopdf_create_converter(globalSettings);

    public void AddObject(IntPtr converter, IntPtr objectSettings, byte[] data) =>
        WkHtmlToXBindings.wkhtmltopdf_add_object(converter, objectSettings, data);

    public void AddObject(IntPtr converter, IntPtr objectSettings, string data) =>
        WkHtmlToXBindings.wkhtmltopdf_add_object(converter, objectSettings, data);

    public bool DoConversion(IntPtr converter) =>
        WkHtmlToXBindings.wkhtmltopdf_convert(converter) != 0;

    public void DestroyConverter(IntPtr converter)
    {
        if (converter != IntPtr.Zero)
        {
            WkHtmlToXBindings.wkhtmltopdf_destroy_converter(converter);
        }
    }

    public byte[] GetConversionResult(IntPtr converter)
    {
        int length = WkHtmlToXBindings.wkhtmltopdf_get_output(converter, out IntPtr resultPointer);

        if (length <= 0 || resultPointer == IntPtr.Zero)
        {
            return Array.Empty<byte>();
        }

        var result = new byte[length];
        Marshal.Copy(resultPointer, result, 0, length);

        return result;
    }

    public int GetHttpErrorCode(IntPtr converter) =>
        WkHtmlToXBindings.wkhtmltopdf_http_error_code(converter);

    public int SetPhaseChangedCallback(IntPtr converter, VoidCallback callback) =>
        WkHtmlToXBindings.wkhtmltopdf_set_phase_changed_callback(converter, callback);

    public int SetProgressChangedCallback(IntPtr converter, VoidCallback callback) =>
        WkHtmlToXBindings.wkhtmltopdf_set_progress_changed_callback(converter, callback);

    public int SetFinishedCallback(IntPtr converter, IntCallback callback) =>
        WkHtmlToXBindings.wkhtmltopdf_set_finished_callback(converter, callback);

    public int SetWarningCallback(IntPtr converter, StringCallback callback) =>
        WkHtmlToXBindings.wkhtmltopdf_set_warning_callback(converter, callback);

    public int SetErrorCallback(IntPtr converter, StringCallback callback) =>
        WkHtmlToXBindings.wkhtmltopdf_set_error_callback(converter, callback);

    public int GetPhaseCount(IntPtr converter) => WkHtmlToXBindings.wkhtmltopdf_phase_count(converter);

    public int GetCurrentPhase(IntPtr converter) => WkHtmlToXBindings.wkhtmltopdf_current_phase(converter);

    public string GetPhaseDescription(IntPtr converter, int phase) =>
        Marshal.PtrToStringAnsi(WkHtmlToXBindings.wkhtmltopdf_phase_description(converter, phase)) ?? string.Empty;

    public string GetProgressString(IntPtr converter) =>
        Marshal.PtrToStringAnsi(WkHtmlToXBindings.wkhtmltopdf_progress_string(converter)) ?? string.Empty;

    /// <summary>
    /// Releases the native library. Must be called on the same thread that called <see cref="Load"/>;
    /// there is deliberately no finalizer, because tearing wkhtmltox down from the finalizer thread
    /// corrupts its Qt state and takes the process with it.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        if (IsLoaded)
        {
            IsLoaded = false;
            WkHtmlToXBindings.wkhtmltopdf_deinit();
        }
    }

    private static string GetString(byte[] buffer)
    {
        int walk = 0;

        while (walk < buffer.Length && buffer[walk] != 0)
        {
            walk++;
        }

        return Encoding.UTF8.GetString(buffer, 0, walk);
    }
}

internal unsafe static class WkHtmlToXBindings
{
    const string DLLNAME = "libwkhtmltox";

    // Every wkhtmltox export is __cdecl. Leaving CallingConvention at its default (Winapi ==
    // __stdcall on Windows) corrupts the stack on x86, so it is set explicitly on every import.
    // ExactSpelling skips the A/W entry-point probing that would otherwise happen on each bind.
    const CallingConvention CONV = CallingConvention.Cdecl;

    #region HTML to PDF bindings

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_extended_qt();

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern IntPtr wkhtmltopdf_version();

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_init(int useGraphics);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_deinit();

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern IntPtr wkhtmltopdf_create_global_settings();

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_global_setting(IntPtr settings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? value);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static unsafe extern int wkhtmltopdf_get_global_setting(IntPtr settings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte* value, int valueSize);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_destroy_global_settings(IntPtr settings);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern IntPtr wkhtmltopdf_create_object_settings();

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_object_setting(IntPtr settings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? value);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static unsafe extern int wkhtmltopdf_get_object_setting(IntPtr settings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte* value, int valueSize);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_destroy_object_settings(IntPtr settings);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern IntPtr wkhtmltopdf_create_converter(IntPtr globalSettings);

    // data is read as a NUL terminated const char *, so the caller must supply the terminator.
    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern void wkhtmltopdf_add_object(IntPtr converter,
        IntPtr objectSettings,
        byte[] data);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern void wkhtmltopdf_add_object(IntPtr converter,
        IntPtr objectSettings,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_convert(IntPtr converter);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern void wkhtmltopdf_destroy_converter(IntPtr converter);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_get_output(IntPtr converter, out IntPtr data);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_phase_changed_callback(IntPtr converter, [MarshalAs(UnmanagedType.FunctionPtr)] VoidCallback callback);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_progress_changed_callback(IntPtr converter, [MarshalAs(UnmanagedType.FunctionPtr)] VoidCallback callback);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_finished_callback(IntPtr converter, [MarshalAs(UnmanagedType.FunctionPtr)] IntCallback callback);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_warning_callback(IntPtr converter, [MarshalAs(UnmanagedType.FunctionPtr)] StringCallback callback);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_set_error_callback(IntPtr converter, [MarshalAs(UnmanagedType.FunctionPtr)] StringCallback callback);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_phase_count(IntPtr converter);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_current_phase(IntPtr converter);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern IntPtr wkhtmltopdf_phase_description(IntPtr converter, int phase);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern IntPtr wkhtmltopdf_progress_string(IntPtr converter);

    [DllImport(DLLNAME, CallingConvention = CONV, ExactSpelling = true)]
    public static extern int wkhtmltopdf_http_error_code(IntPtr converter);

    #endregion
}
