find_package(Qt6 6.8 REQUIRED COMPONENTS
    Core
    Gui
    Qml
    Quick
    QuickControls2
    Graphs
    Svg
    Test
    TextToSpeech
    Widgets)

# Personal journal/import workflows: controller lifecycle, real PNG output and
# screenshot evidence parsing are separate from the mock-only reflection tests.
foreach(workflow IN ITEMS ReflectionLibrary ReflectionShare ImportRecordsController ScreenshotImportParser OfflineOcrEngine JobIconClassifier DungeonComposition HistoryBatch)
    add_executable(MentorRecorder${workflow}Tests ${workflow}Tests.cpp)
    target_link_libraries(MentorRecorder${workflow}Tests PRIVATE
        MentorRecorderDesktopLib Qt6::Gui Qt6::Quick Qt6::Qml Qt6::QuickControls2 Qt6::Test Qt6::Widgets)
    target_compile_definitions(MentorRecorder${workflow}Tests PRIVATE
        MR_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
        MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
    set_target_properties(MentorRecorder${workflow}Tests PROPERTIES AUTOMOC TRUE)
    mr_use_ascii_autogen_dir(MentorRecorder${workflow}Tests)
    add_test(NAME MentorRecorder${workflow}
        COMMAND MentorRecorder${workflow}Tests
            -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorder${workflow}.qtlog.txt,txt")
    set_tests_properties(MentorRecorder${workflow} PROPERTIES
        ENVIRONMENT "QT_QPA_PLATFORM=offscreen;MR_DISABLE_SHARED_FETCH=1;MR_DISABLE_ONLINE_SPEECH=1;MR_DISABLE_UPDATE_CHECK=1")
endforeach()

add_executable(MentorRecorderDesktopTests
    DesktopTests.cpp)

target_link_libraries(MentorRecorderDesktopTests
    PRIVATE
        MentorRecorderDesktopLib
        Qt6::Gui
        Qt6::Test)

set_target_properties(MentorRecorderDesktopTests PROPERTIES
    AUTOMOC TRUE
    OUTPUT_NAME "MentorRecorderDesktopTests")

mr_use_ascii_autogen_dir(MentorRecorderDesktopTests)

add_test(NAME MentorRecorderDesktopTests
    COMMAND MentorRecorderDesktopTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderDesktopTests.qtlog.txt,txt")

add_executable(MentorRecorderAutomaticRecordingTests AutomaticRecordingTests.cpp)
target_link_libraries(MentorRecorderAutomaticRecordingTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
set_target_properties(MentorRecorderAutomaticRecordingTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderAutomaticRecordingTests)
add_test(NAME MentorRecorderAutomaticRecording COMMAND MentorRecorderAutomaticRecordingTests)

add_executable(MentorRecorderGameSelectionTests GameSelectionTests.cpp)
target_link_libraries(MentorRecorderGameSelectionTests PRIVATE MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
set_target_properties(MentorRecorderGameSelectionTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderGameSelectionTests)
add_test(NAME MentorRecorderGameSelection COMMAND MentorRecorderGameSelectionTests)

add_executable(MentorRecorderCandidateReviewTests CandidateReviewTests.cpp)
target_link_libraries(MentorRecorderCandidateReviewTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
target_compile_definitions(MentorRecorderCandidateReviewTests PRIVATE
    MR_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
    MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
set_target_properties(MentorRecorderCandidateReviewTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderCandidateReviewTests)
add_test(NAME MentorRecorderCandidateReview
    COMMAND MentorRecorderCandidateReviewTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderCandidateReview.qtlog.txt,txt")

# Button icons: components/Lucide.js and AppButton's icon slot, on real QML.
add_executable(MentorRecorderIconTests IconTests.cpp)
target_link_libraries(MentorRecorderIconTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
target_compile_definitions(MentorRecorderIconTests PRIVATE
    MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
set_target_properties(MentorRecorderIconTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderIconTests)
add_test(NAME MentorRecorderIcons
    COMMAND MentorRecorderIconTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderIcons.qtlog.txt,txt")

# The history filter's date picker (components/DateField.qml), on real QML.
add_executable(MentorRecorderDateFieldTests DateFieldTests.cpp)
target_link_libraries(MentorRecorderDateFieldTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
target_compile_definitions(MentorRecorderDateFieldTests PRIVATE
    MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
set_target_properties(MentorRecorderDateFieldTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderDateFieldTests)
add_test(NAME MentorRecorderDateField
    COMMAND MentorRecorderDateFieldTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderDateField.qtlog.txt,txt")

# The wizard's time input (components/TimeField.qml), on real QML.
add_executable(MentorRecorderTimeFieldTests TimeFieldTests.cpp)
target_link_libraries(MentorRecorderTimeFieldTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
target_compile_definitions(MentorRecorderTimeFieldTests PRIVATE
    MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
set_target_properties(MentorRecorderTimeFieldTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderTimeFieldTests)
add_test(NAME MentorRecorderTimeField
    COMMAND MentorRecorderTimeFieldTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderTimeField.qtlog.txt,txt")

# Tab enters a reason or note field's example (components/StyledTextField.qml), on real QML.
add_executable(MentorRecorderStyledTextFieldTests StyledTextFieldTests.cpp)
target_link_libraries(MentorRecorderStyledTextFieldTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
target_compile_definitions(MentorRecorderStyledTextFieldTests PRIVATE
    MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
set_target_properties(MentorRecorderStyledTextFieldTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderStyledTextFieldTests)
add_test(NAME MentorRecorderStyledTextField
    COMMAND MentorRecorderStyledTextFieldTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderStyledTextField.qtlog.txt,txt")

# The combo box popup (components/StyledComboBox.qml): no stray headers, wide enough.
add_executable(MentorRecorderStyledComboBoxTests StyledComboBoxTests.cpp)
target_link_libraries(MentorRecorderStyledComboBoxTests PRIVATE
    MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
target_compile_definitions(MentorRecorderStyledComboBoxTests PRIVATE
    MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
set_target_properties(MentorRecorderStyledComboBoxTests PROPERTIES AUTOMOC TRUE)
mr_use_ascii_autogen_dir(MentorRecorderStyledComboBoxTests)
add_test(NAME MentorRecorderStyledComboBox
    COMMAND MentorRecorderStyledComboBoxTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderStyledComboBox.qtlog.txt,txt")

# ---------------------------------------------------------------------------
# tst_ttsservice - template substitution, rate/volume mapping, live events.
# Kept in its own binary. Its announcements go to Qt's silent mock speech engine
# and never to the machine's own voice; without that plugin they are skipped.
# ---------------------------------------------------------------------------
add_executable(MentorRecorderTtsTests
    TtsServiceTests.cpp)

target_link_libraries(MentorRecorderTtsTests
    PRIVATE
        MentorRecorderDesktopLib
        Qt6::Gui
        Qt6::Test)

set_target_properties(MentorRecorderTtsTests PROPERTIES
    AUTOMOC TRUE
    OUTPUT_NAME "MentorRecorderTtsTests")

mr_use_ascii_autogen_dir(MentorRecorderTtsTests)

add_test(NAME MentorRecorderTtsService
    COMMAND MentorRecorderTtsTests)

# ---------------------------------------------------------------------------
# tst_reflections - 导随心得: the two new settings, the mock backend's
# SetRunReflection / GetReflectionSummary and 有心得 filter, and the controller's
# save / clear / 通关后弹出 rules. Needs no Collector and no speech engine.
# ---------------------------------------------------------------------------
add_executable(MentorRecorderReflectionTests
    ReflectionTests.cpp)

target_link_libraries(MentorRecorderReflectionTests
    PRIVATE
        MentorRecorderDesktopLib
        Qt6::Gui
        Qt6::Test)

set_target_properties(MentorRecorderReflectionTests PROPERTIES
    AUTOMOC TRUE
    OUTPUT_NAME "MentorRecorderReflectionTests")

mr_use_ascii_autogen_dir(MentorRecorderReflectionTests)

add_test(NAME MentorRecorderReflections
    COMMAND MentorRecorderReflectionTests)

# Regression suites; all use the same isolated runtime configured below.
# Speech: 在线语音 - SpeechController, TtsService routing, the tts-cache path check.
# CapturePage: 捕获诊断 in shipping QML - title row, 链路, 解析, player vocabulary.
# Motion: 动效 - durations collapse under ReduceMotion, theme reveal lifecycle, trend transitions.
# UpdateNotice: 检查新版本 - the projection of the Collector's verdict, the address
#   predicate, per-version dismissal, the 总览 banner, the switch and the disclosure.
# UpdateInstall: 下载并安装 - the download's projection and re-read, the three checks
#   before 立即安装 starts anything, the exit after it, the mock and the three places.
# InstallerHold: the installer held from its checksum to its start, on real files - a
#   held program still starts (a copy of whoami.exe), and nobody can replace it meanwhile.
# SingleInstance: one Desktop per user - the guard and the "show the first window" request.
# ShellDialog: Main.qml's own dialogs (原因, 首次引导, the wizard's retry) on the shipping window.
# RunDetailPanel: the history detail panel - player wording of 修正历史 / 事件摘要, clicks that stay in it.
# MockBackend: the offline backend answers in the Collector's shapes (paging, sort, event types, timestamps).
# QmlWarningCounter: the screenshot harness's own count of QML/JS runtime warnings.
foreach(MR_REVIEW_SUITE WorkflowController RunFormValidator ExportFailure UiWorkflowRegression EditRunDialogBinding
                        ReflectionDialogBinding Calibration SharedCalibration SharedCalibrationCard Speech
                        CapturePage Motion UpdateNotice UpdateInstall InstallerHold NoteImageStore SingleInstance ShellDialog
                        RunDetailPanel MockBackend QmlWarningCounter)
    add_executable(MentorRecorder${MR_REVIEW_SUITE}Tests ${MR_REVIEW_SUITE}Tests.cpp)
    target_link_libraries(MentorRecorder${MR_REVIEW_SUITE}Tests PRIVATE
        MentorRecorderDesktopLib Qt6::Gui Qt6::Test)
    set_target_properties(MentorRecorder${MR_REVIEW_SUITE}Tests PROPERTIES AUTOMOC TRUE)
    mr_use_ascii_autogen_dir(MentorRecorder${MR_REVIEW_SUITE}Tests)
    target_compile_definitions(MentorRecorder${MR_REVIEW_SUITE}Tests PRIVATE
        MR_SOURCE_DIR="${CMAKE_SOURCE_DIR}"
        MR_DESKTOP_QML_DIR="${PROJECT_SOURCE_DIR}/src/Desktop/qml")
    add_test(NAME MentorRecorder${MR_REVIEW_SUITE}
        COMMAND MentorRecorder${MR_REVIEW_SUITE}Tests
            -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorder${MR_REVIEW_SUITE}.qtlog.txt,txt")
endforeach()

# ---------------------------------------------------------------------------
# tst_ipcrequests - one request sample per message type, compared against the
# committed fixtures the Collector-side contract test validates against the
# JSON Schema. Kept in its own binary so it can be run, and regenerated, on its
# own while a contract change is being reviewed.
# ---------------------------------------------------------------------------
add_executable(MentorRecorderIpcRequestTests
    IpcRequestTests.cpp)

target_link_libraries(MentorRecorderIpcRequestTests
    PRIVATE
        MentorRecorderDesktopLib
        Qt6::Gui
        Qt6::Test)

target_compile_definitions(MentorRecorderIpcRequestTests PRIVATE
    MR_IPC_REQUEST_OUTPUT_DIR="${CMAKE_BINARY_DIR}/ipc-requests"
    MR_IPC_REQUEST_FIXTURE_DIR="${CMAKE_SOURCE_DIR}/tests/Fixtures/ipc-requests")

set_target_properties(MentorRecorderIpcRequestTests PROPERTIES
    AUTOMOC TRUE
    OUTPUT_NAME "MentorRecorderIpcRequestTests")

mr_use_ascii_autogen_dir(MentorRecorderIpcRequestTests)

add_test(NAME MentorRecorderIpcRequests
    COMMAND MentorRecorderIpcRequestTests
        -o "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderIpcRequests.txt,txt")

# ---------------------------------------------------------------------------
# tst_lifecycle - Collector supervision (restart backoff, serve-lease reuse,
# deliberate stop) and the settings file of older builds. Needs no Collector.
# ---------------------------------------------------------------------------
add_executable(MentorRecorderLifecycleTests
    LifecycleTests.cpp)

target_link_libraries(MentorRecorderLifecycleTests
    PRIVATE
        MentorRecorderDesktopLib
        Qt6::Gui
        Qt6::Test)

set_target_properties(MentorRecorderLifecycleTests PROPERTIES
    AUTOMOC TRUE
    OUTPUT_NAME "MentorRecorderLifecycleTests")

mr_use_ascii_autogen_dir(MentorRecorderLifecycleTests)

add_test(NAME MentorRecorderLifecycle
    COMMAND MentorRecorderLifecycleTests)

# ---------------------------------------------------------------------------
# tst_ipcintegration - the shipping IpcBackend against a real Collector child
# process on a temporary database. Skips cleanly when the Collector executable
# has not been copied next to the Desktop binary.
# ---------------------------------------------------------------------------
add_executable(MentorRecorderIpcIntegrationTests
    IpcIntegrationTests.cpp)

target_link_libraries(MentorRecorderIpcIntegrationTests
    PRIVATE
        MentorRecorderDesktopLib
        Qt6::Gui
        Qt6::Test)

set_target_properties(MentorRecorderIpcIntegrationTests PROPERTIES
    AUTOMOC TRUE
    OUTPUT_NAME "MentorRecorderIpcIntegrationTests")

mr_use_ascii_autogen_dir(MentorRecorderIpcIntegrationTests)

add_test(NAME MentorRecorderIpcIntegration
    COMMAND MentorRecorderIpcIntegrationTests
        -o "MentorRecorderIpcIntegration.txt,txt")

# Keep QtTest's narrow-character log filename relative on Windows. A non-ASCII
# absolute build path can fail to open before any test output is written.
set_tests_properties(MentorRecorderIpcIntegration PROPERTIES
    WORKING_DIRECTORY "$<TARGET_FILE_DIR:MentorRecorderIpcIntegrationTests>"
    TIMEOUT 120)

# ---------------------------------------------------------------------------
# QML smoke tests. Each one loads the whole scene graph offscreen and grabs a
# frame; a QML error makes objectCreationFailed exit non-zero. Each also
# asserts, through --verify-text, that the thing in its name is on screen (exit
# 8 otherwise), and fails on any QML/JS runtime warning (MR_QML_WARNING_REGEX
# below): a frame of the wrong page, or of a page whose bindings threw, is not
# a pass (review OJ-7).
# ---------------------------------------------------------------------------
add_test(NAME MentorRecorderQmlLoad
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/qml-smoke.png"
        --mock-recording-state listening
        --page 1
        --theme dark
        --screenshot-delay 150
        --verify-text "完成趋势"
        --verify-text "副本进行中")

# Opens the history detail side panel on the first row.
add_test(NAME MentorRecorderQmlDetailPanel
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/qml-detail.png"
        --mock-recording-state listening
        --page 2
        --theme dark
        --mock-open-detail
        --screenshot-delay 1500
        --verify-text "修正历史")

# Opens the manual-correction dialog on top of that panel.
add_test(NAME MentorRecorderQmlEditDialog
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/qml-edit.png"
        --mock-recording-state listening
        --page 2
        --theme dark
        --mock-open-edit
        --screenshot-delay 1700
        --verify-text "修正结果与进度，原记录会保留")

# The 新增遗漏记录 wizard on its 副本与职业 step: the whole catalogue list and the
# 最近打过 chips from the mock runs.
add_test(NAME MentorRecorderQmlCreateWizard
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/qml-create-wizard.png"
        --mock-recording-state listening
        --page 1
        --theme light
        --mock-open-create
        --mock-wizard-step 2
        --verify-text "共 380 个副本"
        --verify-text "最近打过"
        --screenshot-delay 1500)

# A mock run's fixture records keep their images out of the install folder
# (review OH-7): the wizard's time step names the scratch folder they go to.
add_test(NAME MentorRecorderQmlMockNoteImageFolder
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/qml-mock-note-images.png"
        --mock-recording-state listening
        --page 1
        --theme light
        --mock-open-create
        --mock-wizard-step 3
        --verify-text "MentorRecorder-mock-note-images"
        --screenshot-delay 1500)

# First-run baseline dialog. It opens at start-up, before the stored settings
# are read; by the screenshot it shows them (the mock's 1374, unchanged). The mock
# has recorded completions already, so the line under the fields previews the
# progress they add on top of the baseline (B3-1; CS7-D3).
add_test(NAME MentorRecorderQmlBaselineDialog
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/qml-baseline.png"
        --mock-recording-state listening
        --page 1
        --theme light
        --mock-first-run
        --screenshot-delay 1000
        --verify-text "你已经完成了多少次导随？"
        --verify-text "会加在基数之上")

# CLI boundary. Each of these must exit with code 2 exactly *and* print the
# refusal that names the option. WILL_FAIL would also accept a crash, a missing
# Qt DLL or an unrelated exit 3, so they go through expect_exit.cmake instead.
set(MR_EXPECT_EXIT "${CMAKE_CURRENT_SOURCE_DIR}/expect_exit.cmake")

# An unknown backend must never silently turn a requested live run into mock
# data. The process rejects it before loading QML.
add_test(NAME MentorRecorderDesktopRejectsInvalidBackend
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;invalid"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=invalid --backend value"
        -P "${MR_EXPECT_EXIT}")

# Mock-only knobs must not be accepted while the real IPC backend is selected,
# or the test-only flags would be silently ignored.
add_test(NAME MentorRecorderDesktopRejectsMockFlagsWithIpc
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;ipc;--mock-first-run"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=options require --backend mock"
        -P "${MR_EXPECT_EXIT}")

add_test(NAME MentorRecorderDesktopRejectsValidationMockWithIpc
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;ipc;--mock-validation-state;recording;--mock-formal-running;--mock-validation-keyboard"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=options require --backend mock"
        -P "${MR_EXPECT_EXIT}")

# Each state also asserts what the page actually says. Exit code 8 means the
# expected text was not on screen; a zero exit alone would accept a FAILED
# session rendered as “已保存”.
set(MR_VALIDATION_TEXT_waiting   "等待安全候选")
set(MR_VALIDATION_TEXT_recording "正在取证")
set(MR_VALIDATION_TEXT_stopping  "正在停止并排空")
set(MR_VALIDATION_TEXT_completed "已保存")
set(MR_VALIDATION_TEXT_failed    "ERR_TRACE_WRITE")

foreach(MR_VALIDATION_STATE waiting recording stopping completed failed)
    add_test(NAME MentorRecorderQmlValidation_${MR_VALIDATION_STATE}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/validation-${MR_VALIDATION_STATE}.png"
            --page 5
            --theme dark
            --screenshot-size 1100x720
            --screenshot-delay 1000
            --verify-text "${MR_VALIDATION_TEXT_${MR_VALIDATION_STATE}}"
            --mock-validation-state ${MR_VALIDATION_STATE})
endforeach()

# The midstream banner: capture RUNNING, nothing decoded, decode errors rising.
# docs/live-validation-guide.md section 6. Recording is otherwise possible (a
# verified profile), so the 无法自动记录 notice on screen is the midstream one the
# player sees in this situation, not an unrelated profile mismatch.
add_test(NAME MentorRecorderQmlMidstreamBanner
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/capture-midstream.png"
        --page 5
        --theme dark
        --screenshot-size 1100x900
        --screenshot-delay 1000
        --mock-midstream
        --mock-recording-state listening
        --verify-text "当前连接")

# The pending-review banner on the dashboard reads the count the Collector
# reported; it must not appear when there is nothing to review.
add_test(NAME MentorRecorderQmlPendingReviewBanner
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/dashboard-pending-review.png"
        --mock-recording-state listening
        --page 1
        --theme dark
        --screenshot-size 1280x900
        --screenshot-delay 1200
        --verify-text "条记录待复核")

# The 事件摘要 tab must render real GetRunEvents rows, never a placeholder. A
# player reads the event by its Chinese name. The first history row is a match
# cancelled before entry, so its trail is the pop (CONTENT_FINDER_POP = 匹配成功)
# and the cancellation (MATCH_CANCELLED = 匹配取消), never a 副本结算.
add_test(NAME MentorRecorderQmlRunEventsTab
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/history-events-tab.png"
        --mock-recording-state listening
        --page 2
        --theme dark
        --screenshot-size 1280x900
        --screenshot-delay 1600
        --mock-detail-tab events
        --verify-text "匹配成功"
        --verify-text "匹配取消")

add_test(NAME MentorRecorderQmlValidationDashboard
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/validation-dashboard.png"
        --page 1
        --theme dark
        --screenshot-size 1100x720
        --screenshot-delay 1000
        --mock-validation-state recording
        --verify-text "查看验证状态")

# A finished validation is history; the formal capture running beside it is what
# the dashboard projects (its stop button).
add_test(NAME MentorRecorderQmlValidationDashboardFormalRunning
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/validation-dashboard-formal-running.png"
        --page 1
        --theme dark
        --screenshot-size 1100x720
        --screenshot-delay 1000
        --mock-validation-state completed
        --mock-formal-running
        --verify-text "停止捕获")

add_test(NAME MentorRecorderQmlValidationKeyboard
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/validation-marker-focus.png"
        --page 5
        --theme dark
        --screenshot-size 1100x720
        --screenshot-delay 1000
        --mock-validation-state recording
        --mock-validation-keyboard)

foreach(MR_CANDIDATE_THEME dark light)
    add_test(NAME MentorRecorderQmlCandidateReview_${MR_CANDIDATE_THEME}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-candidates --page 7 --theme ${MR_CANDIDATE_THEME}
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/candidate-review-${MR_CANDIDATE_THEME}.png"
            --screenshot-size 1280x900 --screenshot-delay 1200
            --verify-text "对照核对")
endforeach()
add_test(NAME MentorRecorderQmlCandidateReview_compact
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --mock-candidates --page 7 --theme dark
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/candidate-review-compact.png"
        --screenshot-size 1100x720 --screenshot-delay 1200 --verify-text "对照核对")
add_test(NAME MentorRecorderQmlCandidateCapture
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --mock-candidates --page 5 --theme dark
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/candidate-capture.png"
        --screenshot-size 1280x900 --screenshot-delay 1200 --verify-text "开始候选捕获"
        --verify-text "排本登记" --verify-text "选用推荐")
add_test(NAME MentorRecorderQmlCandidateDisclosure
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --show-disclosure --mock-recording-state listening --theme dark
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/candidate-disclosure.png"
        --screenshot-size 1100x720 --screenshot-delay 1200
        --verify-text "运行顺序")

# 本机校准 in the shipping QML: the card on the capture page, the banner button
# and the sidebar label, each asserted through text the player actually reads.
set(MR_CALIBRATION_TEXT_observing "正在重新校准")
set(MR_CALIBRATION_TEXT_ready "核对并启用")
set(MR_CALIBRATION_TEXT_blocked "本机校准无法继续")
set(MR_CALIBRATION_TEXT_done "本机校准")
foreach(MR_CALIBRATION_STATE observing ready blocked done)
    add_test(NAME MentorRecorderQmlCalibration_${MR_CALIBRATION_STATE}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-calibration ${MR_CALIBRATION_STATE}
            --mock-live none --page 5 --theme dark
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/calibration-${MR_CALIBRATION_STATE}.png"
            --screenshot-size 1280x900 --screenshot-delay 1200
            --verify-text "${MR_CALIBRATION_TEXT_${MR_CALIBRATION_STATE}}")
endforeach()

# The READY banner button lives on every page, including the dashboard.
add_test(NAME MentorRecorderQmlCalibrationBanner
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --mock-calibration ready --mock-live none --page 1 --theme dark
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/calibration-banner.png"
        --screenshot-size 1280x800 --screenshot-delay 1200
        --verify-text "核对并启用")

# --mock-calibration is a mock-only knob like every other --mock-* option.
add_test(NAME MentorRecorderDesktopRejectsCalibrationMockWithIpc
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;ipc;--mock-calibration;ready"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=options require --backend mock"
        -P "${MR_EXPECT_EXIT}")

# 共享校准 on the capture page: the card's shared section in every state the
# mock can take, each asserted through the sentence a player reads.
set(MR_SHARED_TEXT_fetching "正在获取其他玩家的共享校准")
set(MR_SHARED_TEXT_verifying "找到共享校准")
set(MR_SHARED_TEXT_consent "同意，开始记录")
set(MR_SHARED_TEXT_verified "已使用其他玩家分享的校准")
# 核实门槛按来源分级（plans/shared-calibration.md §18）：登录时核实通过即记录，
# 排本与进本在记录中继续核对；未发布的导入码仍要全部判据通过才启用。
set(MR_SHARED_TEXT_verified-auditing "排本和进本还在核对中")
set(MR_SHARED_TEXT_imported-published "找到共享校准，登录时自动核实")
set(MR_SHARED_TEXT_imported-unpublished "已导入校准码，登录并排一次本")
set(MR_SHARED_TEXT_rejected "共享校准与本机流量对不上")
set(MR_SHARED_TEXT_unavailable "没取到共享校准")
set(MR_SHARED_TEXT_user-rejected "已按你的选择改为本机校准")
set(MR_SHARED_TEXT_none-for-build "还没有人分享这个版本的校准")
set(MR_SHARED_TEXT_share "分享给其他玩家")
set(MR_SHARED_QML_TESTS)
foreach(MR_SHARED_STATE fetching verifying consent verified verified-auditing imported-published
                        imported-unpublished rejected unavailable user-rejected none-for-build share)
    add_test(NAME MentorRecorderQmlSharedCalibration_${MR_SHARED_STATE}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-shared ${MR_SHARED_STATE}
            --mock-live none --page 5 --theme dark
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/shared-calibration-${MR_SHARED_STATE}.png"
            --screenshot-size 1280x900 --screenshot-delay 1200
            --verify-text "${MR_SHARED_TEXT_${MR_SHARED_STATE}}")
    list(APPEND MR_SHARED_QML_TESTS MentorRecorderQmlSharedCalibration_${MR_SHARED_STATE})
endforeach()

# 分享给其他玩家 after a restart: calibration is IDLE, its card is absent, and
# the always-visible 协议档案 card carries the button (docs/ui-design.md §4.4.2).
add_test(NAME MentorRecorderQmlSharedCalibration_share_after_restart
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --mock-calibration idle --mock-shared share
        --mock-live none --page 5 --theme dark
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/shared-calibration-share-after-restart.png"
        --screenshot-size 1280x900 --screenshot-delay 1200
        --verify-text "分享给其他玩家")
list(APPEND MR_SHARED_QML_TESTS MentorRecorderQmlSharedCalibration_share_after_restart)

add_test(NAME MentorRecorderDesktopRejectsSharedMockWithIpc
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;ipc;--mock-shared;consent"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=options require --backend mock"
        -P "${MR_EXPECT_EXIT}")

# 设置 (docs/ui-design.md §4.5): each sub-page opened through --settings-tab in
# the shipping QML, asserted through a line only that tab shows. The start-up
# self-check (appearanceSettingsCard / uiStyleSettingControl) runs in the
# alert-flow tests below.
set(MR_SETTINGS_TEXT_general "游戏更新后获取其他玩家的共享校准")
set(MR_SETTINGS_TEXT_tts "本地语音播报")
set(MR_SETTINGS_TEXT_goal "修改后立即重算")
set(MR_SETTINGS_TEXT_data "完整性校验")
set(MR_SETTINGS_TEXT_about "版权与来源")
set(MR_SETTINGS_QML_TESTS)
foreach(MR_SETTINGS_TAB general tts goal data about)
    add_test(NAME MentorRecorderQmlSettings_${MR_SETTINGS_TAB}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-recording-state listening --mock-live none --page 6 --theme dark
            --settings-tab ${MR_SETTINGS_TAB}
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/settings-${MR_SETTINGS_TAB}.png"
            --screenshot-size 1280x800 --screenshot-delay 900
            --verify-text "${MR_SETTINGS_TEXT_${MR_SETTINGS_TAB}}")
    list(APPEND MR_SETTINGS_QML_TESTS MentorRecorderQmlSettings_${MR_SETTINGS_TAB})
endforeach()

# 在线语音 on the 播报 tab (docs/ui-design.md §4.5): each mock state, asserted
# through the line the header shows. Nothing is sent: the mock has no client.
set(MR_SPEECH_TEXT_azure "播报文字会发送到 eastasia.azure-speech.invalid")
set(MR_SPEECH_TEXT_openai "播报文字会发送到 api.example.com")
set(MR_SPEECH_TEXT_unconfigured "在线语音 · 尚未配置")
set(MR_SPEECH_QML_TESTS)
foreach(MR_SPEECH_STATE azure openai unconfigured)
    add_test(NAME MentorRecorderQmlSpeech_${MR_SPEECH_STATE}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-recording-state listening --mock-live none --page 6 --theme dark
            --settings-tab tts --mock-speech ${MR_SPEECH_STATE}
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/settings-speech-${MR_SPEECH_STATE}.png"
            --screenshot-size 1280x800 --screenshot-delay 900
            --verify-text "${MR_SPEECH_TEXT_${MR_SPEECH_STATE}}")
    list(APPEND MR_SPEECH_QML_TESTS MentorRecorderQmlSpeech_${MR_SPEECH_STATE})
endforeach()
add_test(NAME MentorRecorderQmlSpeechConfirm
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --mock-recording-state listening --mock-live none --page 6 --theme light
        --settings-tab tts --mock-open-speech-confirm
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/settings-speech-confirm.png"
        --screenshot-size 1280x800 --screenshot-delay 900
        --verify-text "改用在线语音播报？")
list(APPEND MR_SPEECH_QML_TESTS MentorRecorderQmlSpeechConfirm)

add_test(NAME MentorRecorderDesktopRejectsSpeechMockWithIpc
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;ipc;--mock-speech;azure"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=options require --backend mock"
        -P "${MR_EXPECT_EXIT}")

add_test(NAME MentorRecorderDesktopRejectsInvalidSpeechMock
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;mock;--mock-speech;bogus"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=invalid --mock-speech value"
        -P "${MR_EXPECT_EXIT}")

# --speech-selftest needs an audio device to pass, so only its refusal is a
# ctest; scripts/package.ps1 -Verify plays a real file from the unpacked build.
add_test(NAME MentorRecorderDesktopSpeechSelfTestRejectsMissingFile
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--speech-selftest;${CMAKE_CURRENT_BINARY_DIR}/no-such-speech.wav"
        -DMR_EXPECT_CODE=3
        "-DMR_EXPECT_OUTPUT=speech selftest: no such file"
        -P "${MR_EXPECT_EXIT}")

# 下载并安装 (docs/privacy-boundary.md §8.6) in the three places that offer it,
# one held state each, asserted through the line a player reads. The download
# is simulated; nothing is fetched and a mock run never starts an installer.
set(MR_UPDATE_DOWNLOAD_ARGS_downloading --page 1)
set(MR_UPDATE_DOWNLOAD_TEXT_downloading "已下载 45%（27.0 / 60.0 MB）")
set(MR_UPDATE_DOWNLOAD_ARGS_verifying --page 1)
set(MR_UPDATE_DOWNLOAD_TEXT_verifying "正在核对发布时公布的校验值")
set(MR_UPDATE_DOWNLOAD_ARGS_ready --page 6 --settings-tab general)
set(MR_UPDATE_DOWNLOAD_TEXT_ready "立即安装")
set(MR_UPDATE_DOWNLOAD_ARGS_failed --page 6 --settings-tab about)
set(MR_UPDATE_DOWNLOAD_TEXT_failed "在浏览器中下载")
set(MR_UPDATE_QML_TESTS)
foreach(MR_UPDATE_DOWNLOAD_STATE downloading verifying ready failed)
    add_test(NAME MentorRecorderQmlUpdateDownload_${MR_UPDATE_DOWNLOAD_STATE}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-recording-state listening --mock-live none --theme dark
            --mock-update-download ${MR_UPDATE_DOWNLOAD_STATE}
            ${MR_UPDATE_DOWNLOAD_ARGS_${MR_UPDATE_DOWNLOAD_STATE}}
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/update-download-${MR_UPDATE_DOWNLOAD_STATE}.png"
            --screenshot-size 1280x800 --screenshot-delay 900
            --verify-text "${MR_UPDATE_DOWNLOAD_TEXT_${MR_UPDATE_DOWNLOAD_STATE}}")
    list(APPEND MR_UPDATE_QML_TESTS MentorRecorderQmlUpdateDownload_${MR_UPDATE_DOWNLOAD_STATE})
endforeach()

add_test(NAME MentorRecorderDesktopRejectsInvalidUpdateDownloadMock
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;mock;--mock-update-download;bogus"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=invalid --mock-update-download value"
        -P "${MR_EXPECT_EXIT}")

add_test(NAME MentorRecorderDesktopRejectsInvalidSettingsTab
    COMMAND ${CMAKE_COMMAND}
        -DMR_COMMAND=$<TARGET_FILE:MentorRecorder.Desktop>
        "-DMR_ARGS=--backend;mock;--settings-tab;bogus"
        -DMR_EXPECT_CODE=2
        "-DMR_EXPECT_OUTPUT=invalid --settings-tab value"
        -P "${MR_EXPECT_EXIT}")

set(MR_PERSONAL_QML_TESTS)
foreach(MR_PERSONAL_THEME dark light)
    foreach(MR_PERSONAL_VIEW Library Share Import)
        set(MR_PERSONAL_ARGS --page 8)
        set(MR_PERSONAL_TEXT "全部心得")
        if(MR_PERSONAL_VIEW STREQUAL "Share")
            set(MR_PERSONAL_ARGS --page 2 --mock-open-share)
            set(MR_PERSONAL_TEXT "生成分享图片")
        elseif(MR_PERSONAL_VIEW STREQUAL "Import")
            set(MR_PERSONAL_ARGS --page 2 --mock-open-import)
            set(MR_PERSONAL_TEXT "导入本人记录")
        endif()
        set(MR_PERSONAL_TEST MentorRecorderQml${MR_PERSONAL_VIEW}_${MR_PERSONAL_THEME})
        add_test(NAME ${MR_PERSONAL_TEST}
            COMMAND $<TARGET_FILE:MentorRecorder.Desktop> --backend mock --mock-recording-state listening
                ${MR_PERSONAL_ARGS} --theme ${MR_PERSONAL_THEME}
                --screenshot "${CMAKE_CURRENT_BINARY_DIR}/${MR_PERSONAL_TEST}.png"
                --screenshot-size 1100x720 --screenshot-delay 1200
                --verify-text "${MR_PERSONAL_TEXT}")
        list(APPEND MR_PERSONAL_QML_TESTS ${MR_PERSONAL_TEST})
    endforeach()
endforeach()

set(MR_QML_TESTS
    ${MR_PERSONAL_QML_TESTS}
    ${MR_SHARED_QML_TESTS}
    ${MR_SETTINGS_QML_TESTS}
    ${MR_SPEECH_QML_TESTS}
    ${MR_UPDATE_QML_TESTS}
    MentorRecorderQmlLoad
    MentorRecorderQmlCalibration_observing
    MentorRecorderQmlCalibration_ready
    MentorRecorderQmlCalibration_blocked
    MentorRecorderQmlCalibration_done
    MentorRecorderQmlCalibrationBanner
    MentorRecorderQmlCandidateReview_dark
    MentorRecorderQmlCandidateReview_light
    MentorRecorderQmlCandidateReview_compact
    MentorRecorderQmlCandidateCapture
    MentorRecorderQmlCandidateDisclosure
    MentorRecorderQmlDetailPanel
    MentorRecorderQmlEditDialog
    MentorRecorderQmlCreateWizard
    MentorRecorderQmlMockNoteImageFolder
    MentorRecorderQmlBaselineDialog)

get_target_property(MR_QT_CORE_LIBRARY Qt6::Core IMPORTED_LOCATION_RELEASE)
if(NOT MR_QT_CORE_LIBRARY)
    get_target_property(MR_QT_CORE_LIBRARY Qt6::Core IMPORTED_LOCATION)
endif()
get_filename_component(MR_QT_RUNTIME_DIR "${MR_QT_CORE_LIBRARY}" DIRECTORY)
get_filename_component(MR_CXX_RUNTIME_DIR "${CMAKE_CXX_COMPILER}" DIRECTORY)

set(MR_RECORDING_QML_TESTS)
set(MR_RECORDING_TEXT_waiting "等待游戏启动")
set(MR_RECORDING_TEXT_checking "检查中")
set(MR_RECORDING_TEXT_listening "自动监听中")
set(MR_RECORDING_TEXT_blocked "无法自动记录")
set(MR_RECORDING_TEXT_multiple "检测到多个游戏客户端")
set(MR_RECORDING_TEXT_multiple-exited "所选游戏已退出")
foreach(MR_RECORDING_STYLE classic eorzea)
    set(MR_ALERT_TEST MentorRecorderQmlAlertFlow_${MR_RECORDING_STYLE})
    add_test(NAME ${MR_ALERT_TEST}
        COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
            --backend mock --mock-recording-alert-flow --mock-live none
            --mock-ui-style ${MR_RECORDING_STYLE} --page 1 --theme dark
            --screenshot "${CMAKE_CURRENT_BINARY_DIR}/${MR_ALERT_TEST}.png"
            --screenshot-size 1100x720 --screenshot-delay 900)
    list(APPEND MR_RECORDING_QML_TESTS ${MR_ALERT_TEST})
    foreach(MR_RECORDING_STATE waiting checking listening blocked multiple multiple-exited)
        set(MR_RECORDING_TEST MentorRecorderQmlRecording_${MR_RECORDING_STYLE}_${MR_RECORDING_STATE})
        add_test(NAME ${MR_RECORDING_TEST}
            COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
                --backend mock --mock-recording-state ${MR_RECORDING_STATE}
                --mock-ui-style ${MR_RECORDING_STYLE} --mock-live none --page 1 --theme dark
                --screenshot "${CMAKE_CURRENT_BINARY_DIR}/${MR_RECORDING_TEST}.png"
                --screenshot-size 1100x720 --screenshot-delay 900
                --verify-text "${MR_RECORDING_TEXT_${MR_RECORDING_STATE}}")
        list(APPEND MR_RECORDING_QML_TESTS ${MR_RECORDING_TEST})
    endforeach()
endforeach()

# 游戏未运行、但采集服务已从安装目录读出版本：捕获诊断页（第 5 页）的 链路 一行
# 必须给出该版本的真实档案结论，而不是再让玩家等游戏启动。
set(MR_CLOSED_GAME_TEXT_waiting-verified
    "等待游戏启动 · 已安装版本的档案已就绪，启动游戏后会自动记录")
set(MR_CLOSED_GAME_TEXT_waiting-calibrating
    "等待游戏启动 · 已安装的游戏版本还没有可用档案，启动游戏后需要重新校准")
foreach(MR_CLOSED_GAME_STATE waiting-verified waiting-calibrating)
    foreach(MR_CLOSED_GAME_THEME light dark)
        set(MR_CLOSED_GAME_TEST
            MentorRecorderQmlClosedGame_${MR_CLOSED_GAME_THEME}_${MR_CLOSED_GAME_STATE})
        add_test(NAME ${MR_CLOSED_GAME_TEST}
            COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
                --backend mock --mock-recording-state ${MR_CLOSED_GAME_STATE}
                --mock-live none --page 5 --theme ${MR_CLOSED_GAME_THEME}
                --screenshot "${CMAKE_CURRENT_BINARY_DIR}/${MR_CLOSED_GAME_TEST}.png"
                --screenshot-size 1280x900 --screenshot-delay 900
                --verify-text "${MR_CLOSED_GAME_TEXT_${MR_CLOSED_GAME_STATE}}"
                --verify-text "已安装版本 2026.09.01")
        list(APPEND MR_RECORDING_QML_TESTS ${MR_CLOSED_GAME_TEST})
    endforeach()
endforeach()

# 共享校准正在获取时，链路不另下「需要重新校准」的结论，而是指向上方的校准卡片。
add_test(NAME MentorRecorderQmlClosedGame_dark_shared-fetching
    COMMAND $<TARGET_FILE:MentorRecorder.Desktop>
        --backend mock --mock-recording-state waiting-calibrating --mock-shared fetching
        --mock-live none --page 5 --theme dark
        --screenshot "${CMAKE_CURRENT_BINARY_DIR}/MentorRecorderQmlClosedGame_dark_shared-fetching.png"
        --screenshot-size 1280x900 --screenshot-delay 900
        --verify-text "等待游戏启动 · 已安装的游戏版本还没有可用档案 · 见上方校准卡片")
list(APPEND MR_RECORDING_QML_TESTS MentorRecorderQmlClosedGame_dark_shared-fetching)

set(MR_ALL_DESKTOP_TESTS
    MentorRecorderReflectionLibrary
    MentorRecorderReflectionShare
    MentorRecorderImportRecordsController
    MentorRecorderScreenshotImportParser
    MentorRecorderOfflineOcrEngine
    MentorRecorderJobIconClassifier
    MentorRecorderGameSelection
    ${MR_RECORDING_QML_TESTS}
    MentorRecorderAutomaticRecording
    MentorRecorderDesktopTests
    MentorRecorderCandidateReview
    MentorRecorderIcons
    MentorRecorderDateField
    MentorRecorderTimeField
    MentorRecorderStyledTextField
    MentorRecorderStyledComboBox
    MentorRecorderTtsService
    MentorRecorderReflections
    MentorRecorderWorkflowController
    MentorRecorderRunFormValidator
    MentorRecorderExportFailure
    MentorRecorderUiWorkflowRegression
    MentorRecorderEditRunDialogBinding
    MentorRecorderReflectionDialogBinding
    MentorRecorderCalibration
    MentorRecorderSharedCalibration
    MentorRecorderSharedCalibrationCard
    MentorRecorderSpeech
    MentorRecorderCapturePage
    MentorRecorderMotion
    MentorRecorderUpdateNotice
    MentorRecorderUpdateInstall
    MentorRecorderInstallerHold
    MentorRecorderNoteImageStore
    MentorRecorderSingleInstance
    MentorRecorderShellDialog
    MentorRecorderRunDetailPanel
    MentorRecorderMockBackend
    MentorRecorderQmlWarningCounter
    MentorRecorderIpcRequests
    MentorRecorderLifecycle
    MentorRecorderIpcIntegration
    MentorRecorderDesktopRejectsInvalidBackend
    MentorRecorderDesktopRejectsMockFlagsWithIpc
    MentorRecorderDesktopRejectsValidationMockWithIpc
    MentorRecorderDesktopRejectsCalibrationMockWithIpc
    MentorRecorderDesktopRejectsSharedMockWithIpc
    MentorRecorderDesktopRejectsSpeechMockWithIpc
    MentorRecorderDesktopRejectsInvalidSpeechMock
    MentorRecorderDesktopSpeechSelfTestRejectsMissingFile
    MentorRecorderDesktopRejectsInvalidSettingsTab
    MentorRecorderDesktopRejectsInvalidUpdateDownloadMock
    ${MR_QML_TESTS})

set(MR_VALIDATION_QML_TESTS
    MentorRecorderQmlMidstreamBanner
    MentorRecorderQmlPendingReviewBanner
    MentorRecorderQmlRunEventsTab
    MentorRecorderQmlValidation_waiting
    MentorRecorderQmlValidation_recording
    MentorRecorderQmlValidation_stopping
    MentorRecorderQmlValidation_completed
    MentorRecorderQmlValidation_failed
    MentorRecorderQmlValidationDashboard
    MentorRecorderQmlValidationDashboardFormalRunning
    MentorRecorderQmlValidationKeyboard)
list(APPEND MR_ALL_DESKTOP_TESTS ${MR_VALIDATION_QML_TESTS})

set_tests_properties(${MR_ALL_DESKTOP_TESTS} PROPERTIES
    ENVIRONMENT "QT_QPA_PLATFORM=offscreen")

# ---------------------------------------------------------------------------
# Keep process-supervision tests away from the user's Collector and database.
#
# AppController has no process authority unless a supervisor is explicitly
# injected, and tests inject one only with a stub they name themselves. MR_DATA_DIR
# keeps lease/takeover inspection away from the user's live serve.pid.
# MR_COLLECTOR_PATH matters only to a build compiled with
# MR_DEV_COLLECTOR_DISCOVERY, where it is authoritative and exclusive; a release
# build never reads it. Supervision fixtures override these values only with
# their own temporary stubs and directories.
#
# MentorRecorderIpcIntegration is included too: it launches its fixture
# Collector with a UUID pipe, temporary database/log paths and its own
# MR_DATA_DIR in the child environment, so these defaults protect the test
# process without changing the fixture child's setup.
set_property(TEST ${MR_ALL_DESKTOP_TESTS} APPEND PROPERTY ENVIRONMENT
    "MR_COLLECTOR_PATH=${CMAKE_CURRENT_BINARY_DIR}/no-such-collector/MentorRecorder.Collector.exe"
    "MR_DATA_DIR=${CMAKE_CURRENT_BINARY_DIR}/no-collector-data")

set_tests_properties(${MR_QML_TESTS} ${MR_VALIDATION_QML_TESTS} ${MR_RECORDING_QML_TESTS} PROPERTIES TIMEOUT 30)

# A QML or JavaScript runtime diagnostic names the QML file it comes from
# (qrc:/qt/qml/MentorRecorder/... or file:///...). The screenshot harness prints
# it to stderr ("[warning] <url>:<line>: TypeError: ...", main.cpp's message
# handler) and carries on to the frame, so a binding that throws, a failed
# property assignment or an unresolved name would otherwise still pass (review
# OJ-7). The harness counts them itself and exits 10 (QmlWarningCounter, review
# DT4-X5); this pattern stays as a second net, and it also sees a warning logged
# while the window is torn down. The harness's two environment notices (no font
# directory, no Direct3D 12) name no QML file and are not matched. No "\\[" in front: CTest's regex
# engine then fails to match the group that follows it.
set(MR_QML_WARNING_REGEX "(warning|critical|fatal)\\] (qrc|file):")
set_tests_properties(${MR_QML_TESTS} ${MR_VALIDATION_QML_TESTS} ${MR_RECORDING_QML_TESTS}
    PROPERTIES FAIL_REGULAR_EXPRESSION "${MR_QML_WARNING_REGEX}")

set_property(TEST ${MR_ALL_DESKTOP_TESTS} PROPERTY ENVIRONMENT_MODIFICATION
    "PATH=path_list_prepend:${MR_QT_RUNTIME_DIR}"
    "PATH=path_list_prepend:${MR_CXX_RUNTIME_DIR}")
