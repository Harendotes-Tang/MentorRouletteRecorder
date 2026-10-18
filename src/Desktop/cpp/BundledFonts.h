#pragma once

namespace mr {

/// 在 QGuiApplication 创建后、QML 加载前注册程序内置的静态字体。
/// 字体由 Qt 字体库持有至进程退出，不安装到系统；全部注册成功时返回 true，
/// 任一资源无法读取或解析时记录警告并返回 false，已注册的字体仍保留。
bool registerBundledFonts();

} // namespace mr
