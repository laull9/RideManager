#include "ridemanager_rknn.h"

#include <rknn_api.h>

#include <algorithm>
#include <cstring>
#include <limits>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

namespace
{
thread_local std::string g_last_error;

/// 记录全局错误信息，供创建上下文失败时读取。
int32_t fail_global(const std::string& message)
{
    g_last_error = message;
    return -1;
}

/// 将 RKNN 状态码格式化为可诊断错误。
std::string rknn_error(const char* operation, int32_t status)
{
    std::ostringstream builder;
    builder << operation << " failed: " << status;
    return builder.str();
}

/// 计算张量属性中的元素数量。
int32_t tensor_element_count(const rknn_tensor_attr& attr)
{
    if (attr.n_elems > 0)
    {
        return static_cast<int32_t>(attr.n_elems);
    }

    int64_t count = 1;
    for (uint32_t index = 0; index < attr.n_dims; index++)
    {
        count *= std::max(1, static_cast<int32_t>(attr.dims[index]));
    }

    return static_cast<int32_t>(count);
}

/// 获取 RideManager 输入类型对应的字节数。
int32_t rm_tensor_type_size(int32_t type)
{
    switch (type)
    {
    case RM_RKNN_TENSOR_FLOAT32:
        return 4;
    case RM_RKNN_TENSOR_INT8:
    case RM_RKNN_TENSOR_UINT8:
        return 1;
    default:
        return 0;
    }
}

/// 将 RideManager 输入类型映射为 RKNN Runtime 类型。
rknn_tensor_type to_rknn_tensor_type(int32_t type)
{
    switch (type)
    {
    case RM_RKNN_TENSOR_INT8:
        return RKNN_TENSOR_INT8;
    case RM_RKNN_TENSOR_UINT8:
        return RKNN_TENSOR_UINT8;
    case RM_RKNN_TENSOR_FLOAT32:
    default:
        return RKNN_TENSOR_FLOAT32;
    }
}

/// 将 RKNN Runtime 类型映射为 RideManager ABI 类型。
int32_t from_rknn_tensor_type(rknn_tensor_type type)
{
    switch (type)
    {
    case RKNN_TENSOR_INT8:
        return RM_RKNN_TENSOR_INT8;
    case RKNN_TENSOR_UINT8:
        return RM_RKNN_TENSOR_UINT8;
    case RKNN_TENSOR_FLOAT32:
    default:
        return RM_RKNN_TENSOR_FLOAT32;
    }
}

/// 将 RideManager 输入格式映射为 RKNN Runtime 格式。
rknn_tensor_format to_rknn_tensor_format(int32_t format, rknn_tensor_format default_format)
{
    switch (format)
    {
    case RM_RKNN_TENSOR_FORMAT_NCHW:
        return RKNN_TENSOR_NCHW;
    case RM_RKNN_TENSOR_FORMAT_NHWC:
        return RKNN_TENSOR_NHWC;
    case RM_RKNN_TENSOR_FORMAT_AUTO:
    default:
        return default_format;
    }
}

/// 将 RKNN Runtime 格式映射为 RideManager ABI 格式。
int32_t from_rknn_tensor_format(rknn_tensor_format format)
{
    switch (format)
    {
    case RKNN_TENSOR_NHWC:
        return RM_RKNN_TENSOR_FORMAT_NHWC;
    case RKNN_TENSOR_NCHW:
    default:
        return RM_RKNN_TENSOR_FORMAT_NCHW;
    }
}

/// 将 RKNN 模型声明的 NHWC 输入维度用于 NCHW 到 NHWC 的数据转置。
bool stage_nchw_as_nhwc(
    const rm_rknn_input_tensor& source,
    const rknn_tensor_attr& input_attr,
    int32_t type_size,
    std::vector<uint8_t>& staging_buffer)
{
    if (input_attr.n_dims != 4 || type_size <= 0)
    {
        return false;
    }

    const int64_t batch = input_attr.dims[0];
    const int64_t height = input_attr.dims[1];
    const int64_t width = input_attr.dims[2];
    const int64_t channels = input_attr.dims[3];
    const int64_t element_count = batch * height * width * channels;
    if (batch <= 0 || height <= 0 || width <= 0 || channels <= 0
        || element_count != source.element_count)
    {
        return false;
    }

    staging_buffer.resize(static_cast<size_t>(element_count) * static_cast<size_t>(type_size));
    const auto* source_bytes = static_cast<const uint8_t*>(source.data);
    auto* target_bytes = staging_buffer.data();
    for (int64_t batch_index = 0; batch_index < batch; batch_index++)
    {
        for (int64_t height_index = 0; height_index < height; height_index++)
        {
            for (int64_t width_index = 0; width_index < width; width_index++)
            {
                for (int64_t channel_index = 0; channel_index < channels; channel_index++)
                {
                    const auto source_index =
                        ((batch_index * channels + channel_index) * height + height_index) * width + width_index;
                    const auto target_index =
                        ((batch_index * height + height_index) * width + width_index) * channels + channel_index;
                    std::memcpy(
                        target_bytes + target_index * type_size,
                        source_bytes + source_index * type_size,
                        static_cast<size_t>(type_size));
                }
            }
        }
    }

    return true;
}

/// 填充张量 metadata。
void fill_tensor_metadata(const rknn_tensor_attr& attr, int32_t index, rm_rknn_tensor_metadata* metadata)
{
    std::memset(metadata, 0, sizeof(*metadata));
    metadata->index = index;
    metadata->element_count = tensor_element_count(attr);
    metadata->rank = static_cast<int32_t>(std::min<uint32_t>(attr.n_dims, RM_RKNN_MAX_DIMS));
    metadata->type = from_rknn_tensor_type(attr.type);
    metadata->format = from_rknn_tensor_format(attr.fmt);
    for (int32_t dimension_index = 0; dimension_index < metadata->rank; dimension_index++)
    {
        metadata->dimensions[dimension_index] = static_cast<int32_t>(attr.dims[dimension_index]);
    }

    const auto* name = attr.name[0] == '\0' ? nullptr : attr.name;
    if (name != nullptr)
    {
        std::strncpy(metadata->name, name, RM_RKNN_MAX_NAME - 1);
    }
}

/// 释放 RKNN Runtime 持有的输出缓冲区。
void release_outputs_locked(rm_rknn_context* context);
}

