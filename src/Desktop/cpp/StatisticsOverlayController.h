#pragma once

#include <QObject>
#include <QPointer>
#include <QRect>
#include <QStringList>
#include <QVariantList>

namespace mr {
class AppSettings;

/// Owns presentation preferences/runtime state for the one statistics float.
/// Borrows settings through QPointer; owns no window, backend or data model.
///
/// The float has two forms (docs/ui-design.md §4.9): the full window with its
/// modules, and the one-line mini bar. Both keep the same position; only the
/// full form has a size of its own.
class StatisticsOverlayController final : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool visible READ visible NOTIFY visibleChanged)
    Q_PROPERTY(bool locked READ locked WRITE setLocked NOTIFY lockedChanged)
    Q_PROPERTY(double opacity READ opacity WRITE setOpacity NOTIFY opacityChanged)
    Q_PROPERTY(QRect geometry READ geometry NOTIFY geometryChanged)
    /// The modules on screen, in order. Switching one off keeps its place in
    /// `moduleRows`, so it returns where it was.
    Q_PROPERTY(QStringList modules READ modules WRITE setModules NOTIFY modulesChanged)
    Q_PROPERTY(QVariantList moduleOptions READ moduleOptions NOTIFY moduleOptionsChanged)
    /// Every module in its order, as {key, title, hint, on}: the 自定义 list.
    Q_PROPERTY(QVariantList moduleRows READ moduleRows NOTIFY modulesChanged)
    /// 副本排行: 0 = off, 3 / 5 / 10 = top N, allRows = every duty.
    Q_PROPERTY(int rowLimit READ rowLimit WRITE setRowLimit NOTIFY rowLimitChanged)
    Q_PROPERTY(int allRows READ allRows CONSTANT)
    /// "full" or "mini".
    Q_PROPERTY(QString form READ form WRITE setForm NOTIFY formChanged)
    /// 文字标签: module and legend names on screen; off, they are tooltips.
    Q_PROPERTY(bool labels READ labels WRITE setLabels NOTIFY labelsChanged)
public:
    static constexpr int kAllRows = 1000;
    static constexpr double kDefaultOpacity = 0.85;
    static constexpr double kMinimumOpacity = 0.3;
    static constexpr int kDefaultRowLimit = 3;

    explicit StatisticsOverlayController(AppSettings *settings, QObject *parent = nullptr);
    bool visible() const { return m_visible; }
    bool locked() const { return m_locked; }
    double opacity() const { return m_opacity; }
    QRect geometry() const { return m_geometry; }
    QStringList modules() const { return m_modules; }
    QVariantList moduleOptions() const;
    QVariantList moduleRows() const;
    int rowLimit() const { return m_rowLimit; }
    int allRows() const { return kAllRows; }
    QString form() const { return m_form; }
    bool labels() const { return m_labels; }
    static QStringList defaultModules();
    static QRect validGeometry(const QRect &geometry);
    /// Fits to one available screen, including negative-coordinate monitors.
    static QRect fitGeometry(const QRect &geometry, const QRect &available);
    /// Opens the float. The full form is fitted to a screen here; the mini bar
    /// is fitted by its window, which alone knows the bar's height.
    Q_INVOKABLE void show();
    Q_INVOKABLE void hide();
    Q_INVOKABLE void toggle();
    Q_INVOKABLE void unlock() { setLocked(false); }
    Q_INVOKABLE void setGeometry(const QRect &geometry);
    /// The mini bar moved: keep \a shown (its own size) on screen and store its
    /// position with the full form's size, which is not fitted here.
    Q_INVOKABLE void setPosition(const QRect &shown);
    Q_INVOKABLE void toggleModule(const QString &key);
    Q_INVOKABLE void moveModule(const QString &key, int direction);
    /// Drag-and-drop: put \a key at \a index of the full order.
    Q_INVOKABLE void moveModuleTo(const QString &key, int index);
    /// 恢复默认: modules, ranking, opacity and labels; the form and geometry stay.
    Q_INVOKABLE void reset();
    void setLocked(bool locked);
    void setOpacity(double opacity);
    void setModules(const QStringList &modules);
    void setRowLimit(int limit);
    void setForm(const QString &form);
    void setLabels(bool labels);
Q_SIGNALS:
    void visibleChanged();
    void lockedChanged();
    void opacityChanged();
    void geometryChanged();
    void modulesChanged();
    void moduleOptionsChanged();
    void rowLimitChanged();
    void formChanged();
    void labelsChanged();
    /// Manual open only: the existing models may refresh once.
    void opened();
private:
    QPointer<AppSettings> m_settings;
    void storeModules();
    QRect m_geometry;
    /// Every module key in display order; m_modules is the enabled subset.
    QStringList m_order;
    QStringList m_modules;
    QString m_form;
    bool m_visible = false;
    bool m_locked = false;
    bool m_labels = false;
    double m_opacity = kDefaultOpacity;
    int m_rowLimit = kDefaultRowLimit;
};
} // namespace mr
