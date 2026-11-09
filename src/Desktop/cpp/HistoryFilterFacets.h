#pragma once

#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QTimer>
#include <QVariantMap>

namespace mr {
class IBackend;

/// 历史日期和区服菜单的全量匹配数量；不持有或修改历史分页结果。
/// HistoryController 拥有本对象，后端为受 QPointer 保护的借用。只有菜单启用时
/// 才查询；筛选、连接或可见性变化使旧代回调失效，一批全部结束后统一公布。
class HistoryFilterFacets final : public QObject
{
    Q_OBJECT
    QML_ELEMENT
    QML_UNCREATABLE("HistoryFilterFacets is owned by the history controller")
    Q_PROPERTY(bool active READ active WRITE setActive NOTIFY activeChanged)
    Q_PROPERTY(QVariantMap dateCounts READ dateCounts NOTIFY changed)
    Q_PROPERTY(QVariantMap regionCounts READ regionCounts NOTIFY changed)
    Q_PROPERTY(bool loading READ loading NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
public:
    explicit HistoryFilterFacets(IBackend *backend, QObject *parent = nullptr);
    bool active() const { return m_active; }
    void setActive(bool active);
    QVariantMap dateCounts() const { return m_dateCounts; }
    QVariantMap regionCounts() const { return m_regionCounts; }
    bool loading() const { return m_loading; }
    QString error() const { return m_error; }
    /// 接收已经确认且实际应用的筛选，不接收 QML 中尚未提交的输入草稿。
    void setFilter(const QJsonObject &filter);
    /// 使旧批次失效；隐藏时仅清空，显示时合并同一事件轮中的重复刷新。
    Q_INVOKABLE void refresh();
    /// 预设的本地自然日起止（yyyy-MM-dd）；all/未知键返回两个空串。
    /// 近 7/30 天包含今天，月范围从当月一日到今天。
    Q_INVOKABLE QVariantMap dateRange(const QString &preset) const;
Q_SIGNALS:
    void activeChanged();
    void changed();
private:
    void queryBatch();
    QPointer<IBackend> m_backend;
    QTimer m_refreshTimer;
    QJsonObject m_filter;
    QVariantMap m_dateCounts;
    QVariantMap m_regionCounts;
    QVariantMap m_pendingDates;
    QVariantMap m_pendingRegions;
    quint64 m_generation = 0;
    int m_remaining = 0;
    bool m_active = false;
    bool m_loading = false;
    bool m_failed = false;
    QString m_error;
};
} // namespace mr
