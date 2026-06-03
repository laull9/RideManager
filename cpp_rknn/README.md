# RideManager RKNN bridge

This directory contains the native RKNN Runtime bridge used by `RknnInferenceEngine`.

## Build on RK3588

```bash
cmake -S cpp_rknn -B cpp_rknn/build -DRKNN_RUNTIME_DIR=/path/to/rknn_runtime
cmake --build cpp_rknn/build --config Release
```

The build output contains both `libridemanager_rknn.so` and `librknnrt.so`. The bridge has an
`$ORIGIN` runtime search path, so keeping both files together is sufficient.

When running from the repository with `dotnet run`, build the bridge before building the C#
project. `RideManager.csproj` explicitly copies both libraries from `cpp_rknn/build` into the
.NET output directory after each build. The development loader also falls back to loading the
bridge directly from `cpp_rknn/build`.

```bash
cmake -S cpp_rknn -B cpp_rknn/build -DRKNN_RUNTIME_DIR=/path/to/rknn_runtime
cmake --build cpp_rknn/build --config Release
dotnet run -- livetest --camera CAM_FRONT --source videos/test1.mp4 --headless
```

To diagnose native loader failures:

```bash
ls -l cpp_rknn/build/libridemanager_rknn.so cpp_rknn/build/librknnrt.so
ldd cpp_rknn/build/libridemanager_rknn.so
find bin -path '*/linux-arm64/libridemanager_rknn.so' -o -path '*/linux-arm64/librknnrt.so'
```

## Contract

- Inputs are passed as an array of `rm_rknn_input_tensor`.
- Each input carries its model input index, element count, data pointer, data type, and layout.
- Supported input data types are float32, int8, and uint8.
- Use `RM_RKNN_TENSOR_FORMAT_AUTO` to reuse the model input layout reported by RKNN Runtime.
- RideManager's C# preprocessors send float32 NCHW tensors and explicitly set `RM_RKNN_TENSOR_FORMAT_NCHW`.
- When RKNN Runtime reports a rank-4 NHWC model input, the bridge transposes RideManager's NCHW
  buffer into a reused NHWC staging buffer before `rknn_inputs_set`. This is required by RKNN
  Runtime builds whose normalize path only accepts NHWC source layout.
- Inputs that already match the model layout are passed directly to `rknn_inputs_set`.
- Outputs are requested as float32 and kept valid until the next `rm_rknn_run` or `rm_rknn_destroy`.
- C# copies output tensors immediately, then uses the same post-processing parser as ONNX Runtime.
