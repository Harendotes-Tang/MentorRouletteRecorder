#include "JobIconClassifier.h"
#include "JobCatalog.h"

#include <QFile>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPainter>
#include <QSet>
#include <QTemporaryDir>
#include <QTest>
#include <QUrl>
#include <QVariantMap>

#include <cmath>

namespace {

QString sourceModel()
{
    return QStringLiteral(MR_SOURCE_DIR "/src/Desktop/resources/models/job-icon-classifier.json");
}

QImage sourceIcon(int id)
{
    return QImage(QStringLiteral(MR_SOURCE_DIR "/src/Desktop/resources/icons/jobs/%1.png").arg(id));
}

QImage paintedIcon(int id, int size = 56, const QColor &background = QColor(45, 69, 38))
{
    QImage image(size + 30, size + 30, QImage::Format_ARGB32);
    image.fill(background);
    QPainter painter(&image);
    painter.setRenderHint(QPainter::SmoothPixmapTransform);
    painter.drawImage(QRect(15, 15, size, size), sourceIcon(id));
    return image;
}

bool boundedEvidence(const mr::JobIconClassifier::Result &result)
{
    return std::isfinite(result.confidence) && result.confidence >= 0 && result.confidence <= 1
        && std::isfinite(result.runnerUpConfidence) && result.runnerUpConfidence >= 0
        && result.runnerUpConfidence <= 1 && std::isfinite(result.unknownConfidence)
        && result.unknownConfidence >= 0 && result.unknownConfidence <= 1
        && std::isfinite(result.featureDistance) && result.featureDistance >= 0;
}

} // namespace

class JobIconClassifierTests final : public QObject
{
    Q_OBJECT

private slots:
    void modelLabelCoverageMatchesEligibleCatalogue();
    void knownJobsAcrossSizesAndBackgrounds_data();
    void knownJobsAcrossSizesAndBackgrounds();
    void blankDeleteAndBlueDutyIconsRemainUnknown();
    void solidGoldRectangleRemainsUnknown();
    void excludedJobsNeverBecomeReliableEligibleIdentities();
    void ambiguousMixtureRemainsReviewable();
    void malformedOrMismatchedModelIsUnavailable_data();
    void malformedOrMismatchedModelIsUnavailable();
    void hugeAndTransparentImagesHaveFiniteUnknownEvidence();
};

void JobIconClassifierTests::modelLabelCoverageMatchesEligibleCatalogue()
{
    const mr::JobIconClassifier classifier(sourceModel());
    QVERIFY(classifier.isAvailable());
    QFile file(sourceModel());
    QVERIFY(file.open(QIODevice::ReadOnly));
    const QJsonArray labels = QJsonDocument::fromJson(file.readAll()).object().value(QStringLiteral("class_ids")).toArray();
    QSet<int> modelJobs;
    for (const QJsonValue &label : labels)
        modelJobs.insert(label.toInt());
    QVERIFY(modelJobs.remove(0));
    QSet<int> eligible;
    const mr::JobCatalog catalogue;
    for (const QVariant &row : catalogue.battleJobs())
        eligible.insert(row.toMap().value(QStringLiteral("job_id")).toInt());
    QCOMPARE(modelJobs, eligible);
    const mr::JobIconClassifier packaged;
    QCOMPARE(packaged.isAvailable(), QFile::exists(QStringLiteral(":/resources/models/job-icon-classifier.json")));
}

void JobIconClassifierTests::knownJobsAcrossSizesAndBackgrounds_data()
{
    QTest::addColumn<int>("id");
    QTest::addColumn<int>("size");
    QTest::addColumn<QColor>("background");
    const mr::JobCatalog catalogue;
    for (const QVariant &row : catalogue.battleJobs()) {
        const int id = row.toMap().value(QStringLiteral("job_id")).toInt();
        for (int size : {24, 48, 80}) {
            for (const QColor &background : {QColor(45, 69, 38), QColor(241, 241, 241)}) {
                const QByteArray name = QByteArray::number(id) + '-' + QByteArray::number(size)
                    + '-' + background.name().toLatin1();
                QTest::newRow(name.constData()) << id << size << background;
            }
        }
    }
}

