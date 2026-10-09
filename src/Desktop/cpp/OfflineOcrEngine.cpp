#include "OfflineOcrEngine.h"
#include "ScreenshotImportParser.h"

#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QImageReader>
#include <QProcess>
#include <QProcessEnvironment>
#include <QRegularExpression>
#include <QVariantMap>

#include <algorithm>
#include <array>
#include <cmath>
#include <vector>

#ifdef Q_OS_WIN
#include <qt_windows.h>
#endif

namespace {

constexpr int kFieldBorder = 16;
constexpr int kMaximumFields = 96;
constexpr qint64 kMaximumFieldPixels = 8'000'000;
constexpr auto kTsvHeader = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

struct OcrWord {
    QList<QByteArray> columns;
    QRect rect;
    QString text;
};

QList<OcrWord> wordRows(const QByteArray &tsv, const QSize &size)
{
    QList<OcrWord> result;
    const auto rows = tsv.split('\n');
    if (rows.isEmpty() || !rows.front().startsWith("level\tpage_num\t"))
        return {};
    for (QByteArray row : rows) {
        if (row.endsWith('\r'))
            row.chop(1);
        const auto columns = row.split('\t');
        if (columns.size() != 12 || columns[0] != "5" || columns[11].trimmed().isEmpty())
            continue;
        std::array<int, 4> geometry{};
        bool valid = true;
        for (int i = 0; i < 4; ++i) {
            bool ok = false;
            geometry[i] = columns[6 + i].toInt(&ok);
            valid &= ok && geometry[i] >= 0;
        }
        if (!valid || geometry[2] <= 0 || geometry[3] <= 0
            || qint64(geometry[0]) + geometry[2] > size.width()
            || qint64(geometry[1]) + geometry[3] > size.height())
            return {};
        result.append({columns, QRect(geometry[0], geometry[1], geometry[2], geometry[3]),
                       QString::fromUtf8(columns[11]).trimmed()});
        if (result.size() > 100'000)
            return {};
    }
    return result;
}

QByteArray serializedWords(const QList<OcrWord> &words)
{
    QByteArray result(kTsvHeader);
    for (const auto &word : words) {
        result += word.columns.join('\t');
        result += '\n';
    }
    return result;
}

int hanCount(const QList<OcrWord> &words)
{
    int count = 0;
    for (const auto &word : words)
        for (const QChar character : word.text)
            count += character.script() == QChar::Script_Han;
    return count;
}

/** @brief 从卡片标题区内的独立蓝色方形图标定位副本标题，排除外部手机网页背景。 */
QList<QRect> blueHeaderIcons(const QImage &image, const QRect &searchArea)
{
    const QRect search = searchArea.intersected(image.rect());
    if (search.isEmpty())
        return {};
    const int width = search.width();
    const int height = search.height();
    std::vector<uchar> pixels(size_t(width) * size_t(height), 0);
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x) {
            const QRgb color = image.pixel(search.left() + x, search.top() + y);
            pixels[size_t(y) * width + x] = qBlue(color) > 110 && qGreen(color) > 70
                && qBlue(color) > qRed(color) * 1.25 && qGreen(color) > qRed(color) * 1.2;
        }
    QList<QRect> result;
    std::vector<int> pending;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const int first = y * width + x;
            if (!pixels[size_t(first)])
                continue;
            pixels[size_t(first)] = 0;
            pending.clear();
            pending.push_back(first);
            QRect bounds(x, y, 1, 1);
            for (size_t next = 0; next < pending.size(); ++next) {
                const int position = pending[next];
                const int px = position % width;
                const int py = position / width;
                bounds = bounds.united(QRect(px, py, 1, 1));
                for (const QPoint offset : {QPoint(-1, 0), QPoint(1, 0), QPoint(0, -1), QPoint(0, 1)}) {
                    const int nx = px + offset.x();
                    const int ny = py + offset.y();
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    const int index = ny * width + nx;
                    if (pixels[size_t(index)]) {
                        pixels[size_t(index)] = 0;
                        pending.push_back(index);
                    }
                }
            }
            const int area = static_cast<int>(pending.size());
            if (area >= 40 && bounds.width() >= 10 && bounds.height() >= 10
                && bounds.width() <= image.width() * 0.12 && bounds.height() <= image.width() * 0.12
                && bounds.width() * 2 >= bounds.height() && bounds.height() * 2 >= bounds.width()) {
                result.append(bounds.translated(search.topLeft()));
            }
        }
    }
    return result;
}

