#include "ImportPreviewClient.h"
#include "CollectorText.h"

#include <QJsonValue>
#include <QSet>
#include <QTimer>
#include <limits>
#include <utility>

namespace mr {
namespace {
bool boundedInteger(const QVariant &value, int minimum, int maximum, int &result)
{
    const QJsonValue json = QJsonValue::fromVariant(value);
    if (!json.isDouble() || json.toDouble() < minimum || json.toDouble() > maximum
        || json.toDouble() != json.toInt())
        return false;
    result = json.toInt();
    return true;
}

bool unsupportedPaging(const QPointer<BackendReply> &reply, const QString &code, const QString &message)
{
    if (code != QLatin1String("ERR_BAD_REQUEST"))
        return false;
    const QVariantMap ref = reply ? reply->errorRef().toMap() : QVariantMap();
    const QVariantMap args = ref.value(QStringLiteral("args")).toMap();
    // Only the strict unknown-field refusal establishes a legacy peer.
    return (ref.value(QStringLiteral("key")).toString() == QLatin1String("ipc.field.unknown")
            && args.value(QStringLiteral("field")).toString() == QLatin1String("page_size"))
        || message == QStringLiteral("请求包含契约未声明的字段 page_size。");
}

bool completePreview(const QVariantMap &payload)
{
    const QVariantList rows = payload.value(QStringLiteral("rows")).toList();
    int total = 0;
    if (payload.value(QStringLiteral("preview_id")).toString().isEmpty()
        || !boundedInteger(payload.value(QStringLiteral("summary")).toMap().value(QStringLiteral("total")), 0, 5000, total)
        || total != rows.size())
        return false;
    QSet<int> numbers;
    for (const auto &value : rows) {
        int number = 0;
        if (!boundedInteger(value.toMap().value(QStringLiteral("row_number")), 1, std::numeric_limits<int>::max(), number)
            || numbers.contains(number))
            return false;
        numbers.insert(number);
    }
    return true;
}
}

struct ImportPreviewClient::Operation
{
    QPointer<IBackend> backend;
    RunImportSource source;
    Completion completion;
    quint64 generation = 0;
    QVariantMap first;
    QVariantList rows;
    QSet<int> rowNumbers;
    int next = 1;
    int pageSize = 0;
    int total = 0;
    bool legacyRetry = false;
};

ImportPreviewClient::ImportPreviewClient(QObject *parent) : QObject(parent) {}

void ImportPreviewClient::start(IBackend *backend, const RunImportSource &source, Completion completion)
{
    auto operation = std::make_shared<Operation>();
    operation->backend = backend;
    operation->source = source;
    operation->completion = std::move(completion);
    operation->generation = ++m_generation;
    requestPage(operation);
}

void ImportPreviewClient::cancel()
{
    ++m_generation;
}

void ImportPreviewClient::finish(const std::shared_ptr<Operation> &operation, ImportPreviewResult result) const
{
    if (operation->generation != m_generation || !operation->completion)
        return;
    auto completion = std::move(operation->completion);
    completion(std::move(result)); // May replace this operation or destroy its owner.
}

void ImportPreviewClient::requestPage(const std::shared_ptr<Operation> &operation)
{
    if (operation->generation != m_generation)
        return;
    if (!operation->backend || !operation->backend->isConnected()) {
        finish(operation, {.failure = ImportPreviewFailure::Disconnected});
        return;
    }
    BackendReply *reply = operation->next == 1 ? operation->backend->previewRunImport(operation->source)
        : operation->backend->previewRunImportPage(operation->first.value(QStringLiteral("preview_id")).toString(), operation->next);
    const QPointer<BackendReply> guard(reply);
    reply->whenDone(this, [this, operation, guard](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
        if (operation->generation != m_generation)
            return;
        if (!ok) {
            if (operation->next == 1 && !operation->legacyRetry && operation->source.pageSize > 0
                && unsupportedPaging(guard, code, message)) {
                operation->legacyRetry = true;
                operation->source.pageSize = 0;
                QTimer::singleShot(0, this, [this, operation] { requestPage(operation); });
                return;
            }
            finish(operation, {.failure = ImportPreviewFailure::Rejected, .errorCode = code,
                               .refusal = collector_text::replyErrorText(guard, message)});
            return;
        }
        if (operation->next == 1 && !payload.contains(QStringLiteral("page_info"))) {
            if (completePreview(payload))
                finish(operation, {.payload = payload});
            else
                finish(operation, {.failure = ImportPreviewFailure::Incomplete});
            return;
        }
        const QVariantMap info = payload.value(QStringLiteral("page_info")).toMap();
        int page = 0;
        int size = 0;
        int total = 0;
        int summaryTotal = 0;
        bool valid = boundedInteger(info.value(QStringLiteral("page")), 1, 5000, page)
            && boundedInteger(info.value(QStringLiteral("page_size")), 1, 100, size)
            && boundedInteger(info.value(QStringLiteral("total")), 0, 5000, total)
            && boundedInteger(payload.value(QStringLiteral("summary")).toMap().value(QStringLiteral("total")), 0, 5000, summaryTotal)
            && summaryTotal == total && page == operation->next
            && !payload.value(QStringLiteral("preview_id")).toString().isEmpty();
        if (valid && page == 1) {
            operation->first = payload;
            operation->pageSize = size;
            operation->total = total;
        } else if (valid) {
            valid = size == operation->pageSize && total == operation->total;
            for (const QString &key : {QStringLiteral("preview_id"), QStringLiteral("summary"),
                                       QStringLiteral("source_kind"), QStringLiteral("source_name")})
                valid = valid && payload.value(key) == operation->first.value(key);
        }
        const QVariantList rows = payload.value(QStringLiteral("rows")).toList();
        const qsizetype remaining = total - operation->rows.size();
        valid = valid && remaining >= 0 && rows.size() == qMin<qsizetype>(size, remaining)
            && (total > 0 || page == 1);
        for (const auto &value : rows) {
            int number = 0;
            if (!boundedInteger(value.toMap().value(QStringLiteral("row_number")), 1, std::numeric_limits<int>::max(), number)
                || operation->rowNumbers.contains(number)) {
                valid = false;
                break;
            }
            operation->rowNumbers.insert(number);
        }
        if (!valid) {
            finish(operation, {.failure = ImportPreviewFailure::Incomplete});
            return;
        }
        operation->rows.append(rows);
        if (operation->rows.size() == operation->total) {
            QVariantMap complete = operation->first;
            complete.insert(QStringLiteral("rows"), operation->rows);
            complete.remove(QStringLiteral("page_info"));
            finish(operation, {.payload = std::move(complete)});
            return;
        }
        ++operation->next;
        // Defer even synchronous replies so thousands of pages cannot grow the call stack.
        QTimer::singleShot(0, this, [this, operation] { requestPage(operation); });
    });
}

} // namespace mr
