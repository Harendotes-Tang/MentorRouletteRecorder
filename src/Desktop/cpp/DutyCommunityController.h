#pragma once

#include <QObject>
#include <QPointer>
#include <QString>
#include <QUrl>
#include <QVariantMap>

namespace mr {

class IBackend;

class DutyCommunityController final : public QObject
{
    Q_OBJECT
public:
    explicit DutyCommunityController(IBackend *backend, QObject *parent = nullptr);
    static QUrl guideUrl(const QVariantMap &run);
    Q_INVOKABLE bool hasGuide(const QVariantMap &run) const;
    Q_INVOKABLE bool openGuide(const QVariantMap &run) const;
    Q_INVOKABLE QVariantMap dutyIdentity(const QVariantMap &run) const;
    Q_INVOKABLE QString newId() const;
    Q_INVOKABLE QVariantList findDuties(const QString &region, const QString &query) const;
    Q_INVOKABLE void request(int ticket, const QString &action, const QVariantMap &payload = {});
    Q_INVOKABLE QString exportFile(const QVariantList &items) const;
    Q_INVOKABLE QVariantMap pickImport() const;
    Q_INVOKABLE bool copy(const QString &value) const;
    Q_INVOKABLE bool openGitHub(const QString &target, int issue = 0) const;

signals:
    void answered(int ticket, const QVariantMap &result, const QString &error);

private:
    QPointer<IBackend> m_backend;
};

} // namespace mr
