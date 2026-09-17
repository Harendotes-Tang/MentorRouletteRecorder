#include "RunFormValidator.h"

#include "Formatters.h"

#include <QDate>
#include <QDateTime>
#include <QTime>

namespace {

QVariantMap failure(const QString &code, const QString &message)
{
    QVariantMap out;
    out.insert(QStringLiteral("ok"), false);
    out.insert(QStringLiteral("code"), code);
    out.insert(QStringLiteral("message"), message);
    return out;
}

QVariantMap success()
{
    QVariantMap out;
    out.insert(QStringLiteral("ok"), true);
    out.insert(QStringLiteral("code"), QString());
    out.insert(QStringLiteral("message"), QString());
    return out;
}

QString text(const QVariantMap &map, const char *key)
{
    return map.value(QString::fromLatin1(key)).toString().trimmed();
}

QString display(const QString &value)
{
    return value.isEmpty() ? mr::Formatters::dash() : value;
}

QString eventDate(const QVariantMap &form, const char *key)
{
    const QString name = QString::fromLatin1(key);
    return form.contains(name) ? text(form, key) : text(form, "date");
}

} // namespace

namespace mr {

RunFormValidator::RunFormValidator(QObject *parent) : QObject(parent) {}

bool RunFormValidator::isValidDate(const QString &value)
{
    return QDate::fromString(value.trimmed(), QStringLiteral("yyyy-MM-dd")).isValid();
}

QVariant RunFormValidator::toDateTime(const QString &date, const QString &time)
{
    const QDate day = QDate::fromString(date.trimmed(), QStringLiteral("yyyy-MM-dd"));
    if (!day.isValid() || time.trimmed().isEmpty())
        return {};

    QTime clock = QTime::fromString(time.trimmed(), QStringLiteral("HH:mm:ss.zzz"));
    if (!clock.isValid())
        clock = QTime::fromString(time.trimmed(), QStringLiteral("HH:mm:ss"));
    if (!clock.isValid())
        clock = QTime::fromString(time.trimmed(), QStringLiteral("HH:mm"));
    if (!clock.isValid())
        return {};
    return QVariant::fromValue(QDateTime(day, clock));
}

QVariantMap RunFormValidator::validate(const QVariantMap &form, const QVariantMap &before)
{
    const QString reasonLabel = form.value(QStringLiteral("reason_label"),
                                           QString::fromUtf8("修正原因")).toString();

    if (text(form, "reason").isEmpty()) {
        return failure(QStringLiteral("ERR_REASON_REQUIRED"),
                       QString::fromUtf8("必须填写%1，请求已拒绝（ERR_REASON_REQUIRED）。")
                           .arg(reasonLabel));
    }

    const QString date = text(form, "date");
    if (!isValidDate(date)) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("日期格式必须是 yyyy-MM-dd。"));
    }

    const QString matchedText = text(form, "matched");
    if (matchedText.isEmpty()) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("匹配时间不能为空。"));
    }

    const QVariant matchedVar = toDateTime(date, matchedText);
    if (!matchedVar.isValid()) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("匹配时间格式必须是 HH:mm:ss。"));
    }
    const QDateTime matched = matchedVar.toDateTime();

    const QString enteredText = text(form, "entered");
    const QString endedText = text(form, "ended");
    const QString enteredDate = eventDate(form, "entered_date");
    const QString endedDate = eventDate(form, "ended_date");
    if (!enteredText.isEmpty() && !isValidDate(enteredDate)) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("进本日期格式必须是 yyyy-MM-dd。"));
    }
    if (!endedText.isEmpty() && !isValidDate(endedDate)) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("结束日期格式必须是 yyyy-MM-dd。"));
    }
    const QVariant enteredVar = toDateTime(enteredDate, enteredText);
    const QVariant endedVar = toDateTime(endedDate, endedText);

    if (!enteredText.isEmpty() && !enteredVar.isValid()) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("进本时间格式必须是 HH:mm:ss。"));
    }
    if (!endedText.isEmpty() && !endedVar.isValid()) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("结束时间格式必须是 HH:mm:ss。"));
    }

    const QDateTime entered = enteredVar.toDateTime();
    const QDateTime ended = endedVar.toDateTime();

    if (entered.isValid() && entered < matched) {
        return failure(QStringLiteral("ERR_TIME_ORDER"),
                       QString::fromUtf8(
                           "时间顺序错误：进本时间早于匹配时间（ERR_TIME_ORDER）。"));
    }
    if (ended.isValid() && entered.isValid() && ended < entered) {
        return failure(QStringLiteral("ERR_NEGATIVE_DURATION"),
                       QString::fromUtf8("时间顺序错误：结束时间早于进本时间，耗时为负"
                                         "（ERR_NEGATIVE_DURATION）。"));
    }
    if (ended.isValid() && !entered.isValid() && ended < matched) {
        return failure(QStringLiteral("ERR_TIME_ORDER"),
                       QString::fromUtf8(
                           "时间顺序错误：结束时间早于匹配时间（ERR_TIME_ORDER）。"));
    }

    const QString result = form.value(QStringLiteral("result")).toString();
    if (result != QLatin1String("CANCELLED_BEFORE_ENTRY") && !entered.isValid()) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("非“进本前取消”的结果必须有进本时间。"));
    }
    if (result == QLatin1String("COMPLETED") && !ended.isValid()) {
        return failure(QStringLiteral("ERR_BAD_REQUEST"),
                       QString::fromUtf8("通关记录必须有结束时间。"));
    }

    if (form.value(QStringLiteral("edit_mode")).toBool() && diff(before, form).isEmpty()) {
        return failure(QStringLiteral("ERR_NO_CHANGES"),
                       QString::fromUtf8("没有任何字段被修改。"));
    }

    return success();
}

