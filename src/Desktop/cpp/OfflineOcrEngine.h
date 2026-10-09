#pragma once

#include <QByteArray>
#include <QImage>
#include <QList>
#include <QObject>
#include <QPointer>
#include <QRect>
#include <QStringList>
#include <QTemporaryDir>
#include <QTimer>

#include <memory>

class QProcess;

namespace mr {

/// Runs only the packaged Tesseract on owned PNG copies. Each image is processed
/// serially; cancellation/timeout kills the owned process before deleting its work.
class OfflineOcrEngine final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString currentFile READ currentFile NOTIFY changed)
    Q_PROPERTY(int completedImages READ completedImages NOTIFY changed)
    Q_PROPERTY(int totalImages READ totalImages NOTIFY changed)

public:
    static constexpr qint64 MaxInputBytes = 20 * 1024 * 1024;
    static constexpr qint64 MaxPixels = 50 * 1000 * 1000;
    static constexpr qint64 MaxOutputBytes = 8 * 1024 * 1024;
    static constexpr int MaxImages = 20;
    static constexpr int DefaultTimeoutMs = 90 * 1000;

    explicit OfflineOcrEngine(QObject *parent = nullptr);
    ~OfflineOcrEngine() override;

    bool busy() const { return m_busy; }
    QString currentFile() const;
    int completedImages() const { return m_index; }
    int totalImages() const { return m_files.size(); }
    bool start(const QStringList &files);
    void cancel();

    /// An absolute alternate application root for isolated tests, never PATH.
    void setApplicationDirectoryForTesting(const QString &directory);
    void setTimeoutForTesting(int milliseconds);
    static QString enginePath(const QString &applicationDirectory);
    static QString modelsPath(const QString &applicationDirectory);
    static QString dependencyError(const QString &applicationDirectory);

Q_SIGNALS:
    void changed();
    void imageRecognized(const QString &sourcePath, const QByteArray &tsv);
    void failed(const QString &message);
    void finished(bool complete);

private:
    struct Field {
        QRect rect;
        QString kind;
        QString language;
        int pageSegmentation = 7;
        QList<QRect> deleteMarks;
    };

    void startNext();
    void queueNext(quint64 generation);
    void launchProcess(const QString &input, const QString &language, int pageSegmentation);
    bool prepareFields(const QByteArray &coarseTsv);
    void startField();
    void queueField(quint64 generation);
    void finishImage(QByteArray tsv);
    void stopWithError(const QString &message);
    void settle();
    void processFinished(int exitCode);

    QString m_applicationDirectory;
    QStringList m_files;
    QPointer<QProcess> m_process;
    std::unique_ptr<QTemporaryDir> m_work;
    QTimer m_timeout;
    QString m_outputBase;
    QImage m_image;
    QList<Field> m_fields;
    QByteArray m_recognizedTsv;
    int m_fieldIndex = -1;
    QString m_failure;
    QByteArray m_diagnostics;
    int m_timeoutMs = DefaultTimeoutMs;
    int m_index = 0;
    quint64 m_generation = 0;
    bool m_busy = false;
    bool m_cancelled = false;
};

} // namespace mr
