#pragma once

#include <stdint.h>

#ifdef _WIN32
#define RM_RKNN_EXPORT __declspec(dllexport)
#else
#define RM_RKNN_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

enum
{
    RM_RKNN_MAX_DIMS = 8,
    RM_RKNN_MAX_NAME = 256
};

typedef struct rm_rknn_context rm_rknn_context;

typedef enum rm_rknn_tensor_type
{
    RM_RKNN_TENSOR_FLOAT32 = 0,
    RM_RKNN_TENSOR_INT8 = 1,
    RM_RKNN_TENSOR_UINT8 = 2
} rm_rknn_tensor_type;

typedef enum rm_rknn_tensor_format
{
    RM_RKNN_TENSOR_FORMAT_AUTO = 0,
    RM_RKNN_TENSOR_FORMAT_NCHW = 1,
    RM_RKNN_TENSOR_FORMAT_NHWC = 2
} rm_rknn_tensor_format;

typedef struct rm_rknn_tensor_metadata
{
    int32_t index;
    int32_t element_count;
    int32_t rank;
    int32_t dimensions[RM_RKNN_MAX_DIMS];
    char name[RM_RKNN_MAX_NAME];
    int32_t type;
    int32_t format;
} rm_rknn_tensor_metadata;

typedef struct rm_rknn_input_tensor
{
    int32_t index;
    const void* data;
    int32_t element_count;
    int32_t type;
    int32_t format;
} rm_rknn_input_tensor;

/// 创建 RKNN Runtime 上下文并加载模型。
RM_RKNN_EXPORT int32_t rm_rknn_create(const char* model_path, rm_rknn_context** out_context);

/// 销毁 RKNN Runtime 上下文。
RM_RKNN_EXPORT void rm_rknn_destroy(rm_rknn_context* context);

/// 使用外部输入指针数组运行一次推理；必要时桥接层会暂存并转换输入布局。
RM_RKNN_EXPORT int32_t rm_rknn_run(
    rm_rknn_context* context,
    const rm_rknn_input_tensor* inputs,
    int32_t input_count);

/// 获取模型输入数量。
RM_RKNN_EXPORT int32_t rm_rknn_get_input_count(rm_rknn_context* context);

/// 获取输入张量 metadata。
RM_RKNN_EXPORT int32_t rm_rknn_get_input_metadata(
    rm_rknn_context* context,
    int32_t input_index,
    rm_rknn_tensor_metadata* metadata);

/// 获取上一次推理保留的输出数量。
RM_RKNN_EXPORT int32_t rm_rknn_get_output_count(rm_rknn_context* context);

/// 获取输出张量 metadata。
RM_RKNN_EXPORT int32_t rm_rknn_get_output_metadata(
    rm_rknn_context* context,
    int32_t output_index,
    rm_rknn_tensor_metadata* metadata);

/// 获取输出张量数据指针，指针有效期到下一次 rm_rknn_run 或 rm_rknn_destroy。
RM_RKNN_EXPORT int32_t rm_rknn_get_output_data(
    rm_rknn_context* context,
    int32_t output_index,
    const float** data,
    int32_t* element_count);

/// 获取最近一次 native 错误信息。
RM_RKNN_EXPORT const char* rm_rknn_get_last_error(rm_rknn_context* context);

#ifdef __cplusplus
}
#endif