QRect blueHeaderIcon(const QImage &image, const QRect &card)
{
    const QRect search = QRect(qRound(image.width() * 0.20), card.top(),
                               qRound(image.width() * 0.42),
                               std::min(qRound(image.width() * 0.14), card.height() / 2))
                             .intersected(card);
    const auto icons = blueHeaderIcons(image, search);
    QRect best;
    for (const auto &icon : icons)
        if (icon.width() * icon.height() > best.width() * best.height())
            best = icon;
    return best;
}

int colorDistance(QRgb left, QRgb right)
{
    return std::max({std::abs(qRed(left) - qRed(right)), std::abs(qGreen(left) - qGreen(right)),
                     std::abs(qBlue(left) - qBlue(right))});
}

/** @brief 用卡片同色底面的真实四边限定字段，避免将网页背景亮条送入 OCR。 */
QRect cardSurface(const QImage &image, const QRect &icon)
{
    constexpr std::array<double, 9> fractions{0.08, 0.16, 0.24, 0.34, 0.46, 0.58, 0.70, 0.82, 0.92};
    const int anchorY = icon.center().y();
    QRgb surface = 0;
    int largest = 0;
    for (const double fraction : fractions) {
        const QRgb sample = image.pixel(qRound(image.width() * fraction), anchorY);
        if (std::max({qRed(sample), qGreen(sample), qBlue(sample)}) > 210)
            continue;
        int count = 0;
        for (const double other : fractions)
            count += colorDistance(sample, image.pixel(qRound(image.width() * other), anchorY)) < 40;
        if (count > largest) {
            surface = sample;
            largest = count;
        }
    }
    if (largest < 5)
        return {};
    const auto matchesRow = [&](int row) {
        int count = 0;
        for (const double fraction : fractions)
            count += colorDistance(surface, image.pixel(qRound(image.width() * fraction), row)) < 40;
        return count >= 3;
    };
    int top = anchorY;
    while (top > 0 && matchesRow(top - 1))
        --top;
    int bottom = anchorY;
    while (bottom + 1 < image.height() && matchesRow(bottom + 1))
        ++bottom;
    if (top == 0 || bottom - top < icon.height() * 2
        || icon.top() - top > image.width() * 0.12)
        return {};
    // A row through the card's centre is away from rounded upper corners. Start
    // outside both edges and stop at the first actual surface pixel.
    const int row = (top + bottom) / 2;
    int left = qRound(image.width() * 0.03);
    while (left < image.width() * 0.20 && colorDistance(surface, image.pixel(left, row)) >= 40)
        ++left;
    int right = qRound(image.width() * 0.97);
    while (right > image.width() * 0.70 && colorDistance(surface, image.pixel(right, row)) >= 40)
        --right;
    if (left >= image.width() * 0.20 || right <= image.width() * 0.70)
        return {};
    return QRect(QPoint(left + 2, top), QPoint(right - 2, bottom)).intersected(image.rect());
}

/** @brief 检测横跨正文的持续背景跳变，在分页浮层的实际上沿停止裁切。 */
int visibleBodyBottom(const QImage &image, const QRect &body)
{
    if (body.isEmpty())
        return body.bottom();
    const int step = std::max(1, body.width() / 200);
    for (int y = body.top() + 1; y + 3 <= body.bottom(); ++y) {
        int changed = 0;
        int samples = 0;
        for (int x = body.left(); x <= body.right(); x += step) {
            ++samples;
            const QRgb before = image.pixel(x, y - 1);
            const QRgb after = image.pixel(x, y);
            const QRgb persistent = image.pixel(x, y + 3);
            // White text strokes and thin rules are not an overlay edge. The
            // dark background change must span most of the field and remain
            // present several rows later, including translucent paging bars.
            if (std::max({qRed(before), qGreen(before), qBlue(before), qRed(after), qGreen(after), qBlue(after)}) < 180
                && colorDistance(before, after) > 8 && colorDistance(before, persistent) > 8
                && colorDistance(after, persistent) <= 8)
                ++changed;
        }
        if (changed * 10 >= samples * 7)
            return y - 1;
    }
    return body.bottom();
}