struct rm_rknn_context
{
    rknn_context handle = 0;
    std::vector<rknn_tensor_attr> input_attrs;
    std::vector<std::vector<uint8_t>> input_staging_buffers;
    std::vector<rknn_tensor_attr> output_attrs;
    std::vector<rknn_output> outputs;
    bool outputs_valid = false;
    std::mutex mutex;
    std::string last_error;
};

namespace
{
/// 记录上下文错误信息。
int32_t fail_context(rm_rknn_context* context, const std::string& message)
{
    if (context != nullptr)
    {
        context->last_error = message;
    }

    return -1;
}

/// 释放 RKNN Runtime 持有的输出缓冲区。
void release_outputs_locked(rm_rknn_context* context)
{
    if (context == nullptr || !context->outputs_valid || context->outputs.empty())
    {
        return;
    }

    rknn_outputs_release(context->handle, static_cast<uint32_t>(context->outputs.size()), context->outputs.data());
    context->outputs_valid = false;
    context->outputs.clear();
}
}

int32_t rm_rknn_create(const char* model_path, rm_rknn_context** out_context)
{
    if (out_context == nullptr)
    {
        return fail_global("out_context is null");
    }

    *out_context = nullptr;
    if (model_path == nullptr || model_path[0] == '\0')
    {
        return fail_global("model_path is empty");
    }

    auto context = new rm_rknn_context();
    auto status = rknn_init(&context->handle, const_cast<char*>(model_path), 0, 0, nullptr);
    if (status != RKNN_SUCC)
    {
        const auto message = rknn_error("rknn_init", status);
        delete context;
        return fail_global(message);
    }

    rknn_input_output_num io_num{};
    status = rknn_query(context->handle, RKNN_QUERY_IN_OUT_NUM, &io_num, sizeof(io_num));
    if (status != RKNN_SUCC)
    {
        const auto message = rknn_error("RKNN_QUERY_IN_OUT_NUM", status);
        rknn_destroy(context->handle);
        delete context;
        return fail_global(message);
    }

    context->input_attrs.resize(io_num.n_input);
    for (uint32_t index = 0; index < io_num.n_input; index++)
    {
        auto& attr = context->input_attrs[index];
        std::memset(&attr, 0, sizeof(attr));
        attr.index = index;
        status = rknn_query(context->handle, RKNN_QUERY_INPUT_ATTR, &attr, sizeof(attr));
        if (status != RKNN_SUCC)
        {
            const auto message = rknn_error("RKNN_QUERY_INPUT_ATTR", status);
            rknn_destroy(context->handle);
            delete context;
            return fail_global(message);
        }
    }
    context->input_staging_buffers.resize(io_num.n_input);

    context->output_attrs.resize(io_num.n_output);
    for (uint32_t index = 0; index < io_num.n_output; index++)
    {
        auto& attr = context->output_attrs[index];
        std::memset(&attr, 0, sizeof(attr));
        attr.index = index;
        status = rknn_query(context->handle, RKNN_QUERY_OUTPUT_ATTR, &attr, sizeof(attr));
        if (status != RKNN_SUCC)
        {
            const auto message = rknn_error("RKNN_QUERY_OUTPUT_ATTR", status);
            rknn_destroy(context->handle);
            delete context;
            return fail_global(message);
        }
    }

    *out_context = context;
    return 0;
}

