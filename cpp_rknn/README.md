# RideManager RKNN bridge

This directory contains the native RKNN Runtime bridge used by `RknnInferenceEngine`.

## Build on RK3588

```bash
cmake -S cpp_rknn -B cpp_rknn/build -DRKNN_RUNTIME_DIR=/path/to/rknn_runtime
cmake --build cpp_rknn/build --config Release
```

The C# P/Invoke loader looks for `libridemanager_rknn.so`. Put the built library next to the RideManager executable or in a directory covered by `LD_LIBRARY_PATH`.

## Contract

- Inputs are passed as an array of `rm_rknn_input_tensor`.
- Each input carries its model input index, element count, data pointer, data type, and layout.
- Supported input data types are float32, int8, and uint8.
- Use `RM_RKNN_TENSOR_FORMAT_AUTO` to reuse the model input layout reported by RKNN Runtime.
- The bridge passes input pointers directly to `rknn_inputs_set`; no input copy is made in the bridge.
- Outputs are requested as float32 and kept valid until the next `rm_rknn_run` or `rm_rknn_destroy`.
- C# copies output tensors immediately, then uses the same post-processing parser as ONNX Runtime.