/** @brief 裁切字段并补卡片背景边；不对原图执行二值化或改写识别文字。 */
QImage paddedField(const QImage &image, const QRect &rect, const QList<QRect> &deleteMarks)
{
    const QImage cropped = image.copy(rect);
    const std::array<QPoint, 4> corners{QPoint(0, 0), QPoint(cropped.width() - 1, 0),
                                      QPoint(0, cropped.height() - 1),
                                      QPoint(cropped.width() - 1, cropped.height() - 1)};
    std::array<QRgb, 4> colors{};
    std::transform(corners.begin(), corners.end(), colors.begin(),
                   [&cropped](const QPoint &point) { return cropped.pixel(point); });
    std::sort(colors.begin(), colors.end(), [](QRgb left, QRgb right) { return qGray(left) < qGray(right); });
    QImage padded(cropped.width() + 2 * kFieldBorder, cropped.height() + 2 * kFieldBorder,
                  QImage::Format_RGB32);
    padded.fill(colors[1]);
    for (int y = 0; y < cropped.height(); ++y) {
        auto *destination = reinterpret_cast<QRgb *>(padded.scanLine(y + kFieldBorder)) + kFieldBorder;
        for (int x = 0; x < cropped.width(); ++x)
            destination[x] = cropped.pixel(x, y);
    }
    for (const auto &mark : deleteMarks) {
        const QRect erased = mark.translated(QPoint(kFieldBorder, kFieldBorder) - rect.topLeft())
                                 .intersected(padded.rect());
        for (int y = erased.top(); y <= erased.bottom(); ++y) {
            auto *destination = reinterpret_cast<QRgb *>(padded.scanLine(y));
            for (int x = erased.left(); x <= erased.right(); ++x)
                destination[x] = colors[1];
        }
    }
    return padded;
}