void rm_rknn_destroy(rm_rknn_context* context)
{
    if (context == nullptr)
    {
        return;
    }

    {
        std::lock_guard<std::mutex> lock(context->mutex);
        release_outputs_locked(context);
        if (context->handle != 0)
        {
            rknn_destroy(context->handle);
            context->handle = 0;
        }
    }

    delete context;
}

int32_t rm_rknn_run(
    rm_rknn_context* context,
    const rm_rknn_input_tensor* inputs,
    int32_t input_count)
{
    if (context == nullptr)
    {
        return fail_global("context is null");
    }

    std::lock_guard<std::mutex> lock(context->mutex);
    if (inputs == nullptr)
    {
        return fail_context(context, "inputs is null");
    }

    if (context->input_attrs.empty())
    {
        return fail_context(context, "model has no inputs");
    }

    if (input_count != static_cast<int32_t>(context->input_attrs.size()))
    {
        std::ostringstream builder;
        builder << "input count mismatch: expected " << context->input_attrs.size() << ", actual " << input_count;
        return fail_context(context, builder.str());
    }

    release_outputs_locked(context);

    std::vector<uint8_t> seen(context->input_attrs.size(), 0);
    std::vector<rknn_input> rknn_inputs(static_cast<size_t>(input_count));
    for (int32_t input_index = 0; input_index < input_count; input_index++)
    {
        const auto& source = inputs[input_index];
        if (source.index < 0 || source.index >= static_cast<int32_t>(context->input_attrs.size()))
        {
            return fail_context(context, "input index out of range");
        }

        if (seen[static_cast<size_t>(source.index)] != 0)
        {
            return fail_context(context, "duplicate input index");
        }

        if (source.data == nullptr)
        {
            return fail_context(context, "input data is null");
        }

        const auto type_size = rm_tensor_type_size(source.type);
        if (type_size <= 0)
        {
            return fail_context(context, "unsupported input tensor type");
        }

        if (source.element_count <= 0)
        {
            return fail_context(context, "input element count must be positive");
        }

        const auto byte_count = static_cast<int64_t>(source.element_count) * type_size;
        if (byte_count > std::numeric_limits<uint32_t>::max())
        {
            return fail_context(context, "input byte count is too large");
        }

        const auto& input_attr = context->input_attrs[static_cast<size_t>(source.index)];
        const auto expected_elements = tensor_element_count(input_attr);
        if (expected_elements > 0 && source.element_count != expected_elements)
        {
            std::ostringstream builder;
            builder << "input " << source.index << " element count mismatch: expected "
                << expected_elements << ", actual " << source.element_count;
            return fail_context(context, builder.str());
        }

        seen[static_cast<size_t>(source.index)] = 1;

        auto& target = rknn_inputs[static_cast<size_t>(input_index)];
        target.index = static_cast<uint32_t>(source.index);
        target.size = static_cast<uint32_t>(byte_count);
        target.pass_through = 0;
        target.type = to_rknn_tensor_type(source.type);
        if (source.format == RM_RKNN_TENSOR_FORMAT_NCHW && input_attr.fmt == RKNN_TENSOR_NHWC)
        {
            auto& staging_buffer = context->input_staging_buffers[static_cast<size_t>(source.index)];
            if (!stage_nchw_as_nhwc(source, input_attr, type_size, staging_buffer))
            {
                std::ostringstream builder;
                builder << "input " << source.index
                    << " cannot convert NCHW data to the model's NHWC layout";
                return fail_context(context, builder.str());
            }

            target.buf = staging_buffer.data();
            target.fmt = RKNN_TENSOR_NHWC;
        }
        else
        {
            target.buf = const_cast<void*>(source.data);
            target.fmt = to_rknn_tensor_format(source.format, input_attr.fmt);
        }
    }

    auto status = rknn_inputs_set(context->handle, static_cast<uint32_t>(rknn_inputs.size()), rknn_inputs.data());
    if (status != RKNN_SUCC)
    {
        return fail_context(context, rknn_error("rknn_inputs_set", status));
    }

    status = rknn_run(context->handle, nullptr);
    if (status != RKNN_SUCC)
    {
        return fail_context(context, rknn_error("rknn_run", status));
    }

    context->outputs.assign(context->output_attrs.size(), rknn_output{});
    for (uint32_t index = 0; index < context->outputs.size(); index++)
    {
        context->outputs[index].index = index;
        context->outputs[index].want_float = 1;
        context->outputs[index].is_prealloc = 0;
    }

    status = rknn_outputs_get(context->handle, static_cast<uint32_t>(context->outputs.size()), context->outputs.data(), nullptr);
    if (status != RKNN_SUCC)
    {
        context->outputs.clear();
        return fail_context(context, rknn_error("rknn_outputs_get", status));
    }

    context->outputs_valid = true;
    return 0;
}

