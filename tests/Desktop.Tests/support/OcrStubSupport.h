// The offline OCR engine played by the import test executable: a suite copies the executable
// into an application folder as the engine, and DesktopTestMain.cpp sends that copy back to the
// suite that started it, which answers in the engine's TSV.
#pragma once

#include "OfflineOcrEngine.h"

#include <QByteArray>
#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QString>
#include <QStringList>
#include <QTemporaryDir>

namespace mrtest {

/// The header line of the engine's TSV output.
inline const QByteArray &ocrTsvHeader()
{
    static const QByteArray value = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";
    return value;
}

/// The model files an installed engine needs, each of them non-empty.
inline const QStringList &ocrModelFiles()
{
    static const QStringList value{QStringLiteral("PP-OCRv6_det_small.onnx"),
                                   QStringLiteral("PP-OCRv6_rec_small.onnx"),
                                   QStringLiteral("ch_ppocr_mobile_v2.0_cls_mobile.onnx")};
    return value;
}

/// Installs this executable as the OCR engine of the application folder \a relativeApp inside
/// \a directory, with every model file in place, where mr::OfflineOcrEngine looks for them.
/// Returns that application folder, or an empty string when any step failed.
inline QString installOcrStub(const QTemporaryDir &directory, const QString &relativeApp = QStringLiteral("app with spaces"))
{
    const QString app = directory.filePath(relativeApp);
    const QString models = mr::OfflineOcrEngine::modelsPath(app);
    if (!QDir().mkpath(models)
        || !QFile::copy(QCoreApplication::applicationFilePath(), mr::OfflineOcrEngine::enginePath(app)))
        return {};
    for (const QString &name : ocrModelFiles()) {
        QFile model(QDir(models).filePath(name));
        const QByteArray content("synthetic-model");
        if (!model.open(QIODevice::WriteOnly) || model.write(content) != content.size())
            return {};
    }
    return app;
}

} // namespace mrtest
