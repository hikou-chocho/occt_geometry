cmake_minimum_required(VERSION 3.20)

project(occt_geometry LANGUAGES CXX)

if(NOT WIN32)
  message(FATAL_ERROR "This project supports Windows/MSVC only in Phase1.")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(BUILD_SHARED_LIBS "Build shared library" ON)

if(NOT OpenCASCADE_DIR AND DEFINED ENV{OpenCASCADE_DIR})
  set(OpenCASCADE_DIR "$ENV{OpenCASCADE_DIR}" CACHE PATH "Path to OpenCASCADEConfig.cmake")
endif()

if(NOT OpenCASCADE_DIR)
  message(FATAL_ERROR "OpenCASCADE_DIR is not set.")
endif()

get_filename_component(OpenCASCADE_ROOT "${OpenCASCADE_DIR}" DIRECTORY)
get_filename_component(OCCT_PACKAGE_ROOT "${OpenCASCADE_ROOT}" DIRECTORY)

set(OCCT_INCLUDE_DIR "${OpenCASCADE_ROOT}/inc")
set(OCCT_LIBRARY_DIR "${OpenCASCADE_ROOT}/win64/vc14/lib")
set(OCCT_BINARY_DIR "${OpenCASCADE_ROOT}/win64/vc14/bin")
set(OCCT_THIRDPARTY_ROOT "${OCCT_PACKAGE_ROOT}/3rdparty-vc14-64")

if(NOT EXISTS "${OCCT_INCLUDE_DIR}")
  message(FATAL_ERROR "OCCT include dir not found: ${OCCT_INCLUDE_DIR}")
endif()

if(NOT EXISTS "${OCCT_LIBRARY_DIR}")
  message(FATAL_ERROR "OCCT library dir not found: ${OCCT_LIBRARY_DIR}")
endif()

if(NOT EXISTS "${OCCT_BINARY_DIR}")
  message(FATAL_ERROR "OCCT binary dir not found: ${OCCT_BINARY_DIR}")
endif()

if(EXISTS "${OCCT_THIRDPARTY_ROOT}")
  file(GLOB_RECURSE OCCT_THIRDPARTY_DLLS
    "${OCCT_THIRDPARTY_ROOT}/*/bin/*.dll"
  )
endif()

set(OCCT_LIB_NAMES
  TKernel
  TKMath
  TKG2d
  TKG3d
  TKGeomBase
  TKBRep
  TKGeomAlgo
  TKTopAlgo
  TKPrim
  TKBO
  TKBool
  TKShHealing
  TKMesh
  TKXSBase
  TKDE
  TKDESTEP
  TKDESTL
)

set(OCCT_LIBS "")
foreach(lib_name IN LISTS OCCT_LIB_NAMES)
  find_library(${lib_name}_LIB
    NAMES ${lib_name}
    PATHS "${OCCT_LIBRARY_DIR}"
    NO_DEFAULT_PATH
  )
  if(NOT ${lib_name}_LIB)
    message(FATAL_ERROR "OCCT library not found: ${lib_name}")
  endif()
  list(APPEND OCCT_LIBS "${${lib_name}_LIB}")
endforeach()

add_library(occt_geometry SHARED
  src/l1_geometry_kernel.cpp
)

target_compile_definitions(occt_geometry
  PRIVATE
    L1_GEOMETRY_KERNEL_BUILD
)

target_include_directories(occt_geometry
  PUBLIC
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${OCCT_INCLUDE_DIR}
)

target_link_libraries(occt_geometry
  PRIVATE
    ${OCCT_LIBS}
)

add_custom_command(TARGET occt_geometry POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_directory
    "${OCCT_BINARY_DIR}"
    "$<TARGET_FILE_DIR:occt_geometry>"
)

if(OCCT_THIRDPARTY_DLLS)
  foreach(thirdparty_dll IN LISTS OCCT_THIRDPARTY_DLLS)
    add_custom_command(TARGET occt_geometry POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${thirdparty_dll}"
        "$<TARGET_FILE_DIR:occt_geometry>"
    )
  endforeach()
endif()

if(MSVC)
  target_compile_options(occt_geometry PRIVATE /EHsc)
endif()

set_target_properties(occt_geometry PROPERTIES
  OUTPUT_NAME "l1_geometry_kernel"
)

add_executable(occt_geometry_sample
  samples/main.cpp
)

target_include_directories(occt_geometry_sample
  PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_link_libraries(occt_geometry_sample
  PRIVATE
    occt_geometry
)

add_custom_command(TARGET occt_geometry_sample POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_directory
    "${OCCT_BINARY_DIR}"
    "$<TARGET_FILE_DIR:occt_geometry_sample>"
)

if(OCCT_THIRDPARTY_DLLS)
  foreach(thirdparty_dll IN LISTS OCCT_THIRDPARTY_DLLS)
    add_custom_command(TARGET occt_geometry_sample POST_BUILD
      COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${thirdparty_dll}"
        "$<TARGET_FILE_DIR:occt_geometry_sample>"
    )
  endforeach()
endif()
