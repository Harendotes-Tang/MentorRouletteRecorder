#include "OfflineOcrEngine.h"
#include "ScreenshotOcrText.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QImage>
#include <QImageReader>
#include <QProcess>
#include <QProcessEnvironment>

#ifdef Q_OS_WIN
#include <qt_windows.h>
#endif

namespace mr {

OfflineOcrEngine::OfflineOcrEngine(QObject *parent)
    : QObject(parent), m_applicationDirectory(QCoreApplication::applicationDirPath())
{
    m_timeout.setSingleShot(true);
    connect(&m_timeout, &QTimer::timeout, this, [this] {
        stopWithError(tr("本地图片识别超时，已停止。可以减少图片或分批重试。"));
    });
}

OfflineOcrEngine::~OfflineOcrEngine()
{
    m_timeout.stop();
    if (m_process) {
        m_process->disconnect(this);
        m_process->kill();
        m_process->waitForFinished(1000);
        delete m_process.data();
    }
}

QString OfflineOcrEngine::enginePath(const QString &applicationDirectory)
{
    return QDir(applicationDirectory).filePath(QStringLiteral("ocr/local-ai-ocr.exe"));
}

QString OfflineOcrEngine::modelsPath(const QString &applicationDirectory)
{
    return QDir(applicationDirectory).filePath(QStringLiteral("ocr/models"));
}

QString OfflineOcrEngine::dependencyError(const QString &applicationDirectory)
{
    if (!QFileInfo(enginePath(applicationDirectory)).isFile())
        return tr("缺少随应用安装的本地 AI 文字识别引擎。请使用包含 OCR 的安装包，或先导入表格/JSON。识别不会上传图片。");
    // Runtime deployment validates the complete pinned payload. The worker also
    // checks each fixed model hash before loading any inference session.
    for (const QString &name : {QStringLiteral("PP-OCRv6_det_small.onnx"),
                               QStringLiteral("PP-OCRv6_rec_small.onnx"),
                               QStringLiteral("ch_ppocr_mobile_v2.0_cls_mobile.onnx")}) {
        const QFileInfo file(QDir(modelsPath(applicationDirectory)).filePath(name));
        if (!file.isFile() || file.size() <= 0)
            return tr("本地 AI 文字识别模型不完整，请修复 OCR 安装后重试。");
    }
    return {};
}

void OfflineOcrEngine::setApplicationDirectoryForTesting(const QString &directory)
{
    if (!m_busy && QFileInfo(directory).isAbsolute())
        m_applicationDirectory = QDir::cleanPath(directory);
}

void OfflineOcrEngine::setTimeoutForTesting(int milliseconds)
{
    if (!m_busy)
        m_timeoutMs = qBound(1, milliseconds, DefaultTimeoutMs);
}

QString OfflineOcrEngine::currentFile() const
{
    return m_index < m_files.size() ? QFileInfo(m_files[m_index]).fileName() : QString();
}

bool OfflineOcrEngine::start(const QStringList &files)
{
    if (m_busy)
        return false;
    m_files = files;
    m_index = 0;
    m_cancelled = false;
    m_failure.clear();
    if (files.isEmpty() || files.size() > MaxImages) {
        Q_EMIT failed(tr("一次请选择 1 至 %1 张图片。").arg(MaxImages));
        return false;
    }
    const QString missing = dependencyError(m_applicationDirectory);
    if (!missing.isEmpty()) {
        Q_EMIT failed(missing);
        return false;
    }
    // Validate all headers first; oversized images are never eagerly decoded.
    for (const QString &path : files) {
        const QFileInfo info(path);
        QImageReader reader(path);
        const QSize size = reader.size();
        if (!info.isAbsolute() || !info.isFile() || info.size() <= 0 || info.size() > MaxInputBytes
            || !size.isValid() || qint64(size.width()) * size.height() > MaxPixels) {
            Q_EMIT failed(tr("图片无法读取或超过限制（20 MiB、5000 万像素）：%1").arg(info.fileName()));
            return false;
        }
    }
    m_busy = true;
    const quint64 generation = ++m_generation;
    Q_EMIT changed();
    queueNext(generation);
    return true;
}

void OfflineOcrEngine::cancel()
{
    if (!m_busy)
        return;
    m_cancelled = true;
    if (m_process && m_process->state() != QProcess::NotRunning)
        m_process->kill();
    else
        settle();
}

void OfflineOcrEngine::stopWithError(const QString &message)
{
    if (!m_busy)
        return;
    m_failure = message;
    if (m_process && m_process->state() != QProcess::NotRunning)
        m_process->kill();
    else
        settle();
}

void OfflineOcrEngine::queueNext(quint64 generation)
{
    QTimer::singleShot(0, this, [this, generation] {
        if (generation == m_generation)
            startNext();
    });
}

void OfflineOcrEngine::startNext()
{
    if (!m_busy || m_process)
        return;
    if (m_cancelled || !m_failure.isEmpty() || m_index >= m_files.size()) {
        settle();
        return;
    }
    m_work = std::make_unique<QTemporaryDir>(QDir::tempPath() + QStringLiteral("/mentor-record-ocr-XXXXXX"));
    if (!m_work->isValid()) {
        stopWithError(tr("无法创建本地识别临时目录，请检查磁盘空间和权限。"));
        return;
    }
    QImageReader reader(m_files[m_index]);
    reader.setAutoTransform(true);
    const QImage image = reader.read();
    const QString input = m_work->filePath(QStringLiteral("input.png"));
    if (image.isNull() || !image.save(input, "PNG")) {
        stopWithError(tr("无法解码图片或保存识别副本：%1").arg(currentFile()));
        return;
    }
    m_imageSize = image.size();
    m_outputFile = m_work->filePath(QStringLiteral("recognized.tsv"));
    m_timeout.start(m_timeoutMs);
    launchProcess(input);
}

void OfflineOcrEngine::launchProcess(const QString &input)
{
    m_diagnostics.clear();
    auto *process = new QProcess(this);
    m_process = process;
    const quint64 generation = m_generation;
#ifdef Q_OS_WIN
    process->setCreateProcessArgumentsModifier([](QProcess::CreateProcessArguments *arguments) {
        arguments->flags |= CREATE_NO_WINDOW;
    });
#endif
    auto environment = QProcessEnvironment::systemEnvironment();
    for (const QString &name : {QStringLiteral("PYTHONHOME"), QStringLiteral("PYTHONPATH"), QStringLiteral("PYTHONSTARTUP")})
        environment.remove(name);
    for (const QString &name : {QStringLiteral("OMP_NUM_THREADS"), QStringLiteral("OPENBLAS_NUM_THREADS"), QStringLiteral("MKL_NUM_THREADS")})
        environment.insert(name, QStringLiteral("2"));
    process->setProcessEnvironment(environment);
    process->setWorkingDirectory(m_work->path());
    process->setProgram(enginePath(m_applicationDirectory));
    process->setArguments({QStringLiteral("--input"), input, QStringLiteral("--output"), m_outputFile,
                           QStringLiteral("--models"), modelsPath(m_applicationDirectory)});
    // Drain pipes without recording OCR text; diagnostic retention is bounded.
    connect(process, &QProcess::readyReadStandardError, this, [this, process, generation] {
        const QByteArray bytes = process->readAllStandardError();
        if (generation == m_generation && process == m_process && m_diagnostics.size() < 8192)
            m_diagnostics += bytes.left(8192 - m_diagnostics.size());
    });
    connect(process, &QProcess::readyReadStandardOutput, this, [process] { process->readAllStandardOutput(); });
    connect(process, &QProcess::errorOccurred, this, [this, process, generation](QProcess::ProcessError error) {
        if (generation == m_generation && process == m_process && error == QProcess::FailedToStart)
            stopWithError(tr("本地 AI 识别引擎无法启动，请检查随包 OCR 依赖是否完整。"));
    });
    connect(process, qOverload<int, QProcess::ExitStatus>(&QProcess::finished), this,
            [this, process, generation](int exitCode, QProcess::ExitStatus status) {
        if (generation == m_generation && process == m_process)
            processFinished(exitCode, status == QProcess::CrashExit);
    });
    auto *outputCheck = new QTimer(process);
    outputCheck->setInterval(250);
    connect(outputCheck, &QTimer::timeout, this, [this, process, generation] {
        if (generation == m_generation && process == m_process
            && (QFileInfo(m_outputFile).size() > MaxOutputBytes
                || QFileInfo(m_outputFile + QStringLiteral(".partial")).size() > MaxOutputBytes))
            stopWithError(tr("识别结果超过 8 MiB，已停止。请分批裁切后重试。"));
    });
    outputCheck->start();
    process->start();
    Q_EMIT changed();
}

void OfflineOcrEngine::processFinished(int exitCode, bool crashed)
{
    if (m_process) {
        m_process->disconnect(this);
        m_process->deleteLater();
        m_process = nullptr;
    }
    if (m_cancelled || !m_failure.isEmpty()) {
        settle();
        return;
    }
    QFile output(m_outputFile);
    if (crashed || exitCode != 0 || !output.open(QIODevice::ReadOnly) || output.size() > MaxOutputBytes) {
        stopWithError(tr("本地 AI 识别未完成：%1。请核对图片或修复 OCR 安装。").arg(currentFile()));
        return;
    }
    const QByteArray tsv = output.read(MaxOutputBytes + 1);
    output.close();
    if (tsv.size() > MaxOutputBytes) {
        stopWithError(tr("识别结果超过允许大小，已停止。"));
        return;
    }
    QList<screenshot::Line> lines;
    QString error;
    if (!screenshot::readLines(tsv, m_imageSize, lines, error)) {
        stopWithError(tr("本地 AI 识别结果无法校验：%1").arg(error));
        return;
    }
    finishImage(tsv);
}

void OfflineOcrEngine::finishImage(QByteArray tsv)
{
    const quint64 generation = m_generation;
    m_timeout.stop();
    const QString sourcePath = m_files[m_index];
    ++m_index;
    m_imageSize = {};
    m_outputFile.clear();
    m_work.reset();
    Q_EMIT imageRecognized(sourcePath, tsv);
    if (!m_busy || generation != m_generation)
        return;
    Q_EMIT changed();
    queueNext(generation);
}

void OfflineOcrEngine::settle()
{
    const quint64 terminalGeneration = ++m_generation;
    m_timeout.stop();
    if (m_process) {
        m_process->disconnect(this);
        m_process->deleteLater();
        m_process = nullptr;
    }
    m_work.reset();
    m_imageSize = {};
    m_outputFile.clear();
    const bool complete = !m_cancelled && m_failure.isEmpty() && m_index == m_files.size();
    const QString failure = m_failure;
    m_busy = false;
    Q_EMIT changed();
    if (terminalGeneration != m_generation)
        return;
    if (!failure.isEmpty()) {
        Q_EMIT failed(failure);
        if (terminalGeneration != m_generation)
            return;
    }
    Q_EMIT finished(complete);
}

} // namespace mr