/** @brief 仅识别卡片右侧由两条白色对角线组成的独立删除叉号，不按 OCR 假字删正文。 */
QList<QRect> visibleDeleteMarks(const QImage &image, const QRect &card, const QRect &body)
{
    const QRect search = body.intersected(QRect(qRound(image.width() * 0.82), body.top(),
                                               qRound(image.width() * 0.13), body.height()));
    QList<QRect> result;
    if (search.isEmpty())
        return result;
    const int width = search.width();
    const int height = search.height();
    std::vector<uchar> pixels(size_t(width) * size_t(height), 0);
    for (int y = 0; y < height; ++y)
        for (int x = 0; x < width; ++x) {
            const QRgb color = image.pixel(search.left() + x, search.top() + y);
            pixels[size_t(y) * width + x] = std::min({qRed(color), qGreen(color), qBlue(color)}) > 190;
        }
    std::vector<int> pending;
    for (int y = 0; y < height; ++y) {
        for (int x = 0; x < width; ++x) {
            const int first = y * width + x;
            if (!pixels[size_t(first)])
                continue;
            pixels[size_t(first)] = 0;
            pending.clear();
            pending.push_back(first);
            QRect bounds(x, y, 1, 1);
            for (size_t next = 0; next < pending.size(); ++next) {
                const int position = pending[next];
                const int px = position % width;
                const int py = position / width;
                bounds = bounds.united(QRect(px, py, 1, 1));
                for (const QPoint offset : {QPoint(-1, 0), QPoint(1, 0), QPoint(0, -1), QPoint(0, 1)}) {
                    const int nx = px + offset.x();
                    const int ny = py + offset.y();
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    const int index = ny * width + nx;
                    if (pixels[size_t(index)]) {
                        pixels[size_t(index)] = 0;
                        pending.push_back(index);
                    }
                }
            }
            if (bounds.width() < 12 || bounds.height() < 12
                || bounds.width() > image.width() * 0.06 || bounds.height() > image.width() * 0.06
                || bounds.width() * 4 < bounds.height() * 3 || bounds.height() * 4 < bounds.width() * 3)
                continue;
            const QRect original = bounds.translated(search.topLeft());
            const double expectedSize = image.width() * 0.024;
            const double tolerance = std::max(3.0, expectedSize * 0.20);
            if (std::abs(original.center().x() - image.width() * 0.885) > tolerance
                || std::abs(original.center().y() - card.center().y()) > tolerance
                || std::abs(original.width() - expectedSize) > tolerance
                || std::abs(original.height() - expectedSize) > tolerance)
                continue;
            int diagonal = 0;
            std::array<int, 4> corners{};
            for (const int position : pending) {
                const double dx = double(position % width - bounds.left()) / (bounds.width() - 1);
                const double dy = double(position / width - bounds.top()) / (bounds.height() - 1);
                diagonal += std::abs(dx - dy) <= 0.13 || std::abs(dx + dy - 1.0) <= 0.13;
                if (std::abs(dx - 0.5) > 0.25 && std::abs(dy - 0.5) > 0.25)
                    ++corners[size_t((dy > 0.5 ? 2 : 0) + (dx > 0.5 ? 1 : 0))];
            }
            if (diagonal < pending.size() * 0.85
                || !std::all_of(corners.begin(), corners.end(), [](int count) { return count >= 3; }))
                continue;
            // An X in XD or beside ordinary prose is text evidence. Keep it
            // whenever other white foreground touches the button's near area.
            bool adjoiningText = false;
            const int margin = std::max(3, original.height() / 4);
            const QRect vicinity = original.adjusted(-margin, -margin, margin, margin).intersected(body);
            for (int vy = vicinity.top(); vy <= vicinity.bottom() && !adjoiningText; ++vy)
                for (int vx = vicinity.left(); vx <= vicinity.right(); ++vx) {
                    if (original.contains(vx, vy))
                        continue;
                    const QRgb color = image.pixel(vx, vy);
                    if (std::min({qRed(color), qGreen(color), qBlue(color)}) > 190) {
                        adjoiningText = true;
                        break;
                    }
                }
            if (!adjoiningText)
                result.append(original.adjusted(-1, -1, 1, 1));
        }
    }
    return result;
}

} // namespace

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
    return QDir(applicationDirectory).filePath(QStringLiteral("ocr/tesseract.exe"));
}

QString OfflineOcrEngine::modelsPath(const QString &applicationDirectory)
{
    return QDir(applicationDirectory).filePath(QStringLiteral("ocr/tessdata"));
}

