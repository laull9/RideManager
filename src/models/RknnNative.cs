using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace RideManager.Models;

/// <summary>
/// 封装 RideManager RKNN C++ 桥接层的 P/Invoke 入口。
/// </summary>
internal static unsafe partial class RknnNative
{
    private const string LibraryName = "ridemanager_rknn";

    /// <summary>
    /// native 张量 metadata 中最多保留的维度数量。
    /// </summary>
    public const int MaxDimensions = 8;

    /// <summary>
    /// native 张量名称缓冲区长度。
    /// </summary>
    private const int MaxNameLength = 256;

    /// <summary>
    /// 注册开发态 native 库解析器，允许 dotnet run 直接使用 cpp_rknn/build 输出。
    /// </summary>
    static RknnNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(RknnNative).Assembly, ResolveLibrary);
    }

    /// <summary>
    /// 创建 RKNN native 上下文。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_create", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int Create(string modelPath, out IntPtr context);

    /// <summary>
    /// 销毁 RKNN native 上下文。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_destroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Destroy(IntPtr context);

    /// <summary>
    /// 运行一次 RKNN 推理。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_run", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Run(IntPtr context, RknnInputTensor* inputs, int inputCount);

    /// <summary>
    /// 获取模型输入数量。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_get_input_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetInputCount(IntPtr context);

    /// <summary>
    /// 获取输入张量 metadata。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_get_input_metadata", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetInputMetadata(IntPtr context, int inputIndex, RknnTensorMetadata* metadata);

    /// <summary>
    /// 获取上一次推理的输出数量。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_get_output_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetOutputCount(IntPtr context);

    /// <summary>
    /// 获取输出张量 metadata。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_get_output_metadata", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetOutputMetadata(IntPtr context, int outputIndex, RknnTensorMetadata* metadata);

    /// <summary>
    /// 获取输出张量 float32 数据指针。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_get_output_data", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetOutputData(IntPtr context, int outputIndex, out IntPtr data, out int elementCount);

    /// <summary>
    /// 获取 native 桥接层最近一次错误信息。
    /// </summary>
    [DllImport(LibraryName, EntryPoint = "rm_rknn_get_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetLastErrorPointer(IntPtr context);

    /// <summary>
    /// 优先从应用输出目录和仓库内 CMake 构建目录加载 RKNN bridge。
    /// </summary>
    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Equals(LibraryName, StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        foreach (var path in GetCandidateLibraryPaths())
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 返回 Linux RKNN bridge 的常用部署位置。
    /// </summary>
    private static IReadOnlyList<string> GetCandidateLibraryPaths()
    {
        return new[]
        {
            Path.Combine(AppContext.BaseDirectory, "libridemanager_rknn.so"),
            Path.Combine(Environment.CurrentDirectory, "cpp_rknn", "build", "libridemanager_rknn.so")
        };
    }

    /// <summary>
    /// 获取 native 桥接层最近一次错误信息。
    /// </summary>
    public static string GetLastError(IntPtr context)
    {
        var pointer = GetLastErrorPointer(context);
        return pointer == IntPtr.Zero ? "native_error" : Marshal.PtrToStringUTF8(pointer) ?? "native_error";
    }

    /// <summary>
    /// 表示 RKNN 输入数据类型。
    /// </summary>
    public enum RknnTensorType
    {
        Float32 = 0,
        Int8 = 1,
        UInt8 = 2
    }

    /// <summary>
    /// 表示 RKNN 输入数据布局。
    /// </summary>
    public enum RknnTensorFormat
    {
        Auto = 0,
        Nchw = 1,
        Nhwc = 2
    }

    /// <summary>
    /// 表示要传给 native 桥接层的一路输入张量。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RknnInputTensor
    {
        /// <summary>
        /// 模型输入索引。
        /// </summary>
        public int Index;

        /// <summary>
        /// 输入数据指针。
        /// </summary>
        public IntPtr Data;

        /// <summary>
        /// 输入元素数量。
        /// </summary>
        public int ElementCount;

        /// <summary>
        /// 输入数据类型。
        /// </summary>
        public RknnTensorType Type;

        /// <summary>
        /// 输入数据布局。
        /// </summary>
        public RknnTensorFormat Format;
    }

    /// <summary>
    /// 表示 native 桥接层返回的输出张量 metadata。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct RknnTensorMetadata
    {
        /// <summary>
        /// 输出索引。
        /// </summary>
        public int Index;

        /// <summary>
        /// 输出元素数量。
        /// </summary>
        public int ElementCount;

        /// <summary>
        /// 输出维度数量。
        /// </summary>
        public int Rank;

        private fixed int _dimensions[MaxDimensions];
        private fixed byte _name[MaxNameLength];

        /// <summary>
        /// 张量数据类型。
        /// </summary>
        public RknnTensorType Type;

        /// <summary>
        /// 张量数据布局。
        /// </summary>
        public RknnTensorFormat Format;

        /// <summary>
        /// 读取输出维度。
        /// </summary>
        public int[] GetDimensions()
        {
            var count = Math.Clamp(Rank, 0, MaxDimensions);
            var dimensions = new int[count];
            fixed (int* source = _dimensions)
            {
                for (var index = 0; index < count; index++)
                {
                    dimensions[index] = source[index];
                }
            }

            return dimensions;
        }

        /// <summary>
        /// 读取输出名称。
        /// </summary>
        public string GetName()
        {
            fixed (byte* source = _name)
            {
                var length = 0;
                while (length < MaxNameLength && source[length] != 0)
                {
                    length++;
                }

                return length == 0
                    ? $"output{Index}"
                    : Encoding.UTF8.GetString(source, length);
            }
        }
    }
}
