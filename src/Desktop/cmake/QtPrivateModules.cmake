include_guard(GLOBAL)

function(mr_find_qt_private_modules)
    # Qt's automatic private-module lookup uses the public module's EXACT
    # version. Keep this opt-in local to finding the ABI-dependent bridge.
    set(QT_FIND_PRIVATE_MODULES TRUE)
    file(REAL_PATH "${Qt6_DIR}/.." qt_package_root)
    foreach(module IN ITEMS Core Gui Qml Quick QuickTemplates2)
        file(REAL_PATH "${Qt6${module}_DIR}/.." module_root)
        if(NOT module_root STREQUAL qt_package_root OR
                NOT "${Qt6${module}_VERSION}" VERSION_EQUAL "${Qt6_VERSION}")
            message(FATAL_ERROR "Qt ${module} must match the selected Qt SDK and version.")
        endif()
    endforeach()
    foreach(module IN ITEMS Quick QuickTemplates2)
        find_package(Qt6${module}Private "${Qt6${module}_VERSION}" EXACT REQUIRED CONFIG
            PATHS "${qt_package_root}/Qt6${module}Private" NO_DEFAULT_PATH)
        file(REAL_PATH "${Qt6${module}Private_DIR}/.." private_root)
        if(NOT private_root STREQUAL qt_package_root)
            message(FATAL_ERROR "Qt ${module}Private must come from the public module's SDK.")
        endif()
    endforeach()

    # Package verification binds the DLLs to this build's SDK, including when
    # another same-version SDK or windeployqt is selected later.
    if(WIN32)
        file(REAL_PATH "${Qt6_DIR}/../../.." qt_prefix)
        set(runtime_hashes)
        foreach(module IN ITEMS Core Gui Qml Quick QuickTemplates2)
            foreach(suffix IN ITEMS "" "d")
                set(name "Qt6${module}${suffix}.dll")
                if(EXISTS "${qt_prefix}/bin/${name}")
                    file(SHA256 "${qt_prefix}/bin/${name}" digest)
                    list(APPEND runtime_hashes "\"${name}\":\"${digest}\"")
                endif()
            endforeach()
        endforeach()
        list(JOIN runtime_hashes ",\n" runtime_json)
        file(WRITE "${CMAKE_BINARY_DIR}/qt-private-runtime.json"
            "{\"schema_version\":1,\"qt_version\":\"${Qt6_VERSION}\",\"dll_sha256\":{${runtime_json}}}\n")
    endif()
endfunction()
