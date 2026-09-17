# Copies the freshly built C# Collector next to the Desktop executable so an IDE
# build (Qt Creator, VS Code) runs end to end without scripts/build.ps1.
#
# Invoked as: cmake -DSRC_ROOT=<repo>/src/Collector/bin -DCONFIG=Debug|Release
#                   -DDEST=<dir of MentorRecorder.Desktop.exe> -P StageCollector.cmake
#
# Boundary: the Machina.FFXIV package ships an injection payload
# (deucalion*.dll). Directory.Build.targets already strips it from the build
# output; this script refuses to copy it as a second line of defence.

cmake_minimum_required(VERSION 3.20)

foreach(v SRC_ROOT CONFIG DEST)
    if(NOT DEFINED ${v})
        message(FATAL_ERROR "StageCollector.cmake: ${v} is required")
    endif()
endforeach()

set(_candidates
    "${SRC_ROOT}/x64/${CONFIG}/net8.0-windows/win-x64"
    "${SRC_ROOT}/${CONFIG}/net8.0-windows/win-x64")

set(_src "")
set(_newest 0)
foreach(dir IN LISTS _candidates)
    set(_dll "${dir}/MentorRecorder.Collector.dll")
    if(EXISTS "${_dll}")
        file(TIMESTAMP "${_dll}" _ts "%s" UTC)
        if(_ts GREATER _newest)
            set(_newest ${_ts})
            set(_src "${dir}")
        endif()
    endif()
endforeach()

if(_src STREQUAL "")
    message(FATAL_ERROR "StageCollector.cmake: no Collector build output under ${SRC_ROOT} (config ${CONFIG})")
endif()

file(GLOB_RECURSE _payload RELATIVE "${_src}" "${_src}/deucalion*")
if(_payload)
    message(FATAL_ERROR "StageCollector.cmake: refusing to stage an injection payload: ${_payload}")
endif()

file(GLOB_RECURSE _files RELATIVE "${_src}" "${_src}/*")
set(_count 0)
foreach(rel IN LISTS _files)
    if(rel MATCHES "[.](db|db-wal|db-shm|log)$")
        continue()
    endif()
    get_filename_component(_reldir "${rel}" DIRECTORY)
    file(MAKE_DIRECTORY "${DEST}/${_reldir}")
    file(COPY_FILE "${_src}/${rel}" "${DEST}/${rel}" ONLY_IF_DIFFERENT)
    math(EXPR _count "${_count} + 1")
endforeach()
message(STATUS "Staged Collector: ${_count} file(s) from ${_src} -> ${DEST}")