QVariantList RunFormValidator::diff(const QVariantMap &before, const QVariantMap &form)
{
    QVariantList rows;
    const auto add = [&rows](const QString &label, const QString &a, const QString &b) {
        if (a == b)
            return;
        QVariantMap row;
        row.insert(QStringLiteral("k"), label);
        row.insert(QStringLiteral("a"), display(a));
        row.insert(QStringLiteral("b"), display(b));
        rows.append(row);
    };

    add(QString::fromUtf8("副本"), text(before, "duty_name"), text(form, "duty_name"));
    add(QString::fromUtf8("职业"), text(before, "job_name"), text(form, "job_name"));
    add(QString::fromUtf8("匹配日期"), text(before, "date"), text(form, "date"));
    add(QString::fromUtf8("匹配时间"), text(before, "matched"), text(form, "matched"));
    if (!text(before, "entered").isEmpty() || !text(form, "entered").isEmpty())
        add(QString::fromUtf8("进本日期"), eventDate(before, "entered_date"),
            eventDate(form, "entered_date"));
    add(QString::fromUtf8("进本时间"), text(before, "entered"), text(form, "entered"));
    if (!text(before, "ended").isEmpty() || !text(form, "ended").isEmpty())
        add(QString::fromUtf8("结束日期"), eventDate(before, "ended_date"),
            eventDate(form, "ended_date"));
    add(QString::fromUtf8("结束时间"), text(before, "ended"), text(form, "ended"));
    add(QString::fromUtf8("结果"),
        Formatters::resultLabel(before.value(QStringLiteral("result")).toString()),
        Formatters::resultLabel(form.value(QStringLiteral("result")).toString()));
    add(QString::fromUtf8("计入进度"),
        before.value(QStringLiteral("contributes")).toBool() ? QString::fromUtf8("是")
                                                             : QString::fromUtf8("否"),
        form.value(QStringLiteral("contributes")).toBool() ? QString::fromUtf8("是")
                                                           : QString::fromUtf8("否"));
    add(QString::fromUtf8("备注"), text(before, "note"), text(form, "note"));
    return rows;
}

} // namespace mr
