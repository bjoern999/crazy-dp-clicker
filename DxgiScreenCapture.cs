using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TachoClicker;

internal readonly struct CaptureFrameResult
{
    public bool HasPixels { get; init; }
    public bool IsNewFrame { get; init; }
    public long FrameQpcTimestamp { get; init; }
    public double CaptureDurationMs { get; init; }
    public string Source { get; init; } // "DXGI" oder "DWM"
    public uint AccumulatedFrames { get; init; }
}

/// <summary>
/// Ultra-schnelle Sub-Millisekunden-Bildschirmerfassung via DXGI Desktop Duplication (reines COM P/Invoke, 0 NuGet-Pakete).
/// Kopiert auf der GPU ausschliesslich das benoetigte Rechteck (z. B. 180x180 = 129 KB statt 33 MB 4K-Desktop) in eine
/// Staging-Texture (< 0,5 ms) und liefert den exakten Hardware-GPU-Zeitstempel LastPresentTime (QPC-Ticks).
/// Faellt bei Bedarf (Display-Reset / UAC / fehlender Support) nahtlos auf GDI + DwmGetCompositionTimingInfo (qpcVBlank) zurueck.
/// </summary>
internal sealed unsafe class DxgiScreenCapture : IDisposable
{
    private const int S_OK = 0;
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    private const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    private const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);

    private const uint D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint D3D11_MAP_READ = 1;

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        uint driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out IntPtr ppDevice,
        out uint pFeatureLevel,
        out IntPtr ppImmediateContext);

    private void* _device;
    private void* _context;
    private void* _duplication;
    private void* _stagingTex;
    private int _stagingW;
    private int _stagingH;

    private int _outputLeft;
    private int _outputTop;
    private int _outputWidth;
    private int _outputHeight;
    private int _outputIndex = -1;

    private long _lastPresentQpc;
    private long _lastDwmVBlankQpc;
    private DateTime _nextInitAttemptUtc = DateTime.MinValue;
    private bool _disposed;

    // GDI+DWM Fallback-Ressourcen
    private Bitmap? _gdiBitmap;
    private Graphics? _gdiGraphics;
    private int _gdiW;
    private int _gdiH;

    public bool IsDxgiActive => _duplication != null;
    public string ActiveModeName => _duplication != null ? "DXGI" : "DWM";
    public string LastInitError { get; private set; } = "";
    public double LastCaptureDurationMs { get; private set; }
    public int OutputWidth => _outputWidth;
    public int OutputHeight => _outputHeight;

    public DxgiScreenCapture(int hintCenterX = 0, int hintCenterY = 0)
    {
        TryInitializeDxgi(hintCenterX, hintCenterY);
    }

    public bool TryInitializeDxgi(int hintX = 0, int hintY = 0)
    {
        if (_disposed) return false;
        if (DateTime.UtcNow < _nextInitAttemptUtc) return false;

        ReleaseDxgiResources();

        IntPtr factoryPtr = IntPtr.Zero;
        IntPtr devicePtr = IntPtr.Zero;
        IntPtr contextPtr = IntPtr.Zero;
        void* chosenAdapter = null;
        void* chosenOutput = null;
        void* fallbackAdapter = null;
        void* fallbackOutput = null;
        void* output1 = null;
        void* duplication = null;

        try
        {
            // Alle DXGI-Adapter & Monitore durchsuchen (funktioniert auch auf Hybrid-GPU-Systemen!)
            Guid iidFactory1 = IID_IDXGIFactory1;
            int hr = CreateDXGIFactory1(ref iidFactory1, out factoryPtr);
            if (hr != S_OK || factoryPtr == IntPtr.Zero)
            {
                LastInitError = $"CreateDXGIFactory1 failed: 0x{hr:X8}";
                _nextInitAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                return false;
            }

            void* factory = (void*)factoryPtr;
            DXGI_OUTPUT_DESC fallbackDesc = default;
            int fallbackIdx = -1;
            DXGI_OUTPUT_DESC chosenDesc = default;
            int chosenIdx = -1;

            for (uint a = 0; a < 8 && chosenOutput == null; a++)
            {
                void* adapterPtr = null;
                // IDXGIFactory1::EnumAdapters1 -> slot 12
                int ahr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)GetVtbl(factory)[12])(factory, a, &adapterPtr);
                if (ahr != S_OK || adapterPtr == null)
                    break;

                bool keepAdapter = false;
                for (uint i = 0; i < 8; i++)
                {
                    void* outPtr = null;
                    // IDXGIAdapter::EnumOutputs -> slot 7
                    int ehr = ((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)GetVtbl(adapterPtr)[7])(adapterPtr, i, &outPtr);
                    if (ehr != S_OK || outPtr == null)
                        break;

                    DXGI_OUTPUT_DESC desc = default;
                    // IDXGIOutput::GetDesc -> slot 7
                    int dhr = ((delegate* unmanaged[Stdcall]<void*, DXGI_OUTPUT_DESC*, int>)GetVtbl(outPtr)[7])(outPtr, &desc);
                    if (dhr == S_OK && desc.AttachedToDesktop != 0)
                    {
                        bool containsHint = hintX > 0 && hintY > 0 &&
                            hintX >= desc.DesktopLeft && hintX < desc.DesktopRight &&
                            hintY >= desc.DesktopTop && hintY < desc.DesktopBottom;

                        if (containsHint)
                        {
                            chosenOutput = outPtr;
                            chosenAdapter = adapterPtr;
                            chosenDesc = desc;
                            chosenIdx = (int)i;
                            outPtr = null;
                            keepAdapter = true;
                            break;
                        }

                        if (fallbackOutput == null)
                        {
                            fallbackOutput = outPtr;
                            fallbackAdapter = adapterPtr;
                            fallbackDesc = desc;
                            fallbackIdx = (int)i;
                            outPtr = null;
                            keepAdapter = true;
                        }
                    }

                    if (outPtr != null) ComRelease(outPtr);
                }

                if (!keepAdapter)
                    ComRelease(adapterPtr);
            }

            if (chosenOutput == null)
            {
                chosenOutput = fallbackOutput;
                chosenAdapter = fallbackAdapter;
                chosenDesc = fallbackDesc;
                chosenIdx = fallbackIdx;
                fallbackOutput = null;
                fallbackAdapter = null;
            }
            else
            {
                if (fallbackOutput != null) { ComRelease(fallbackOutput); fallbackOutput = null; }
                if (fallbackAdapter != null && fallbackAdapter != chosenAdapter) { ComRelease(fallbackAdapter); }
                fallbackAdapter = null;
            }

            if (chosenOutput == null || chosenAdapter == null)
            {
                LastInitError = "No attached DXGI output found";
                _nextInitAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                return false;
            }

            // D3D11-Device exakt auf dem Adapter erzeugen, an dem der Ziel-Monitor haengt
            hr = D3D11CreateDevice(
                (IntPtr)chosenAdapter,
                D3D_DRIVER_TYPE_UNKNOWN,
                IntPtr.Zero,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                IntPtr.Zero,
                0,
                D3D11_SDK_VERSION,
                out devicePtr,
                out _,
                out contextPtr);

            if (hr != S_OK || devicePtr == IntPtr.Zero || contextPtr == IntPtr.Zero)
            {
                // Fallback auf Default-Hardware-Adapter
                hr = D3D11CreateDevice(
                    IntPtr.Zero,
                    D3D_DRIVER_TYPE_HARDWARE,
                    IntPtr.Zero,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    IntPtr.Zero,
                    0,
                    D3D11_SDK_VERSION,
                    out devicePtr,
                    out _,
                    out contextPtr);
                if (hr != S_OK || devicePtr == IntPtr.Zero || contextPtr == IntPtr.Zero)
                {
                    LastInitError = $"D3D11CreateDevice failed: 0x{hr:X8}";
                    _nextInitAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                    return false;
                }
            }

            void* device = (void*)devicePtr;
            Guid iidOut1 = IID_IDXGIOutput1;
            hr = ComQueryInterface(chosenOutput, &iidOut1, &output1);
            if (hr != S_OK || output1 == null)
            {
                LastInitError = $"QI(IDXGIOutput1) failed: 0x{hr:X8}";
                _nextInitAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                return false;
            }

            // IDXGIOutput1::DuplicateOutput -> slot 22
            hr = ((delegate* unmanaged[Stdcall]<void*, void*, void**, int>)GetVtbl(output1)[22])(output1, device, &duplication);
            if (hr != S_OK || duplication == null)
            {
                LastInitError = $"DuplicateOutput failed: 0x{hr:X8}";
                _nextInitAttemptUtc = DateTime.UtcNow.AddSeconds(3);
                return false;
            }

            _device = device;
            _context = (void*)contextPtr;
            _duplication = duplication;
            _outputLeft = chosenDesc.DesktopLeft;
            _outputTop = chosenDesc.DesktopTop;
            _outputWidth = chosenDesc.DesktopRight - chosenDesc.DesktopLeft;
            _outputHeight = chosenDesc.DesktopBottom - chosenDesc.DesktopTop;
            _outputIndex = chosenIdx;
            _lastPresentQpc = 0;
            LastInitError = "";

            // Ownership uebertragen -> nicht im finally freigeben
            devicePtr = IntPtr.Zero;
            contextPtr = IntPtr.Zero;
            duplication = null;
            return true;
        }
        catch (Exception ex)
        {
            LastInitError = $"DXGI exception: {ex.Message}";
            _nextInitAttemptUtc = DateTime.UtcNow.AddSeconds(3);
            return false;
        }
        finally
        {
            if (duplication != null) ComRelease(duplication);
            if (output1 != null) ComRelease(output1);
            if (chosenOutput != null) ComRelease(chosenOutput);
            if (chosenAdapter != null) ComRelease(chosenAdapter);
            if (fallbackOutput != null) ComRelease(fallbackOutput);
            if (fallbackAdapter != null && fallbackAdapter != chosenAdapter) ComRelease(fallbackAdapter);
            if (factoryPtr != IntPtr.Zero) ComRelease((void*)factoryPtr);
            if (contextPtr != IntPtr.Zero) ComRelease((void*)contextPtr);
            if (devicePtr != IntPtr.Zero) ComRelease((void*)devicePtr);
        }
    }

    /// <summary>
    /// Liest ein Bildschirm-Rechteck (left, top, width, height) als dicht gepacktes BGRA32 (4 Bytes/Pixel) in destBgra.
    /// Bei aktivem DXGI wird auf das naechste GPU-Frame gewartet (max timeoutMs) und das Sub-Rechteck in < 0,5 ms kopiert.
    /// Wenn kein neues GPU-Frame vorliegt:
    ///   - allowGdiIfNoNewFrame=false -> gibt HasPixels=false, IsNewFrame=false zurueck (ideal waehrend Armed-Spin!).
    ///   - allowGdiIfNoNewFrame=true  -> liest einmalig via GDI+DWM (ideal fuer statische Initial-Pruefung).
    /// </summary>
    public CaptureFrameResult CaptureRegion(
        int left,
        int top,
        int width,
        int height,
        byte[] destBgra,
        int timeoutMs = 4,
        bool allowGdiIfNoNewFrame = false)
    {
        if (_disposed || width <= 0 || height <= 0 || destBgra.Length < width * height * 4)
            return default;

        // Falls Ziel auf anderem Monitor liegt oder DXGI nach Reset neu initialisiert werden soll
        if (_duplication == null)
        {
            TryInitializeDxgi(left + width / 2, top + height / 2);
        }
        else if (_outputWidth > 0 && _outputHeight > 0)
        {
            int cx = left + width / 2;
            int cy = top + height / 2;
            if (cx < _outputLeft || cx >= _outputLeft + _outputWidth ||
                cy < _outputTop || cy >= _outputTop + _outputHeight)
            {
                TryInitializeDxgi(cx, cy);
            }
        }

        if (_duplication != null)
        {
            long tStart = Stopwatch.GetTimestamp();
            bool needFallback = false;

            // Maximal 2 Versuche, falls der erste AcquireNextFrame nur eine reine Mausbewegung (LastPresentTime == 0) war
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint wait = attempt == 0 ? (uint)Math.Max(0, timeoutMs) : 0u;
                DXGI_OUTDUPL_FRAME_INFO frameInfo = default;
                void* desktopRes = null;

                // IDXGIOutputDuplication::AcquireNextFrame -> slot 8
                int hr = ((delegate* unmanaged[Stdcall]<void*, uint, DXGI_OUTDUPL_FRAME_INFO*, void**, int>)GetVtbl(_duplication)[8])(
                    _duplication, wait, &frameInfo, &desktopRes);

                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    if (allowGdiIfNoNewFrame)
                        return CaptureViaGdiDwm(left, top, width, height, destBgra);

                    return new CaptureFrameResult
                    {
                        HasPixels = false,
                        IsNewFrame = false,
                        FrameQpcTimestamp = _lastPresentQpc,
                        CaptureDurationMs = (Stopwatch.GetTimestamp() - tStart) * TicksToMs,
                        Source = "DXGI"
                    };
                }

                if (hr != S_OK || desktopRes == null)
                {
                    if (desktopRes != null) ComRelease(desktopRes);

                    if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_DEVICE_REMOVED ||
                        hr == DXGI_ERROR_DEVICE_RESET || hr == DXGI_ERROR_INVALID_CALL)
                    {
                        LastInitError = $"AcquireNextFrame lost: 0x{hr:X8}";
                        ReleaseDxgiResources();
                        _nextInitAttemptUtc = DateTime.UtcNow.AddMilliseconds(500);
                    }
                    needFallback = true;
                    break;
                }

                // Reines Maus-Update ohne neues Desktop-Bild -> sofort freigeben und nicht als Frame werten!
                if (frameInfo.LastPresentTime == 0)
                {
                    ComRelease(desktopRes);
                    // IDXGIOutputDuplication::ReleaseFrame -> slot 14
                    ((delegate* unmanaged[Stdcall]<void*, int>)GetVtbl(_duplication)[14])(_duplication);
                    continue;
                }

                bool copied = false;
                try
                {
                    if (EnsureStagingTexture(width, height))
                    {
                        void* desktopTex = null;
                        Guid iidTex = IID_ID3D11Texture2D;
                        int qhr = ComQueryInterface(desktopRes, &iidTex, &desktopTex);
                        if (qhr == S_OK && desktopTex != null)
                        {
                            try
                            {
                                int localLeft = Math.Clamp(left - _outputLeft, 0, Math.Max(0, _outputWidth - width));
                                int localTop = Math.Clamp(top - _outputTop, 0, Math.Max(0, _outputHeight - height));

                                var srcBox = new D3D11_BOX
                                {
                                    left = (uint)localLeft,
                                    top = (uint)localTop,
                                    front = 0,
                                    right = (uint)(localLeft + width),
                                    bottom = (uint)(localTop + height),
                                    back = 1
                                };

                                // ID3D11DeviceContext::CopySubresourceRegion -> slot 46
                                ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, uint, void*, uint, D3D11_BOX*, void>)GetVtbl(_context)[46])(
                                    _context, _stagingTex, 0, 0, 0, 0, desktopTex, 0, &srcBox);

                                D3D11_MAPPED_SUBRESOURCE mapped = default;
                                // ID3D11DeviceContext::Map -> slot 14
                                int mhr = ((delegate* unmanaged[Stdcall]<void*, void*, uint, uint, uint, D3D11_MAPPED_SUBRESOURCE*, int>)GetVtbl(_context)[14])(
                                    _context, _stagingTex, 0, D3D11_MAP_READ, 0, &mapped);

                                if (mhr == S_OK && mapped.pData != IntPtr.Zero)
                                {
                                    try
                                    {
                                        int rowBytes = width * 4;
                                        byte* srcBase = (byte*)mapped.pData;
                                        fixed (byte* dstBase = destBgra)
                                        {
                                            for (int y = 0; y < height; y++)
                                            {
                                                Buffer.MemoryCopy(
                                                    srcBase + (y * mapped.RowPitch),
                                                    dstBase + (y * rowBytes),
                                                    rowBytes,
                                                    rowBytes);
                                            }
                                        }
                                        copied = true;
                                    }
                                    finally
                                    {
                                        // ID3D11DeviceContext::Unmap -> slot 15
                                        ((delegate* unmanaged[Stdcall]<void*, void*, uint, void>)GetVtbl(_context)[15])(_context, _stagingTex, 0);
                                    }
                                }
                            }
                            finally
                            {
                                ComRelease(desktopTex);
                            }
                        }
                    }
                }
                finally
                {
                    ComRelease(desktopRes);
                    // IDXGIOutputDuplication::ReleaseFrame -> slot 14
                    ((delegate* unmanaged[Stdcall]<void*, int>)GetVtbl(_duplication)[14])(_duplication);
                }

                if (copied)
                {
                    bool isNew = frameInfo.LastPresentTime > _lastPresentQpc;
                    if (isNew)
                        _lastPresentQpc = frameInfo.LastPresentTime;

                    double durMs = (Stopwatch.GetTimestamp() - tStart) * TicksToMs;
                    LastCaptureDurationMs = durMs;

                    return new CaptureFrameResult
                    {
                        HasPixels = true,
                        IsNewFrame = isNew,
                        FrameQpcTimestamp = frameInfo.LastPresentTime,
                        CaptureDurationMs = durMs,
                        Source = "DXGI",
                        AccumulatedFrames = frameInfo.AccumulatedFrames
                    };
                }

                needFallback = true;
                break;
            }

            if (!needFallback)
            {
                if (allowGdiIfNoNewFrame)
                    return CaptureViaGdiDwm(left, top, width, height, destBgra);

                return new CaptureFrameResult
                {
                    HasPixels = false,
                    IsNewFrame = false,
                    FrameQpcTimestamp = _lastPresentQpc,
                    CaptureDurationMs = (Stopwatch.GetTimestamp() - tStart) * TicksToMs,
                    Source = "DXGI"
                };
            }
        }

        return CaptureViaGdiDwm(left, top, width, height, destBgra);
    }

    private CaptureFrameResult CaptureViaGdiDwm(int left, int top, int width, int height, byte[] destBgra)
    {
        long tStart = Stopwatch.GetTimestamp();
        EnsureGdiBitmap(width, height);

        try
        {
            _gdiGraphics!.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            return new CaptureFrameResult
            {
                HasPixels = false,
                IsNewFrame = false,
                CaptureDurationMs = (Stopwatch.GetTimestamp() - tStart) * TicksToMs,
                Source = "DWM"
            };
        }

        // Hardware-VBlank-Zeitstempel via DwmGetCompositionTimingInfo statt verrauschtem Post-GDI NowMs()!
        // Auf den juengsten vergangenen Hardware-VBlank (<= nowQpc) ausrichten -> identische Epoche wie DXGI LastPresentTime!
        long nowQpc = Stopwatch.GetTimestamp();
        long frameQpc;
        bool isNew;
        string src;
        if (NativeMethods.TryGetDwmTiming(out long qpcVBlank, out long qpcPeriod, out _, out _))
        {
            frameQpc = qpcVBlank;
            while (frameQpc > nowQpc)
                frameQpc -= qpcPeriod;
            while (nowQpc - frameQpc >= qpcPeriod)
                frameQpc += qpcPeriod;

            isNew = frameQpc > _lastDwmVBlankQpc;
            if (isNew)
                _lastDwmVBlankQpc = frameQpc;
            src = "DWM";
        }
        else
        {
            frameQpc = nowQpc;
            isNew = true;
            src = "GDI";
        }

        var rect = new Rectangle(0, 0, width, height);
        BitmapData data = _gdiBitmap!.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            byte* srcBase = (byte*)data.Scan0;
            fixed (byte* dstBase = destBgra)
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(
                        srcBase + (y * data.Stride),
                        dstBase + (y * rowBytes),
                        rowBytes,
                        rowBytes);
                }
            }
        }
        finally
        {
            _gdiBitmap.UnlockBits(data);
        }

        double durMs = (Stopwatch.GetTimestamp() - tStart) * TicksToMs;
        LastCaptureDurationMs = durMs;

        return new CaptureFrameResult
        {
            HasPixels = true,
            IsNewFrame = isNew,
            FrameQpcTimestamp = frameQpc,
            CaptureDurationMs = durMs,
            Source = src
        };
    }

    private bool EnsureStagingTexture(int width, int height)
    {
        if (_device == null) return false;
        if (_stagingTex != null && _stagingW == width && _stagingH == height)
            return true;

        if (_stagingTex != null)
        {
            ComRelease(_stagingTex);
            _stagingTex = null;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0
        };

        void* tex = null;
        // ID3D11Device::CreateTexture2D -> slot 5
        int hr = ((delegate* unmanaged[Stdcall]<void*, D3D11_TEXTURE2D_DESC*, void*, void**, int>)GetVtbl(_device)[5])(
            _device, &desc, null, &tex);

        if (hr != S_OK || tex == null)
        {
            LastInitError = $"CreateTexture2D({width}x{height}) failed: 0x{hr:X8}";
            return false;
        }

        _stagingTex = tex;
        _stagingW = width;
        _stagingH = height;
        return true;
    }

    private void EnsureGdiBitmap(int width, int height)
    {
        if (_gdiBitmap != null && _gdiW == width && _gdiH == height)
            return;

        _gdiGraphics?.Dispose();
        _gdiBitmap?.Dispose();
        _gdiW = width;
        _gdiH = height;
        _gdiBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        _gdiGraphics = Graphics.FromImage(_gdiBitmap);
    }

    private void ReleaseDxgiResources()
    {
        if (_stagingTex != null) { ComRelease(_stagingTex); _stagingTex = null; }
        if (_duplication != null) { ComRelease(_duplication); _duplication = null; }
        if (_context != null) { ComRelease(_context); _context = null; }
        if (_device != null) { ComRelease(_device); _device = null; }
        _stagingW = 0;
        _stagingH = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDxgiResources();
        _gdiGraphics?.Dispose();
        _gdiBitmap?.Dispose();
        _gdiGraphics = null;
        _gdiBitmap = null;
    }

    private static void** GetVtbl(void* comPtr) => *(void***)comPtr;

    private static int ComQueryInterface(void* comPtr, Guid* iid, void** ppv) =>
        ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)GetVtbl(comPtr)[0])(comPtr, iid, ppv);

    private static uint ComRelease(void* comPtr) =>
        ((delegate* unmanaged[Stdcall]<void*, uint>)GetVtbl(comPtr)[2])(comPtr);

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed ushort DeviceName[32];
        public int DesktopLeft;
        public int DesktopTop;
        public int DesktopRight;
        public int DesktopBottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_POINTER_POSITION
    {
        public int X;
        public int Y;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_BOX
    {
        public uint left;
        public uint top;
        public uint front;
        public uint right;
        public uint bottom;
        public uint back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