void JobIconClassifierTests::knownJobsAcrossSizesAndBackgrounds()
{
    QFETCH(int, id);
    QFETCH(int, size);
    QFETCH(QColor, background);
    const mr::JobIconClassifier classifier(sourceModel());
    QVERIFY(classifier.isAvailable());
    const auto result = classifier.classify(paintedIcon(id, size, background));
    QVERIFY(result.available);
    QVERIFY(boundedEvidence(result));
    QCOMPARE(result.candidateId, id);
    // Small resampled artwork can legitimately be rejected; it must never confidently become another job.
    QVERIFY(result.id == 0 || result.id == id);
    if (size == 48)
        QCOMPARE(result.id, id);
}

void JobIconClassifierTests::blankDeleteAndBlueDutyIconsRemainUnknown()
{
    const mr::JobIconClassifier classifier(sourceModel());
    QVERIFY(classifier.isAvailable());
    QImage image(100, 100, QImage::Format_ARGB32);
    image.fill(QColor(45, 69, 38));
    auto result = classifier.classify(image);
    QVERIFY(result.available);
    QCOMPARE(result.id, 0);
    QCOMPARE(result.candidateId, 0);
    {
        QPainter painter(&image);
        painter.setPen(QPen(Qt::white, 3));
        painter.drawLine(25, 25, 75, 75);
        painter.drawLine(25, 75, 75, 25);
    }
    result = classifier.classify(image);
    QCOMPARE(result.id, 0);
    QCOMPARE(result.candidateId, 0);
    image.fill(QColor(45, 69, 38));
    {
        QPainter painter(&image);
        painter.fillRect(QRect(25, 25, 50, 50), QColor(50, 170, 240));
        painter.fillRect(QRect(35, 35, 30, 30), QColor(15, 70, 120));
    }
    result = classifier.classify(image);
    QCOMPARE(result.id, 0);
    QCOMPARE(result.candidateId, 0);
    QVERIFY(boundedEvidence(result));
}

void JobIconClassifierTests::excludedJobsNeverBecomeReliableEligibleIdentities()
{
    const mr::JobIconClassifier classifier(sourceModel());
    const mr::JobCatalog catalogue;
    QSet<int> eligible;
    for (const QVariant &row : catalogue.battleJobs())
        eligible.insert(row.toMap().value(QStringLiteral("job_id")).toInt());
    int tested = 0;
    for (const QVariant &row : catalogue.allJobs()) {
        const int id = row.toMap().value(QStringLiteral("job_id")).toInt();
        if (eligible.contains(id))
            continue;
        const auto result = classifier.classify(paintedIcon(id));
        QVERIFY2(result.available, "Excluded identities must be rejected by a loaded model.");
        QCOMPARE(result.id, 0);
        QVERIFY(boundedEvidence(result));
        ++tested;
    }
    QVERIFY(tested > 0);
}

void JobIconClassifierTests::solidGoldRectangleRemainsUnknown()
{
    const mr::JobIconClassifier classifier(sourceModel());
    QImage image(160, 100, QImage::Format_ARGB32);
    image.fill(QColor(45, 69, 38));
    QPainter painter(&image);
    painter.fillRect(QRect(20, 20, 120, 60), QColor(232, 207, 133));
    painter.end();
    const auto result = classifier.classify(image);
    QVERIFY(result.available);
    QVERIFY(boundedEvidence(result));
    QCOMPARE(result.id, 0);
}

void JobIconClassifierTests::ambiguousMixtureRemainsReviewable()
{
    const mr::JobIconClassifier classifier(sourceModel());
    QImage image = paintedIcon(19);
    QPainter painter(&image);
    painter.setOpacity(0.5);
    painter.drawImage(QRect(15, 15, 56, 56), sourceIcon(28));
    painter.end();
    const auto result = classifier.classify(image);
    QVERIFY(result.available);
    QVERIFY(boundedEvidence(result));
    QCOMPARE(result.id, 0);
}

