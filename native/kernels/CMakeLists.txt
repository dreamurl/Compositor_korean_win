cmake_minimum_required(VERSION 3.20)
project(compositor_kernels C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Upstream Compositor's pixel kernels, copied verbatim. They use only stdint/stddef/math/string/
# stdlib, so they compile unchanged under MSVC. KernelsShim.c holds this port's own additions.
set(KERNEL_SOURCES
    AdjustPixels.c
    BrushPixels.c
    ContentFill.c
    HealPixels.c
    LensPixels.c
    LevelsPixels.c
    NoisePixels.c
    WandPixels.c
    shim/KernelsShim.c)

if(MSVC)
    # /fp:fast changes rounding, and the kernels' output is compared against macOS reference images.
    add_compile_options(/W3 /O2 /fp:precise)
endif()

# Static: linked straight into the NativeAOT binary, so the shipped exe carries no kernel DLL.
add_library(compositor_kernels_static STATIC ${KERNEL_SOURCES})
set_target_properties(compositor_kernels_static PROPERTIES
    OUTPUT_NAME compositor_kernels
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/static")

# Shared: the test run executes on CoreCLR, which resolves [LibraryImport] at run time and so
# needs a real DLL. Same sources, same exports.
add_library(compositor_kernels_shared SHARED ${KERNEL_SOURCES} kernels.def)
set_target_properties(compositor_kernels_shared PROPERTIES
    OUTPUT_NAME compositor_kernels
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/shared"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/shared")
