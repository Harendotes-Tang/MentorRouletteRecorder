# QtGraphs 6.11.2 probes D3D12 even when Qt Quick uses D3D11. Build the narrowly
# patched, ABI-checked runtime beside this project, never inside the Qt SDK.
if(WIN32 AND MINGW AND Qt6Graphs_VERSION VERSION_EQUAL "6.11.2")
    find_program(MR_QTGRAPHS_PWSH NAMES pwsh pwsh.exe REQUIRED)
    get_filename_component(MR_QTGRAPHS_PREFIX "${Qt6_DIR}/../../.." ABSOLUTE)
    if(CMAKE_MAKE_PROGRAM MATCHES "[Nn]inja")
        set(MR_QTGRAPHS_NINJA "${CMAKE_MAKE_PROGRAM}")
    else()
        find_program(MR_QTGRAPHS_NINJA NAMES ninja ninja.exe REQUIRED)
    endif()
    set(MR_QTGRAPHS_TEST_ARGUMENTS)
    if(BUILD_TESTING)
        set(MR_QTGRAPHS_TEST_DIRECTORY "${CMAKE_BINARY_DIR}/tests/Desktop.Tests")
        if(CMAKE_CONFIGURATION_TYPES)
            string(APPEND MR_QTGRAPHS_TEST_DIRECTORY "/$<CONFIG>")
        endif()
        list(APPEND MR_QTGRAPHS_TEST_ARGUMENTS
            -TestDirectory "${MR_QTGRAPHS_TEST_DIRECTORY}")
    endif()
    add_custom_target(mr_stage_qtgraphs
        COMMAND "${MR_QTGRAPHS_PWSH}" -NoLogo -NoProfile -NonInteractive
            -File "${PROJECT_SOURCE_DIR}/scripts/bootstrap-qtgraphs.ps1"
            -QtPrefix "${MR_QTGRAPHS_PREFIX}"
            -CompilerPath "${CMAKE_CXX_COMPILER}"
            -CMakeExecutable "${CMAKE_COMMAND}"
            -NinjaExecutable "${MR_QTGRAPHS_NINJA}"
            -DesktopDirectory "$<TARGET_FILE_DIR:MentorRecorder.Desktop>"
            ${MR_QTGRAPHS_TEST_ARGUMENTS}
        COMMENT "Preparing the project-specific QtGraphs runtime"
        USES_TERMINAL
        VERBATIM)
    # Always run the hash check, including an up-to-date selected Creator target.
    # TARGET_FILE_DIR adds no reverse dependency under CMP0112 NEW.
    add_dependencies(MentorRecorder.Desktop mr_stage_qtgraphs)
    add_dependencies(MentorRecorderDesktopLib mr_stage_qtgraphs)
endif()
