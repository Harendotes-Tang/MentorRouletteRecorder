# ---------------------------------------------------------------------------
# MentorRecorder.Desktop - C++20 / Qt 6 Quick desktop shell
#
# Hard boundary (see docs/privacy-boundary.md):
#   * this process never listens on a socket or a pipe;
#   * this process never performs an outbound network request;
#   * the only IPC is a *client* connection to the Collector's Named Pipe.
# ---------------------------------------------------------------------------

find_package(Qt6 6.8 REQUIRED COMPONENTS
    Core
    Gui
    Qml
    Quick
    QuickControls2
    Graphs
    Multimedia
    Svg
    Test
    TextToSpeech
    Widgets)

qt_standard_project_setup(REQUIRES 6.8)

set(CMAKE_AUTOMOC ON)
set(CMAKE_AUTORCC ON)

# ---------------------------------------------------------------------------
# Core library: everything except main(). The unit tests link this target so
# the IPC framing, the pipe-name derivation and the mock statistics can be
# exercised without starting a GUI.
# ---------------------------------------------------------------------------
qt_add_library(MentorRecorderDesktopLib STATIC)
mr_use_ascii_autogen_dir(MentorRecorderDesktopLib)

target_compile_features(MentorRecorderDesktopLib PUBLIC cxx_std_20)

# UpdateController prints this build's version beside the one the Collector
# reports, and it lives in the library the unit tests link, so the definition
# cannot be left on the executable alone. The full string, suffix included: this
# is what the title bar and the 关于 page show, and a test build must be
# recognisable as one there (MR_VERSION_FULL, root CMakeLists.txt).
target_compile_definitions(MentorRecorderDesktopLib
    PRIVATE MR_APP_VERSION="${MR_VERSION_FULL}")

target_include_directories(MentorRecorderDesktopLib
    PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/cpp)

target_link_libraries(MentorRecorderDesktopLib
    PUBLIC
        Qt6::Core
        Qt6::Gui
        Qt6::Qml
        Qt6::Quick
        Qt6::QuickControls2
        Qt6::Graphs
        Qt6::Multimedia
        Qt6::Svg
        Qt6::TextToSpeech
        Qt6::Widgets)

if(WIN32)
    # ConvertSidToStringSidW lives in advapi32; it is used only to derive the
    # per-user Named Pipe name. No other process is opened.
    target_link_libraries(MentorRecorderDesktopLib PUBLIC advapi32)
    # dwmapi: DwmSetWindowAttribute rounds the frameless window's corners on Windows 11.
    target_link_libraries(MentorRecorderDesktopLib PUBLIC dwmapi)
endif()