void JobIconClassifierTests::malformedOrMismatchedModelIsUnavailable_data()
{
    QTest::addColumn<QString>("fault");
    for (const char *fault : {"missing", "json", "large", "format", "labels", "label_fraction",
                              "weights_short", "weights_string", "weight_overflow", "loose_threshold"})
        QTest::newRow(fault) << QString::fromLatin1(fault);
}

void JobIconClassifierTests::malformedOrMismatchedModelIsUnavailable()
{
    QFETCH(QString, fault);
    QTemporaryDir dir;
    QVERIFY(dir.isValid());
    const QString path = dir.filePath(QStringLiteral("model.json"));
    if (fault != QLatin1String("missing")) {
        QFile source(sourceModel());
        QVERIFY(source.open(QIODevice::ReadOnly));
        QJsonObject model = QJsonDocument::fromJson(source.readAll()).object();
        QByteArray data;
        if (fault == QLatin1String("json")) {
            data = "{invalid";
        } else if (fault == QLatin1String("large")) {
            data = QByteArray(1024 * 1024 + 1, ' ');
        } else {
            if (fault == QLatin1String("format"))
                model.insert(QStringLiteral("format"), QStringLiteral("future-format"));
            else if (fault == QLatin1String("loose_threshold"))
                model.insert(QStringLiteral("confidence_threshold"), 0.1);
            else if (fault == QLatin1String("labels") || fault == QLatin1String("label_fraction")) {
                QJsonArray labels = model.value(QStringLiteral("class_ids")).toArray();
                labels[1] = fault == QLatin1String("labels") ? 1.0 : 19.5;
                model.insert(QStringLiteral("class_ids"), labels);
            } else {
                QJsonObject weights = model.value(QStringLiteral("weights")).toObject();
                QJsonArray values = weights.value(QStringLiteral("input_hidden")).toArray();
                if (fault == QLatin1String("weights_short"))
                    values.removeLast();
                else if (fault == QLatin1String("weights_string"))
                    values[0] = QStringLiteral("NaN");
                else if (fault == QLatin1String("weight_overflow"))
                    values[0] = 1e200;
                weights.insert(QStringLiteral("input_hidden"), values);
                model.insert(QStringLiteral("weights"), weights);
            }
            data = QJsonDocument(model).toJson(QJsonDocument::Compact);
        }
        QFile file(path);
        QVERIFY(file.open(QIODevice::WriteOnly));
        QCOMPARE(file.write(data), data.size());
        file.close();
    }
    const mr::JobIconClassifier classifier(path);
    QVERIFY(!classifier.isAvailable());
    const auto result = classifier.classify(paintedIcon(28));
    QVERIFY(!result.available);
    QCOMPARE(result.id, 0);
    QCOMPARE(result.candidateId, 0);
    QVERIFY(boundedEvidence(result));
}

void JobIconClassifierTests::hugeAndTransparentImagesHaveFiniteUnknownEvidence()
{
    const mr::JobIconClassifier classifier(sourceModel());
    for (const QImage &image : {QImage(), QImage(2050, 20, QImage::Format_ARGB32)}) {
        const auto result = classifier.classify(image);
        QVERIFY(result.available);
        QCOMPARE(result.id, 0);
        QVERIFY(boundedEvidence(result));
    }
    QImage transparent(100, 100, QImage::Format_ARGB32);
    transparent.fill(qRgba(230, 210, 145, 0));
    const auto result = classifier.classify(transparent);
    QVERIFY(result.available);
    QCOMPARE(result.id, 0);
    QCOMPARE(result.candidateId, 0);
    QVERIFY(boundedEvidence(result));
}

int main(int argc, char *argv[])
{
    Q_INIT_RESOURCE(mr_icons);
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QGuiApplication app(argc, argv);
    JobIconClassifierTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "JobIconClassifierTests.moc"
