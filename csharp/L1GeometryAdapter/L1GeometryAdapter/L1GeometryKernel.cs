using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace L1GeometryAdapter
{
    // ---------------------------------------------------------------
    // Structs / Enums mirroring l1_geometry_kernel.h
    // ---------------------------------------------------------------

    /// <summary>3 つの double をパディングなしで保持する汎用ベクトル型（24 bytes）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    /// <summary>C の double[3] × 3 と同一レイアウト（72 bytes）。unsafe 不要。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AxisDto
    {
        public Vec3 Origin;
        public Vec3 Dir;
        public Vec3 Xdir;
    }

    public enum StockType : int
    {
        Box      = 1,
        Cylinder = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StockDto
    {
        public StockType Type;
        public double    P1;
        public double    P2;
        public double    P3;
        public AxisDto   Axis;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MillHoleFeatureDto
    {
        public double  Radius;
        public double  Depth;
        public AxisDto Axis;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PocketRectFeatureDto
    {
        public double  Width;
        public double  Height;
        public double  Depth;
        public AxisDto Axis;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Path2DPointDto
    {
        public double U;
        public double V;
    }

    public enum Path2DSegmentType : int
    {
        Line   = 1,
        Arc    = 2,
        Spline = 3,
    }

    public enum ArcDirection : int
    {
        CW  = 1,
        CCW = 2,
    }

    /// <summary>
    /// C の Path2DSegmentDto と同一レイアウト（56 bytes）。
    /// doubles を先頭に配置するためパディングなし。unsafe 不要。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Path2DSegmentDto
    {
        public Path2DPointDto    From;
        public Path2DPointDto    To;
        public Path2DPointDto    Center;
        public Path2DSegmentType Type;
        public ArcDirection      ArcDirection;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OperationResult
    {
        public int ResultShapeId;
        public int DeltaShapeId;
        public int ErrorCode;
    }

    public enum OutputFormat : int
    {
        Step = 1,
        Stl  = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OutputOptions
    {
        public OutputFormat Format;
        public double       LinearDeflection;
        public double       AngularDeflection;
        public int          Parallel;
    }

    // ---------------------------------------------------------------
    // Raw P/Invoke  (internal — 呼び出し側は L1Kernel を使う)
    // ---------------------------------------------------------------

    internal static class L1GeometryKernelNative
    {
        private const string Dll = "l1_geometry_kernel";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr L1_CreateKernel();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_DestroyKernel(IntPtr kernel);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_CreateStock(IntPtr kernel, ref StockDto dto, out int outStockId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_ApplyMillHole(
            IntPtr kernel, int stockId,
            ref MillHoleFeatureDto dto,
            out OperationResult outResult);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_ApplyPocketRect(
            IntPtr kernel, int stockId,
            ref PocketRectFeatureDto dto,
            out OperationResult outResult);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_ApplyTurnOd(
            IntPtr kernel, int stockId,
            ref AxisDto axis,
            [In] Path2DSegmentDto[] segments, int segmentCount, int closed,
            out OperationResult outResult);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_ApplyTurnId(
            IntPtr kernel, int stockId,
            ref AxisDto axis,
            [In] Path2DSegmentDto[] segments, int segmentCount, int closed,
            out OperationResult outResult);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_ApplyMillContour(
            IntPtr kernel, int stockId,
            ref AxisDto axis,
            [In] Path2DSegmentDto[] segments, int segmentCount, int closed,
            double depth,
            out OperationResult outResult);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_DeleteShape(IntPtr kernel, int shapeId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int L1_ImportStepAsShape(
            IntPtr kernel,
            string filePathUtf8,
            out int outShapeId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int L1_ExportShape(
            IntPtr kernel, int shapeId,
            ref OutputOptions opt,
            string filePathUtf8);
    }

    // ---------------------------------------------------------------
    // Public API — IDisposable ラッパー
    // ---------------------------------------------------------------

    /// <summary>l1_geometry_kernel のライフタイムを管理する。</summary>
    public sealed class L1Kernel : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly Stack<int> _trackedShapes = new();
        private bool _disposed;

        public L1Kernel()
        {
            _handle = L1GeometryKernelNative.L1_CreateKernel();
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("L1_CreateKernel failed.");
        }

        // --- Stock ---

        public int CreateStock(ref StockDto dto)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_CreateStock(_handle, ref dto, out int id);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_CreateStock));
            _trackedShapes.Push(id);
            return id;
        }

        // --- Features ---

        public OperationResult ApplyMillHole(int stockId, MillHoleFeatureDto dto)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ApplyMillHole(_handle, stockId, ref dto, out var result);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ApplyMillHole));
            TrackResult(result);
            return result;
        }

        public OperationResult ApplyPocketRect(int stockId, PocketRectFeatureDto dto)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ApplyPocketRect(_handle, stockId, ref dto, out var result);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ApplyPocketRect));
            TrackResult(result);
            return result;
        }

        public OperationResult ApplyTurnOd(int stockId, AxisDto axis,
                                           Path2DSegmentDto[] segments, bool closed)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ApplyTurnOd(
                _handle, stockId, ref axis, segments, segments.Length, closed ? 1 : 0,
                out var result);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ApplyTurnOd));
            TrackResult(result);
            return result;
        }

        public OperationResult ApplyTurnId(int stockId, AxisDto axis,
                                           Path2DSegmentDto[] segments, bool closed)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ApplyTurnId(
                _handle, stockId, ref axis, segments, segments.Length, closed ? 1 : 0,
                out var result);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ApplyTurnId));
            TrackResult(result);
            return result;
        }

        public OperationResult ApplyMillContour(int stockId, AxisDto axis,
                                                Path2DSegmentDto[] segments, bool closed,
                                                double depth)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ApplyMillContour(
                _handle, stockId, ref axis, segments, segments.Length, closed ? 1 : 0,
                depth, out var result);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ApplyMillContour));
            TrackResult(result);
            return result;
        }

        // --- Export ---

        public int ImportStep(string filePath)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ImportStepAsShape(_handle, filePath, out int shapeId);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ImportStepAsShape));
            _trackedShapes.Push(shapeId);
            return shapeId;
        }

        public void ExportShape(int shapeId, OutputOptions opt, string filePath)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_ExportShape(_handle, shapeId, ref opt, filePath);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ExportShape));
        }

        // --- IDisposable ---

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            while (_trackedShapes.TryPop(out int id))
                L1GeometryKernelNative.L1_DeleteShape(_handle, id);

            L1GeometryKernelNative.L1_DestroyKernel(_handle);
        }

        // --- helpers ---

        private void TrackResult(OperationResult result)
        {
            _trackedShapes.Push(result.ResultShapeId);
            _trackedShapes.Push(result.DeltaShapeId);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(L1Kernel));
        }

        private static void ThrowIfError(int rc, string api)
        {
            if (rc != 0) throw new InvalidOperationException($"{api} failed: errorCode={rc}");
        }
    }
}