set(MR_CPP_SOURCES
    cpp/AutomaticRecordingController.cpp
    cpp/AutomaticRecordingController.h
    cpp/AppController.cpp
    cpp/AppController.h
    cpp/HistoryController.cpp
    cpp/HistoryController.h
    cpp/StatisticsController.cpp
    cpp/StatisticsController.h
    cpp/AppSettings.cpp
    cpp/AppSettings.h
    cpp/CaptureValidationController.cpp
    cpp/CaptureValidationController.h
    cpp/CalibrationController.cpp
    cpp/CalibrationController.h
    cpp/CandidateReviewController.cpp
    cpp/CandidateReviewController.h
    cpp/CollectorProcess.cpp
    cpp/CollectorProcess.h
    cpp/DutyCatalog.cpp
    cpp/DutyCatalog.h
    cpp/ExportController.cpp
    cpp/ExportController.h
    cpp/Formatters.cpp
    cpp/Formatters.h
    cpp/IBackend.cpp
    cpp/IBackend.h
    cpp/IpcBackend.cpp
    cpp/IpcBackend.h
    cpp/IpcClient.cpp
    cpp/IpcClient.h
    cpp/IpcFraming.cpp
    cpp/IpcFraming.h
    cpp/JobCatalog.cpp
    cpp/JobCatalog.h
    cpp/MockBackend.cpp
    cpp/MockBackendCandidates.cpp
    cpp/MockBackendShared.cpp
    cpp/MockBackendSpeech.cpp
    cpp/MockBackend.h
    cpp/MockBackendStats.cpp
    cpp/MockData.h
    cpp/PipeName.cpp
    cpp/PipeName.h
    cpp/RoleCatalog.cpp
    cpp/RoleCatalog.h
    cpp/RunFormValidator.cpp
    cpp/RunFormValidator.h
    cpp/RunListModel.cpp
    cpp/RunListModel.h
    cpp/SharedCalibrationController.cpp
    cpp/SharedCalibrationController.h
    cpp/SpeechAudioFile.cpp
    cpp/SpeechAudioFile.h
    cpp/SpeechController.cpp
    cpp/SpeechController.h
    cpp/SpeechPlayer.cpp
    cpp/SpeechPlayer.h
    cpp/StatsModels.cpp
    cpp/StatsModels.h
    cpp/TrayController.cpp
    cpp/TrayController.h
    cpp/TtsService.cpp
    cpp/TtsService.h
    cpp/TtsServiceOnline.cpp
    cpp/UpdateController.cpp
    cpp/UpdateController.h)

target_sources(MentorRecorderDesktopLib PRIVATE ${MR_CPP_SOURCES})

set(MR_QML_FILES
    qml/Main.qml
    qml/Theme.qml
    qml/charts/GraphsTrendChart.qml
    qml/charts/TrendChart.qml
    qml/charts/TrendMotionLayer.qml
    qml/components/AppButton.qml
    qml/components/Card.qml
    qml/components/CardKicker.qml
    qml/components/CalibrationCard.qml
    qml/components/CalibrationDialog.qml
    qml/components/CandidateValidationCard.qml
    qml/components/CapCell.qml
    qml/components/CategoryIcon.qml
    qml/components/Chip.qml
    qml/components/DateField.qml
    qml/components/DialogFrame.qml
    qml/components/Entrance.qml
    qml/components/FieldLabel.qml
    qml/components/HeadingLabel.qml
    qml/components/ImportCalibrationCodeDialog.qml
    qml/components/InsetBox.qml
    qml/components/JobIcon.qml
    qml/components/Lucide.js
    qml/components/NavItem.qml
    qml/components/OnlineSpeechPanel.qml
    qml/components/PageHeader.qml
    qml/components/PickChip.qml
    qml/components/PickSurface.qml
    qml/components/PanelDecoration.qml
    qml/components/PillSwitch.qml
    qml/components/ProgressRing.qml
    qml/components/ReflectionCard.qml
    qml/components/RoleIcon.qml
    qml/components/SegmentedControl.qml
    qml/components/StackedResultBar.qml
    qml/components/StatBar.qml
    qml/components/SettingToggleRow.qml
    qml/components/SettingsPanel.qml
    qml/components/SettingsRow.qml
    qml/components/SharedCalibrationSection.qml
    qml/components/SliderRow.qml
    qml/components/StatCard.qml
    qml/components/StyledComboBox.qml
    qml/components/StyledTextArea.qml
    qml/components/StyledTextField.qml
    qml/components/Tag.qml
    qml/components/ThemeReveal.qml
    qml/components/TimeField.qml
    qml/components/Toast.qml
    qml/components/PageHost.qml
    qml/components/ToggleSwitch.qml
    qml/components/TrendBars.qml
    qml/components/UpdateNotice.qml
    qml/dialogs/BaselineDialog.qml
    qml/dialogs/DisclosureDialog.qml
    qml/dialogs/EditRunDialog.qml
    qml/dialogs/OnlineSpeechConfirmDialog.qml
    qml/dialogs/ReflectionDialog.qml
    qml/dialogs/RunDetailPanel.qml
    qml/dialogs/RunWizardDutyStep.qml
    qml/dialogs/RunWizardResultStep.qml
    qml/dialogs/RunWizardTimeStep.qml
    qml/pages/CapturePage.qml
    qml/pages/CaptureChainPanel.qml
    qml/pages/CaptureMaintainerSection.qml
    qml/pages/CaptureNotices.qml
    qml/pages/CaptureNpcapPanel.qml
    qml/pages/CaptureParsePanel.qml
    qml/pages/CaptureToolsPanel.qml
    qml/pages/CaptureValidationEvidenceCard.qml
    qml/pages/CandidateReviewPage.qml
    qml/pages/DashboardPage.qml
    qml/pages/DungeonsPage.qml
    qml/pages/HistoryPage.qml
    qml/pages/JobsPage.qml
    qml/pages/SettingsAboutTab.qml
    qml/pages/SettingsDataTab.qml
    qml/pages/SettingsGeneralTab.qml
    qml/pages/SettingsGoalTab.qml
    qml/pages/SettingsPage.qml
    qml/pages/SettingsSpeechTab.qml)

