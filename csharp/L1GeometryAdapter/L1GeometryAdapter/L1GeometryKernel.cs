using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace L1GeometryAdapter
{
    // ---------------------------------------------------------------
    // Structs / Enums mirroring l1_geometry_kernel.h
    // ---------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct AxisDto
    {
        public fixed double Origin[3];
        public fixed double Dir[3];
        public fixed double Xdir[3];
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
        public double P1;
        public double P2;
        public double P3;
        public AxisDto Axis;
    }

    public enum FeatureType : int
    {
        Drill      = 1,
        PocketRect = 2,
        TurnOd     = 3,
        TurnId     = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrillFeatureDto
    {
        public double Radius;
        public double Depth;
        public AxisDto Axis;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PocketRectFeatureDto
    {
        public double Width;
        public double Height;
        public double Depth;
        public AxisDto Axis;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TurnOdFeatureDto
    {
        public double TargetDiameter;
        public double Length;
        public int ProfileCount;
        public fixed double ProfileZ[L1GeometryKernelNative.TurnOdProfileMax];
        public fixed double ProfileRadius[L1GeometryKernelNative.TurnOdProfileMax];
        public AxisDto Axis;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TurnIdFeatureDto
    {
        public double TargetDiameter;
        public double Length;
        public int ProfileCount;
        public fixed double ProfileZ[L1GeometryKernelNative.TurnOdProfileMax];
        public fixed double ProfileRadius[L1GeometryKernelNative.TurnOdProfileMax];
        public AxisDto Axis;
    }

    // C側: FeatureDto.u はオフセット4から始まる union
    [StructLayout(LayoutKind.Explicit,Pack =8)]
    public struct FeatureDto
    {
        [FieldOffset(0)] public FeatureType Type;
        [FieldOffset(8)] public DrillFeatureDto Drill;
        [FieldOffset(8)] public PocketRectFeatureDto PocketRect;
        [FieldOffset(8)] public TurnOdFeatureDto TurnOd;
        [FieldOffset(8)] public TurnIdFeatureDto TurnId;
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
        public double LinearDeflection;
        public double AngularDeflection;
        public int Parallel;
    }

    // ---------------------------------------------------------------
    // Raw P/Invoke  (internal — 呼び出し側は L1Kernel を使う)
    // ---------------------------------------------------------------

    internal static class L1GeometryKernelNative
    {
        public const int TurnOdProfileMax = 64;

        private const string Dll = "l1_geometry_kernel";

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr L1_CreateKernel();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_DestroyKernel(IntPtr kernel);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_CreateStock(IntPtr kernel, ref StockDto dto, out int outStockId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_ApplyFeature(IntPtr kernel, int stockId, ref FeatureDto feature, out OperationResult outResult);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int L1_DeleteShape(IntPtr kernel, int shapeId);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        internal static extern int L1_ExportShape(IntPtr kernel, int shapeId, ref OutputOptions opt, string filePathUtf8);
    }

    // ---------------------------------------------------------------
    // Public API — IDisposable ラッパー
    // ---------------------------------------------------------------

    /// <summary>
    /// l1_geometry_kernel のライフタイムを管理する。
    /// </summary>
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

        /// <summary>ストックを作成して Shape ID を返す。Dispose 時に自動削除される。</summary>
        public int CreateStock(ref StockDto dto)
        {
            ThrowIfDisposed();
            int rc = L1GeometryKernelNative.L1_CreateStock(_handle, ref dto, out int id);
            ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_CreateStock));
            _trackedShapes.Push(id);
            return id;
        }

        // --- Feature ---

        /// <summary>
        /// フィーチャを順番に適用し、各フィーチャの OperationResult を返す。
        /// </summary>
        public IReadOnlyList<OperationResult> ApplyFeatures(int stockId, IEnumerable<FeatureDto> features)
        {
            ThrowIfDisposed();
            var results = new List<OperationResult>();
            int currentShapeId = stockId;
            foreach (var feature in features)
            {
                var f = feature;
                int rc = L1GeometryKernelNative.L1_ApplyFeature(_handle, currentShapeId, ref f, out OperationResult result);
                ThrowIfError(rc, nameof(L1GeometryKernelNative.L1_ApplyFeature));
                _trackedShapes.Push(result.ResultShapeId);
                _trackedShapes.Push(result.DeltaShapeId);
                results.Add(result);
                currentShapeId = result.ResultShapeId;
            }
            return results;
        }

        // --- Export ---

        /// <summary>シェイプをファイルにエクスポートする。</summary>
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

            // 全 Shape を逆順削除してからカーネルを破棄
            while (_trackedShapes.TryPop(out int id))
                L1GeometryKernelNative.L1_DeleteShape(_handle, id);

            L1GeometryKernelNative.L1_DestroyKernel(_handle);
        }

        // --- helpers ---

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