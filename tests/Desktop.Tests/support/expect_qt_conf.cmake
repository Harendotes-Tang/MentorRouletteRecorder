# cmake -DMR_EXECUTABLE=<MentorRecorder.Desktop.exe> -DMR_EXPECT_QT_CONF=ON|OFF -P expect_qt_conf.cmake
#
# A distribution build embeds :/qt/etc/qt.conf ("[Paths]" / "Prefix = .") so that Qt's prefix
# is the application folder and a planted <parent>\lib\libQt6Core.a cannot move plugin and QML
# lookup to <parent> (audit 2026-11-03 Z4-1, src/Desktop/CMakeLists.txt). A development build
# must not carry it: it loads Qt from the SDK and would stop finding the SDK's plugins. The file
# is too small for rcc to compress, so its two lines are plain strings in the executable.
if(NOT EXISTS "${MR_EXECUTABLE}")
    message(FATAL_ERROR "no executable at ${MR_EXECUTABLE}")
endif()
file(STRINGS "${MR_EXECUTABLE}" found REGEX "^(\\[Paths\\]|Prefix = \\.)$")
list(REMOVE_DUPLICATES found)
list(LENGTH found count)
if(MR_EXPECT_QT_CONF)
    if(NOT count EQUAL 2)
        message(FATAL_ERROR "the distribution build lacks the embedded qt.conf (found: ${found})")
    endif()
    message(STATUS "embedded qt.conf present: ${found}")
else()
    list(FIND found "Prefix = ." prefix)
    if(NOT prefix EQUAL -1)
        message(FATAL_ERROR "a development build carries the distribution's embedded qt.conf")
    endif()
    message(STATUS "no embedded qt.conf in this development build")
endif()
