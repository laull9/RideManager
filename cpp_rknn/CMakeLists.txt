cmake_minimum_required(VERSION 3.16)

project(ridemanager_rknn LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(RKNN_RUNTIME_DIR "" CACHE PATH "Path to RKNN Runtime package root")

find_path(RKNN_INCLUDE_DIR
    NAMES rknn_api.h
    HINTS
        ${RKNN_RUNTIME_DIR}/include
        ${RKNN_RUNTIME_DIR}/runtime/Linux/librknn_api/include
        /usr/include
        /usr/local/include)

find_library(RKNN_LIBRARY
    NAMES rknnrt librknnrt.so
    HINTS
        ${RKNN_RUNTIME_DIR}/lib
        ${RKNN_RUNTIME_DIR}/lib/aarch64
        ${RKNN_RUNTIME_DIR}/runtime/Linux/librknn_api/aarch64
        ${RKNN_RUNTIME_DIR}/runtime/Linux/librknn_api/aarch64/lib
        /usr/lib
        /usr/local/lib)

if (NOT RKNN_INCLUDE_DIR OR NOT RKNN_LIBRARY)
    message(FATAL_ERROR "RKNN Runtime not found. Pass -DRKNN_RUNTIME_DIR=/path/to/rknn_runtime")
endif ()

add_library(ridemanager_rknn SHARED
    src/ridemanager_rknn.cpp)

target_include_directories(ridemanager_rknn
    PUBLIC include
    PRIVATE ${RKNN_INCLUDE_DIR})

target_link_libraries(ridemanager_rknn
    PRIVATE ${RKNN_LIBRARY})

set_target_properties(ridemanager_rknn PROPERTIES
    OUTPUT_NAME ridemanager_rknn
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN")

add_custom_command(TARGET ridemanager_rknn POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${RKNN_LIBRARY}"
        "$<TARGET_FILE_DIR:ridemanager_rknn>/librknnrt.so"
    COMMENT "Copying RKNN Runtime next to libridemanager_rknn.so")

install(TARGETS ridemanager_rknn
    LIBRARY DESTINATION lib)

install(FILES "${RKNN_LIBRARY}"
    DESTINATION lib
    RENAME librknnrt.so)