# ---------------------------------------------------------------------------
# Icon resources.
#
# The job, role and content-type PNGs are FINAL FANTASY XIV art, (C) SQUARE ENIX,
# bundled under the FINAL FANTASY XIV Materials Usage License (non-commercial use
# with attribution; the notice is shown on the settings page and in
# THIRD_PARTY_NOTICES.md). They are not covered by this project's GPL licence.
# -DMR_BUNDLE_GAME_ICONS=OFF builds without them: the UI then draws text badges,
# and a player may supply their own PNGs under %LOCALAPPDATA%\MentorRecorder\icons\
# (JobCatalog and RoleCatalog look there first). See resources/icons/LICENSE-NOTE.md.
# ---------------------------------------------------------------------------
option(MR_BUNDLE_GAME_ICONS "Bundle the FFXIV icon PNGs (SQUARE ENIX art, Materials Usage License)" ON)
set(MR_ICON_JOBS "")
set(MR_ICON_JOBS_FRAMED "")
set(MR_ICON_CONTENT "")
set(MR_ICON_ROLES "")
if(MR_BUNDLE_GAME_ICONS)
    file(GLOB MR_ICON_JOBS        RELATIVE ${CMAKE_CURRENT_SOURCE_DIR} resources/icons/jobs/*.png)
    file(GLOB MR_ICON_JOBS_FRAMED RELATIVE ${CMAKE_CURRENT_SOURCE_DIR} resources/icons/jobs_framed/*.png)
    file(GLOB MR_ICON_CONTENT     RELATIVE ${CMAKE_CURRENT_SOURCE_DIR} resources/icons/content_types/*.png)
    file(GLOB MR_ICON_ROLES       RELATIVE ${CMAKE_CURRENT_SOURCE_DIR} resources/icons/roles/*.png)
    message(STATUS "Bundling FFXIV icons (SQUARE ENIX art, Materials Usage License, non-commercial)")
endif()

# The icon resources live on the *library*, not on the executable: JobCatalog
# and RoleCatalog read them from ":/resources/icons/...", and the unit tests
# link the library only.
qt_add_resources(MentorRecorderDesktopLib "mr_icons"
    PREFIX "/"
    BASE ${CMAKE_CURRENT_SOURCE_DIR}
    FILES
        ${MR_ICON_JOBS}
        ${MR_ICON_JOBS_FRAMED}
        ${MR_ICON_CONTENT}
        ${MR_ICON_ROLES}
        resources/icons/manifest.json
        resources/icons/content_types.json
        resources/icons/roles/manifest.json)

# ---------------------------------------------------------------------------
# Bundled duty reference data.
#
# Display only: it maps content_id to a level and an expansion so the history
# and statistics pages can fill the two cells $defs/Run and
# $defs/DungeonStatsRow are not allowed to carry. It never identifies a
# roulette and never makes a protocol profile usable
# (data/duties/README.md, docs/privacy-boundary.md section 7).
# ---------------------------------------------------------------------------
qt_add_resources(MentorRecorderDesktopLib "mr_duties"
    PREFIX "/"
    BASE "${CMAKE_CURRENT_SOURCE_DIR}/../.."
    FILES
        "${CMAKE_CURRENT_SOURCE_DIR}/../../data/duties/cn.2026-09-04.json")

# ---------------------------------------------------------------------------
# Bundled fonts.
#
# Cinzel is the eorzea style's Latin heading face; IBM Plex Mono carries the
# classic (workbench) style's figures. Both are licensed under the SIL Open Font
# License 1.1 (resources/fonts/OFL.txt, resources/fonts/IBMPlexMono-OFL.txt), so -
# unlike the icons above - they may be redistributed with this software.
# main.cpp registers every weight through QFontDatabase::addApplicationFont().
# ---------------------------------------------------------------------------
qt_add_resources(MentorRecorderDesktopLib "mr_fonts"
    PREFIX "/"
    BASE ${CMAKE_CURRENT_SOURCE_DIR}
    FILES
        resources/fonts/Cinzel-Regular.ttf
        resources/fonts/Cinzel-Bold.ttf
        resources/fonts/OFL.txt
        resources/fonts/IBMPlexMono-Medium.ttf
        resources/fonts/IBMPlexMono-SemiBold.ttf
        resources/fonts/IBMPlexMono-OFL.txt)

# ---------------------------------------------------------------------------
# Executable
# ---------------------------------------------------------------------------
# The executable icon is original artwork (resources/app/README.md); windres compiles it.
# CMake's default RC compiler is the bare name "windres", which only works when the
# toolchain's bin directory is on PATH at build time. Resolve it next to the C++ compiler.
if(MINGW AND (NOT CMAKE_RC_COMPILER OR CMAKE_RC_COMPILER STREQUAL "windres"))
    get_filename_component(_mr_toolchain_bin "${CMAKE_CXX_COMPILER}" DIRECTORY)
    find_program(MR_WINDRES NAMES windres llvm-windres x86_64-w64-mingw32-windres
        HINTS "${_mr_toolchain_bin}" NO_DEFAULT_PATH)
    if(MR_WINDRES)
        set(CMAKE_RC_COMPILER "${MR_WINDRES}" CACHE FILEPATH "Resource compiler" FORCE)
    endif()
endif()
enable_language(RC)

# ---------------------------------------------------------------------------
# Version stamping.
#
# MR_VERSION comes from Directory.Build.props through the root CMakeLists.txt.
# It reaches the executable by exactly two routes and no other:
#   * resources/app/app.rc.in -> ${CMAKE_CURRENT_BINARY_DIR}/app.rc, which is
#     what Explorer's "Details" tab and the installer's version check read;
#   * the MR_APP_VERSION compile definition, which main.cpp passes to
#     QApplication::setApplicationVersion (the --version output).
#
# Two shapes, deliberately: a Windows VERSIONINFO resource has four numeric
# fields and no room for a prerelease label, so FILEVERSION / PRODUCTVERSION and
# the FileVersion string use the numeric MR_VERSION, while the ProductVersion
# string - the one a person reads - carries MR_VERSION_FULL with its suffix.
# ---------------------------------------------------------------------------
set(MR_VERSION "${MentorRecorder_VERSION}")
set(MR_VERSION_MAJOR "${MentorRecorder_VERSION_MAJOR}")
set(MR_VERSION_MINOR "${MentorRecorder_VERSION_MINOR}")
set(MR_VERSION_PATCH "${MentorRecorder_VERSION_PATCH}")
set(MR_APP_RC "${CMAKE_CURRENT_BINARY_DIR}/app.rc")
configure_file(resources/app/app.rc.in "${MR_APP_RC}" @ONLY)
# windres resolves an ICON path against the directory of the script it is
# compiling, and it cannot open a path containing non-ASCII characters (this
# repository's name is Chinese by design - see mr_use_ascii_autogen_dir in the
# root CMakeLists.txt for the same problem in AUTOMOC). Copying the icon next to
# the generated script keeps the reference relative and the script pure ASCII.
configure_file(resources/app/app.ico "${CMAKE_CURRENT_BINARY_DIR}/app.ico" COPYONLY)

qt_add_executable(MentorRecorder.Desktop WIN32
    cpp/main.cpp
    cpp/Motion.cpp
    cpp/Motion.h
    "${MR_APP_RC}")

target_compile_definitions(MentorRecorder.Desktop
    PRIVATE MR_APP_VERSION="${MR_VERSION_FULL}")

mr_use_ascii_autogen_dir(MentorRecorder.Desktop)

target_link_libraries(MentorRecorder.Desktop
    PRIVATE
        MentorRecorderDesktopLib)

# ---------------------------------------------------------------------------
# Stage the C# Collector beside the Desktop executable.
#
# The Desktop launches MentorRecorder.Collector.exe from its own directory. An
# IDE build does not run scripts/build.ps1, so without this step the UI starts
# with "未找到 Collector" and every status stays at its default. Requires the
# .NET SDK on PATH; skipped with a notice otherwise.
# ---------------------------------------------------------------------------
option(MR_STAGE_COLLECTOR "Build the C# Collector and copy it next to MentorRecorder.Desktop.exe" ON)
find_program(MR_DOTNET_EXE dotnet)
get_filename_component(MR_REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)
if(MR_STAGE_COLLECTOR AND MR_DOTNET_EXE)
    add_custom_target(mr_stage_collector ALL
        COMMAND "${MR_DOTNET_EXE}" build
                "${MR_REPO_ROOT}/src/Collector/MentorRecorder.Collector.csproj"
                -c $<IF:$<CONFIG:Debug>,Debug,Release> --nologo -v q
        COMMAND "${CMAKE_COMMAND}"
                -DSRC_ROOT=${MR_REPO_ROOT}/src/Collector/bin
                -DCONFIG=$<IF:$<CONFIG:Debug>,Debug,Release>
                -DDEST=$<TARGET_FILE_DIR:MentorRecorder.Desktop>
                -P "${CMAKE_CURRENT_SOURCE_DIR}/cmake/StageCollector.cmake"
        COMMENT "Staging MentorRecorder.Collector beside the Desktop executable"
        VERBATIM)
    add_dependencies(mr_stage_collector MentorRecorder.Desktop)
else()
    message(STATUS "Collector staging skipped (MR_STAGE_COLLECTOR=${MR_STAGE_COLLECTOR}, dotnet=${MR_DOTNET_EXE}); run scripts/build.ps1 or set MR_COLLECTOR_PATH")
endif()


set_source_files_properties(qml/Theme.qml
    PROPERTIES QT_QML_SINGLETON_TYPE TRUE)

# Put the QML module directly on the executable.  Keeping QML out of the
# reusable C++ library avoids an unnecessary static QML plugin and lets the
# unit tests link only the production logic they exercise.
qt_add_qml_module(MentorRecorder.Desktop
    URI MentorRecorder
    VERSION 1.0
    RESOURCE_PREFIX /qt/qml
    NO_IMPORT_SCAN
    QML_FILES ${MR_QML_FILES})

set_target_properties(MentorRecorder.Desktop PROPERTIES
    OUTPUT_NAME "MentorRecorder.Desktop"
    QT_QML_MODULE_NO_IMPORT_SCAN TRUE)

# The screenshot / headless CLI needs console output on Windows; keep the GUI
# subsystem but write diagnostics through qInstallMessageHandler to stderr,
# which is attached when started from a console.