int32_t rm_rknn_get_input_count(rm_rknn_context* context)
{
    if (context == nullptr)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(context->mutex);
    return static_cast<int32_t>(context->input_attrs.size());
}

int32_t rm_rknn_get_input_metadata(
    rm_rknn_context* context,
    int32_t input_index,
    rm_rknn_tensor_metadata* metadata)
{
    if (context == nullptr)
    {
        return fail_global("context is null");
    }

    std::lock_guard<std::mutex> lock(context->mutex);
    if (metadata == nullptr)
    {
        return fail_context(context, "metadata is null");
    }

    if (input_index < 0 || input_index >= static_cast<int32_t>(context->input_attrs.size()))
    {
        return fail_context(context, "input index out of range");
    }

    const auto& attr = context->input_attrs[static_cast<size_t>(input_index)];
    fill_tensor_metadata(attr, input_index, metadata);
    return 0;
}

int32_t rm_rknn_get_output_count(rm_rknn_context* context)
{
    if (context == nullptr)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(context->mutex);
    return context->outputs_valid ? static_cast<int32_t>(context->outputs.size()) : 0;
}

int32_t rm_rknn_get_output_metadata(
    rm_rknn_context* context,
    int32_t output_index,
    rm_rknn_tensor_metadata* metadata)
{
    if (context == nullptr)
    {
        return fail_global("context is null");
    }

    std::lock_guard<std::mutex> lock(context->mutex);
    if (metadata == nullptr)
    {
        return fail_context(context, "metadata is null");
    }

    if (output_index < 0 || output_index >= static_cast<int32_t>(context->output_attrs.size()))
    {
        return fail_context(context, "output index out of range");
    }

    const auto& attr = context->output_attrs[static_cast<size_t>(output_index)];
    fill_tensor_metadata(attr, output_index, metadata);
    return 0;
}

int32_t rm_rknn_get_output_data(
    rm_rknn_context* context,
    int32_t output_index,
    const float** data,
    int32_t* element_count)
{
    if (context == nullptr)
    {
        return fail_global("context is null");
    }

    std::lock_guard<std::mutex> lock(context->mutex);
    if (data == nullptr || element_count == nullptr)
    {
        return fail_context(context, "output data arguments are null");
    }

    *data = nullptr;
    *element_count = 0;
    if (!context->outputs_valid || output_index < 0 || output_index >= static_cast<int32_t>(context->outputs.size()))
    {
        return fail_context(context, "output index out of range");
    }

    const auto& output = context->outputs[static_cast<size_t>(output_index)];
    *data = static_cast<const float*>(output.buf);
    *element_count = static_cast<int32_t>(output.size / sizeof(float));
    return *data == nullptr || *element_count <= 0
        ? fail_context(context, "output buffer is empty")
        : 0;
}

const char* rm_rknn_get_last_error(rm_rknn_context* context)
{
    if (context != nullptr && !context->last_error.empty())
    {
        return context->last_error.c_str();
    }

    return g_last_error.empty() ? "native_error" : g_last_error.c_str();
}
