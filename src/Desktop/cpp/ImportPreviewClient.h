#pragma once

#include "IBackend.h"
#include "LiveText.h"

#include <QObject>
#include <QPointer>
#include <functional>
#include <memory>

namespace mr {

enum class ImportPreviewFailure : quint8 { None, Disconnected, Rejected, Incomplete };

/// A whole frozen preview or a failure; never exposes a partially accumulated page set.
struct ImportPreviewResult
{
    ImportPreviewFailure failure = ImportPreviewFailure::None;
    QVariantMap payload;
    QString errorCode;
    LiveText refusal;
};

/// Owns one asynchronous read-only preview operation. The controller retains batch/commit state.
/// Calls and callbacks run on the owning QObject thread. Cancel/replacement suppresses both late
/// replies and queued page requests; destruction disconnects all reply handlers automatically.
class ImportPreviewClient final : public QObject
{
public:
    using Completion = std::function<void(ImportPreviewResult)>;
    explicit ImportPreviewClient(QObject *parent = nullptr);
    /// Borrows backend; completion may run synchronously. No prior request completes after start.
    void start(IBackend *backend, const RunImportSource &source, Completion completion);
    /// Invalidates only this read operation; no Collector mutation or accepted batch is touched.
    void cancel();

private:
    struct Operation;
    void requestPage(const std::shared_ptr<Operation> &operation);
    void finish(const std::shared_ptr<Operation> &operation, ImportPreviewResult result) const;
    quint64 m_generation = 0;
};

} // namespace mr
