cmake_minimum_required(VERSION 3.24)

# ---------------------------------------------------------------------------
# Single source of truth for the product version.
#
# Directory.Build.props carries it in two properties: <VersionPrefix> (three
# plain numbers) and an optional <VersionSuffix> (a prerelease label such as
# `beta.1`). Together they are what MSBuild stamps into the Collector assembly,
# what scripts/package.ps1 writes into BUILD-METADATA.json and passes to ISCC,
# and - through the derivations below - what ends up in the Desktop executable's
# VERSIONINFO resource and its --version output.
# No second literal may be introduced here: independent copies of the version
# drift apart.
#
# Two values come out of this block and they are not interchangeable:
#   MR_VERSION_PREFIX  the three numbers, for anything that must be numeric
#                      (project(VERSION), the RC VERSIONINFO fields);
#   MR_VERSION_FULL    what a human reads, suffix included (1.4.0-beta.1).
# ---------------------------------------------------------------------------
set(MR_VERSION_PROPS "${CMAKE_CURRENT_LIST_DIR}/Directory.Build.props")
if(NOT EXISTS "${MR_VERSION_PROPS}")
    message(FATAL_ERROR "MentorRecorder: ${MR_VERSION_PROPS} not found; it defines the version.")
endif()
file(STRINGS "${MR_VERSION_PROPS}" MR_VERSION_LINES
    REGEX "<VersionPrefix>[^<]*</VersionPrefix>")
if(NOT MR_VERSION_LINES)
    message(FATAL_ERROR
        "MentorRecorder: no <VersionPrefix>x.y.z</VersionPrefix> in ${MR_VERSION_PROPS}.")
endif()
list(GET MR_VERSION_LINES 0 MR_VERSION_LINE)
string(REGEX MATCH "[0-9]+\\.[0-9]+\\.[0-9]+" MR_VERSION_PREFIX "${MR_VERSION_LINE}")
if(NOT MR_VERSION_PREFIX)
    message(FATAL_ERROR
        "MentorRecorder: <VersionPrefix> in ${MR_VERSION_PROPS} is not x.y.z: ${MR_VERSION_LINE}")
endif()

# The suffix is optional: an empty <VersionSuffix/> or no element at all is a
# release. A malformed one is refused rather than silently dropped, because a
# test build that loses its suffix looks exactly like the release.
set(MR_VERSION_SUFFIX "")
file(STRINGS "${MR_VERSION_PROPS}" MR_VERSION_SUFFIX_LINES
    REGEX "<VersionSuffix>[^<]*</VersionSuffix>")
if(MR_VERSION_SUFFIX_LINES)
    list(GET MR_VERSION_SUFFIX_LINES 0 MR_VERSION_SUFFIX_LINE)
    string(REGEX MATCH "<VersionSuffix>([^<]*)</VersionSuffix>" _mr_unused "${MR_VERSION_SUFFIX_LINE}")
    string(STRIP "${CMAKE_MATCH_1}" MR_VERSION_SUFFIX)
    if(MR_VERSION_SUFFIX AND NOT MR_VERSION_SUFFIX MATCHES "^[0-9A-Za-z.-]+$")
        message(FATAL_ERROR
            "MentorRecorder: <VersionSuffix> in ${MR_VERSION_PROPS} is not a prerelease "
            "label such as beta.1: ${MR_VERSION_SUFFIX}")
    endif()
endif()

if(MR_VERSION_SUFFIX)
    set(MR_VERSION_FULL "${MR_VERSION_PREFIX}-${MR_VERSION_SUFFIX}")
else()
    set(MR_VERSION_FULL "${MR_VERSION_PREFIX}")
endif()

# Re-run CMake when the props file changes so a version bump cannot be carried
# by a stale cache into the resource script or the compile definition.
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${MR_VERSION_PROPS}")
message(STATUS "MentorRecorder version ${MR_VERSION_FULL} (from Directory.Build.props)")

project(MentorRecorder
    VERSION ${MR_VERSION_PREFIX}
    DESCRIPTION "FF14 Mentor Roulette Recorder - desktop UI (Qt 6 Quick)"
    LANGUAGES CXX)

include(CTest)

# Qt's MinGW tools cannot reliably create AUTOMOC output below a path containing
# non-ASCII characters, and the repository name is Chinese. Normal build products
# stay in the requested build directory; only disposable autogen files are placed
# below the ASCII-only system temp path.
function(mr_use_ascii_autogen_dir target_name)
    if(WIN32 AND "${CMAKE_BINARY_DIR}" MATCHES "[^ -~]"
       AND DEFINED ENV{TEMP} AND NOT "$ENV{TEMP}" STREQUAL "")
        string(SHA256 autogen_key "${CMAKE_BINARY_DIR}|${target_name}")
        string(SUBSTRING "${autogen_key}" 0 16 autogen_key)
        file(TO_CMAKE_PATH "$ENV{TEMP}" temp_root)
        set_property(TARGET "${target_name}" PROPERTY AUTOGEN_BUILD_DIR
            "${temp_root}/MentorRecorder/autogen-${autogen_key}")
    endif()
endfunction()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# --------------------------------------------------------------------------
# Descend into src/Desktop only once it carries its own CMakeLists.txt, so a
# fresh clone configures cleanly at every phase.
# --------------------------------------------------------------------------
if(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/src/Desktop/CMakeLists.txt")
    add_subdirectory(src/Desktop)
    if(BUILD_TESTING AND EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/tests/Desktop.Tests/CMakeLists.txt")
        add_subdirectory(tests/Desktop.Tests)
    endif()
else()
    message(STATUS
        "MentorRecorder: src/Desktop/CMakeLists.txt not present yet - "
        "the Qt 6 Desktop target is introduced in Phase 1. Nothing to build.")
endif()