QString OfflineOcrEngine::dependencyError(const QString &applicationDirectory)
{
    if (!QFileInfo(enginePath(applicationDirectory)).isFile())
        return tr("缺少随应用安装的本地文字识别引擎。请使用包含 OCR 的安装包，或先导入表格/JSON。识别不会上传图片。");
    for (const QString &model : {QStringLiteral("chi_sim"), QStringLiteral("eng")}) {
        if (!QFileInfo(QDir(modelsPath(applicationDirectory)).filePath(model + QStringLiteral(".traineddata"))).isFile())
            return tr("本地文字识别语言文件不完整，请修复 OCR 安装后重试。");
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
    // Check every input before launching any recognition, including dimensions
    // read from its header so an oversized image is never eagerly decompressed.
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
    m_image = reader.read();
    m_fields.clear();
    m_recognizedTsv.clear();
    m_fieldIndex = -1;
    const QString input = m_work->filePath(QStringLiteral("input.png"));
    if (m_image.isNull() || !m_image.save(input, "PNG")) {
        stopWithError(tr("无法解码图片或保存识别副本：%1").arg(currentFile()));
        return;
    }
    m_outputBase = m_work->filePath(QStringLiteral("recognized"));
    // All coarse and field passes share one image deadline. A large card count
    // cannot multiply the user's 90-second limit by the number of child jobs.
    m_timeout.start(m_timeoutMs);
    launchProcess(input, QStringLiteral("chi_sim+eng"), 11);
}

void OfflineOcrEngine::launchProcess(const QString &input, const QString &language, int pageSegmentation)
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
    environment.insert(QStringLiteral("OMP_THREAD_LIMIT"), QStringLiteral("1"));
    process->setProcessEnvironment(environment);
    process->setWorkingDirectory(m_work->path());
    process->setProgram(enginePath(m_applicationDirectory));
    process->setArguments({input, m_outputBase, QStringLiteral("--tessdata-dir"), modelsPath(m_applicationDirectory),
                           QStringLiteral("-l"), language, QStringLiteral("--psm"),
                           QString::number(pageSegmentation), QStringLiteral("-c"),
                           QStringLiteral("tessedit_create_tsv=1")});
    // Consume process pipes continuously. The engine never logs recognized text;
    // diagnostic bytes are bounded and only an operation-level error is shown.
    connect(process, &QProcess::readyReadStandardError, this, [this, process, generation] {
        const QByteArray bytes = process->readAllStandardError();
        if (generation == m_generation && process == m_process && m_diagnostics.size() < 8192)
            m_diagnostics += bytes.left(8192 - m_diagnostics.size());
    });
    connect(process, &QProcess::readyReadStandardOutput, this, [process] { process->readAllStandardOutput(); });
    connect(process, &QProcess::errorOccurred, this, [this, process, generation](QProcess::ProcessError error) {
        if (generation == m_generation && process == m_process && error == QProcess::FailedToStart)
            stopWithError(tr("本地识别引擎无法启动，请检查随包 OCR 依赖是否完整。"));
    });
    connect(process, qOverload<int, QProcess::ExitStatus>(&QProcess::finished), this,
            [this, process, generation](int exitCode, QProcess::ExitStatus) {
        if (generation == m_generation && process == m_process)
            processFinished(exitCode);
    });
    // Also cap a continuously growing output file while the child is running.
    auto *outputCheck = new QTimer(process);
    outputCheck->setInterval(250);
    connect(outputCheck, &QTimer::timeout, this, [this, process, generation] {
        if (generation == m_generation && process == m_process
            && QFileInfo(m_outputBase + QStringLiteral(".tsv")).size() > MaxOutputBytes)
            stopWithError(tr("识别结果超过 8 MiB，已停止。请分批裁切后重试。"));
    });
    outputCheck->start();
    process->start();
    Q_EMIT changed();
}

