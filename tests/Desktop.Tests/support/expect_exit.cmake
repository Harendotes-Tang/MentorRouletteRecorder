# ---------------------------------------------------------------------------
# Run a command and assert its exact exit code and, optionally, that its output
# contains an expected string.
#
# CTest's WILL_FAIL accepts *any* non-zero exit - a crash, a missing Qt DLL and
# a deliberate "exit 2, invalid option" are indistinguishable - and
# PASS_REGULAR_EXPRESSION ignores the exit code. The CLI boundary tests need
# both halves.
#
# Usage:
#   cmake -DMR_COMMAND=<exe> -DMR_ARGS=a;b;c -DMR_EXPECT_CODE=2
#         [-DMR_EXPECT_OUTPUT=<substring>] -P expect_exit.cmake
# ---------------------------------------------------------------------------

if(NOT DEFINED MR_COMMAND)
    message(FATAL_ERROR "MR_COMMAND is required")
endif()
if(NOT DEFINED MR_EXPECT_CODE)
    message(FATAL_ERROR "MR_EXPECT_CODE is required")
endif()

separate_arguments(MR_ARGS_LIST UNIX_COMMAND "")
if(DEFINED MR_ARGS)
    set(MR_ARGS_LIST ${MR_ARGS})
endif()

execute_process(
    COMMAND "${MR_COMMAND}" ${MR_ARGS_LIST}
    RESULT_VARIABLE actual_code
    OUTPUT_VARIABLE actual_stdout
    ERROR_VARIABLE actual_stderr)

message(STATUS "exit=${actual_code}")
message(STATUS "stdout=${actual_stdout}")
message(STATUS "stderr=${actual_stderr}")

if(NOT actual_code EQUAL MR_EXPECT_CODE)
    message(FATAL_ERROR
        "expected exit code ${MR_EXPECT_CODE}, got ${actual_code}")
endif()

if(DEFINED MR_EXPECT_OUTPUT AND NOT MR_EXPECT_OUTPUT STREQUAL "")
    set(combined "${actual_stdout}${actual_stderr}")
    string(FIND "${combined}" "${MR_EXPECT_OUTPUT}" found)
    if(found EQUAL -1)
        message(FATAL_ERROR
            "expected output to contain '${MR_EXPECT_OUTPUT}'")
    endif()
endif()
