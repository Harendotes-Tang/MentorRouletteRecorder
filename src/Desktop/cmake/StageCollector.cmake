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

foreach(v DEST)
    if(NOT DEFINED ${v})
        message(FATAL_ERROR "StageCollector.cmake: ${v} is required")
    endif()
endforeach()

if(DEFINED SOURCE_DIRECTORY)
    set(_candidates "${SOURCE_DIRECTORY}")
else()
    set(_candidates
    "${SRC_ROOT}/x64/${CONFIG}/net8.0-windows/win-x64"
    "${SRC_ROOT}/${CONFIG}/net8.0-windows/win-x64")
endif()

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

# Profiles are a managed subtree. A file missing from the rebuilt Collector must
# not survive a second staging into a Desktop or CTest directory.
if(IS_SYMLINK "${DEST}/protocol-profiles")
    message(FATAL_ERROR "Refusing to synchronize linked protocol-profiles directory")
endif()
file(GLOB_RECURSE _staged_profiles RELATIVE "${DEST}" "${DEST}/protocol-profiles/*.json")
foreach(rel IN LISTS _staged_profiles)
    if(NOT EXISTS "${_src}/${rel}")
        file(REMOVE "${DEST}/${rel}")
        if(EXISTS "${DEST}/${rel}" OR IS_SYMLINK "${DEST}/${rel}")
            message(FATAL_ERROR "StageCollector.cmake: cannot remove obsolete built-in profile: ${DEST}/${rel}")
        endif()
    endif()
endforeach()

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