void OfflineOcrEngine::processFinished(int exitCode)
{
    const quint64 generation = m_generation;
    if (m_process) {
        m_process->disconnect(this);
        m_process->deleteLater();
        m_process = nullptr;
    }
    if (m_cancelled || !m_failure.isEmpty()) {
        settle();
        return;
    }
    QFile output(m_outputBase + QStringLiteral(".tsv"));
    if (exitCode != 0 || !output.open(QIODevice::ReadOnly) || output.size() > MaxOutputBytes) {
        stopWithError(tr("本地识别未完成：%1。请核对图片或修复 OCR 安装。").arg(currentFile()));
        return;
    }
    const QByteArray tsv = output.read(MaxOutputBytes + 1);
    output.close();
    if (tsv.size() > MaxOutputBytes) {
        stopWithError(tr("识别结果超过允许大小，已停止。"));
        return;
    }
    if (m_fieldIndex < 0) {
        m_recognizedTsv = tsv;
        const bool hasFields = prepareFields(tsv);
        if (generation != m_generation || !m_busy)
            return;
        if (hasFields)
            queueField(generation);
        else
            finishImage(tsv);
        return;
    }

    const Field &field = m_fields[m_fieldIndex];
    auto replacement = wordRows(tsv, field.rect.size() + QSize(kFieldBorder * 2, kFieldBorder * 2));
    QList<OcrWord> mapped;
    for (auto word : replacement) {
        const QRect translated = word.rect.translated(field.rect.topLeft() - QPoint(kFieldBorder, kFieldBorder));
        const QRect bounds = translated.intersected(field.rect).intersected(m_image.rect());
        if (bounds.isEmpty())
            continue;
        word.rect = bounds;
        word.columns[1] = "1";
        word.columns[2] = QByteArray::number(100'000 + m_fieldIndex);
        word.columns[6] = QByteArray::number(bounds.x());
        word.columns[7] = QByteArray::number(bounds.y());
        word.columns[8] = QByteArray::number(bounds.width());
        word.columns[9] = QByteArray::number(bounds.height());
        mapped.append(std::move(word));
    }
    auto recognized = wordRows(m_recognizedTsv, m_image.size());
    QList<OcrWord> previous;
    for (const auto &word : recognized)
        if (field.rect.contains(word.rect.center())
            && std::none_of(field.deleteMarks.begin(), field.deleteMarks.end(), [&](const QRect &mark) {
                return mark.contains(word.rect.center());
            }))
            previous.append(word);
    bool accept = !mapped.isEmpty() && hanCount(mapped) * 2 >= hanCount(previous);
    if (field.kind == QLatin1String("time")) {
        QStringList words;
        for (const auto &word : mapped)
            words.append(word.text);
        static const QRegularExpression timestamp(QStringLiteral(
            R"(^\s*\d{4}\s*[-－]\s*\d{2}\s*[-－]\s*\d{2}\s+\d{2}\s*[:：]\s*\d{2}\s*[:：]\s*\d{2}\s*$)"));
        accept = timestamp.match(words.join(QLatin1Char(' '))).hasMatch();
    }
    if (field.kind == QLatin1String("level")) {
        QStringList words;
        for (const auto &word : mapped)
            words.append(word.text);
        static const QRegularExpression level(QStringLiteral(R"(^\s*(?:[Ll]\s*)?[Vv]\s*[.。．:]?\s*\d{1,3}\s*$)"));
        accept = level.match(words.join(QLatin1Char(' '))).hasMatch();
    }
    // Short notes can regress into Latin gibberish in a second pass. Keep the
    // original evidence if the field was empty or lost most of its Han text.
    if (accept) {
        recognized.erase(std::remove_if(recognized.begin(), recognized.end(), [&](const OcrWord &word) {
            return field.rect.contains(word.rect.center());
        }), recognized.end());
        recognized.append(mapped);
    }
    for (const auto &mark : field.deleteMarks) {
        recognized.erase(std::remove_if(recognized.begin(), recognized.end(), [&](const OcrWord &word) {
            return mark.contains(word.rect.center());
        }), recognized.end());
        // This symbol is backed by the actual diagonal cross pixels. Retain its
        // original position so the parser can report overlapping body text.
        const QRect bounds = mark.intersected(m_image.rect());
        recognized.append({{"5", "1", QByteArray::number(100'000 + m_fieldIndex), "1", "1", "99999",
                            QByteArray::number(bounds.x()), QByteArray::number(bounds.y()),
                            QByteArray::number(bounds.width()), QByteArray::number(bounds.height()),
                            "100", QStringLiteral("×").toUtf8()}, bounds, QStringLiteral("×")});
    }
    m_recognizedTsv = serializedWords(recognized);
    if (m_recognizedTsv.size() > MaxOutputBytes) {
        stopWithError(tr("分区识别结果超过 8 MiB，请分批裁切后重试。"));
        return;
    }
    ++m_fieldIndex;
    if (m_fieldIndex < m_fields.size())
        queueField(generation);
    else
        finishImage(m_recognizedTsv);
}

bool OfflineOcrEngine::prepareFields(const QByteArray &coarseTsv)
{
    const auto words = wordRows(coarseTsv, m_image.size());
    auto visibleWords = words;
    const auto parsed = ScreenshotImportParser::parse(m_image, coarseTsv, m_files[m_index]);
    QList<QRect> cards;
    for (const auto &value : parsed) {
        const auto source = value.toMap().value(QStringLiteral("source_rect")).toMap();
        cards.append(QRect(source.value(QStringLiteral("x")).toInt(), source.value(QStringLiteral("y")).toInt(),
                           source.value(QStringLiteral("width")).toInt(), source.value(QStringLiteral("height")).toInt()));
    }
    // A covered footer and a misread Lv prefix can hide a visible final card
    // from the coarse TSV. Its type icon plus an independent dark card surface
    // still provides a local image anchor for new level/title field passes.
    const auto icons = blueHeaderIcons(m_image, QRect(qRound(m_image.width() * 0.20), 0,
                                                      qRound(m_image.width() * 0.42), m_image.height()));
    for (const auto &icon : icons) {
        bool represented = false;
        for (const auto &card : cards) {
            const QRect known = blueHeaderIcon(m_image, card);
            if (known.isValid() && std::abs(known.center().y() - icon.center().y()) < icon.height()) {
                represented = true;
                break;
            }
        }
        if (!represented) {
            const QRect surface = cardSurface(m_image, icon);
            if (surface.isValid())
                cards.append(surface);
        }
    }
    std::sort(cards.begin(), cards.end(), [](const QRect &left, const QRect &right) { return left.top() < right.top(); });
    static const QRegularExpression date(QStringLiteral(R"(^\d{4}\s*[-－]\s*\d{2}\s*[-－]\s*\d{2}$)"));
    static const QRegularExpression level(QStringLiteral(R"(^(?:[Ll]\s*)?[Vv]\s*[.。．:]?\s*\d{1,3}$)"));
    for (const QRect &coarseCard : cards) {
        const QRect icon = blueHeaderIcon(m_image, coarseCard);
        if (!icon.isValid())
            continue;
        const QRect surface = cardSurface(m_image, icon);
        if (!surface.isValid())
            continue;
        const QRect card = QRect(surface.left(), coarseCard.top(), surface.width(), coarseCard.height())
                               .intersected(m_image.rect());
        const int padding = std::max(4, icon.height() / 8);
        int textLeft = qRound(m_image.width() * 0.25);
        QRect dateRect;
        int pagingTop = card.bottom() + 1;
        for (const auto &word : words) {
            if (!card.contains(word.rect.center()))
                continue;
            if (word.rect.right() < icon.left() && level.match(word.text).hasMatch()
                && std::abs(word.rect.center().y() - icon.center().y()) < icon.height())
                textLeft = word.rect.left();
            if (date.match(word.text).hasMatch())
                dateRect = word.rect;
            if (word.rect.top() > icon.bottom()
                && (word.text.contains(QLatin1String("page"), Qt::CaseInsensitive)
                    || word.text.contains(QStringLiteral("条记录")) || word.text == QLatin1String("of")))
                pagingTop = std::min(pagingTop, word.rect.top());
        }
        if (dateRect.isValid()) {
            const QRect anchor = dateRect;
            for (const auto &word : words)
                if (card.contains(word.rect.center()) && word.rect.left() >= anchor.left()
                    && std::abs(word.rect.center().y() - anchor.center().y()) <= anchor.height() / 2)
                    dateRect = dateRect.united(word.rect);
        }
        const int titleLeft = icon.right() + std::max(padding, icon.height() / 3);
        const QRect title = QRect(titleLeft, icon.top() - padding,
                                  card.right() - titleLeft + 1, icon.height() + padding * 2)
                                .intersected(card).intersected(m_image.rect());
        const QRect levelRect = QRect(textLeft - padding, icon.top() - padding,
                                      icon.left() - textLeft, icon.height() + padding * 2)
                                    .intersected(card).intersected(m_image.rect());
        const int footerTop = dateRect.isValid() ? dateRect.top() - std::max(3, dateRect.height() / 3)
                                                : card.bottom() - qRound(m_image.width() * 0.05);
        const QRect coarseBody = QRect(textLeft - 2, icon.bottom() + padding,
                                       card.right() - textLeft + 3,
                                       std::min(footerTop - padding, pagingTop - padding) - icon.bottom() - padding)
                                     .intersected(card).intersected(m_image.rect());
        const int bodyBottom = visibleBodyBottom(m_image, coarseBody.intersected(surface));
        QRect body = coarseBody;
        body.setBottom(std::min(body.bottom(), bodyBottom));
        if (body.bottom() < coarseBody.bottom()) {
            // Keep paging words as evidence for the parser's coverage warning,
            // but never use covered controls or partial letters as coarse-note
            // fallback when the field pass is empty or too short to run.
            visibleWords.erase(std::remove_if(visibleWords.begin(), visibleWords.end(), [&](const OcrWord &word) {
                const bool paging = word.text.contains(QLatin1String("page"), Qt::CaseInsensitive)
                    || word.text.contains(QStringLiteral("条记录")) || word.text == QLatin1String("of");
                return !paging && word.rect.intersects(coarseBody) && word.rect.bottom() > body.bottom();
            }), visibleWords.end());
        }
        const QRect footer = (dateRect.isValid() ? dateRect.adjusted(-padding, -padding, padding, padding)
            : QRect(card.left() + qRound(card.width() * 0.55), footerTop,
                    qRound(card.width() * 0.40), card.bottom() - footerTop - padding))
                                    .intersected(card).intersected(m_image.rect());
        for (Field field : {Field{levelRect, QStringLiteral("level"), QStringLiteral("eng"), 7},
                                   Field{title, QStringLiteral("title"), QStringLiteral("chi_sim"), 7},
                                   Field{body, QStringLiteral("body"), QStringLiteral("chi_sim+eng"), 6},
                                   Field{footer, QStringLiteral("time"), QStringLiteral("eng"), 7}}) {
            if (field.rect.isEmpty())
                continue;
            if (field.kind == QLatin1String("body") && field.rect.height() < std::max(12, icon.height() / 2))
                continue;
            if (m_fields.size() >= kMaximumFields
                || qint64(field.rect.width() + kFieldBorder * 2) * (field.rect.height() + kFieldBorder * 2)
                    > kMaximumFieldPixels) {
                stopWithError(tr("截图中的识别分区过多或过大，请分批裁切后重试。"));
                return false;
            }
            if (field.kind == QLatin1String("body"))
                field.deleteMarks = visibleDeleteMarks(m_image, card, field.rect);
            if (field.kind == QLatin1String("body")) {
                QList<OcrWord> coarseBody;
                for (const auto &word : words)
                    if (field.rect.contains(word.rect.center())
                        && std::none_of(field.deleteMarks.begin(), field.deleteMarks.end(), [&](const QRect &mark) {
                            return mark.contains(word.rect.center());
                        }))
                        coarseBody.append(word);
                if (hanCount(coarseBody) > 0)
                    field.language = QStringLiteral("chi_sim");
            }
            m_fields.append(std::move(field));
        }
    }
    if (m_fields.isEmpty())
        return false;
    m_recognizedTsv = serializedWords(visibleWords);
    m_fieldIndex = 0;
    return true;
}

void OfflineOcrEngine::queueField(quint64 generation)
{
    QTimer::singleShot(0, this, [this, generation] {
        if (generation == m_generation && m_busy)
            startField();
    });
}

void OfflineOcrEngine::startField()
{
    if (!m_busy || m_process || m_fieldIndex < 0 || m_fieldIndex >= m_fields.size())
        return;
    const Field &field = m_fields[m_fieldIndex];
    const QString input = m_work->filePath(QStringLiteral("%1-%2.png").arg(field.kind).arg(m_fieldIndex));
    if (!paddedField(m_image, field.rect, field.deleteMarks).save(input, "PNG")) {
        stopWithError(tr("无法保存本地分区识别副本，请检查磁盘空间和权限。"));
        return;
    }
    m_outputBase = m_work->filePath(QStringLiteral("field-%1").arg(m_fieldIndex));
    launchProcess(input, field.language, field.pageSegmentation);
}

void OfflineOcrEngine::finishImage(QByteArray tsv)
{
    const quint64 generation = m_generation;
    m_timeout.stop();
    const QString sourcePath = m_files[m_index];
    ++m_index;
    m_fields.clear();
    m_fieldIndex = -1;
    m_image = {};
    m_recognizedTsv.clear();
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
        // FailedToStart can settle without emitting finished.
        m_process->disconnect(this);
        m_process->deleteLater();
        m_process = nullptr;
    }
    m_work.reset();
    m_image = {};
    m_fields.clear();
    m_recognizedTsv.clear();
    m_fieldIndex = -1;
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
