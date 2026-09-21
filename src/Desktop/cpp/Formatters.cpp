#include "Formatters.h"

#include <QDateTime>
#include <QDir>
#include <QHash>
#include <QLocale>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QStringList>
#include <QTimeZone>

namespace mr {

Formatters::Formatters(QObject *parent) : QObject(parent) {}

QDateTime Formatters::parse(const QVariant &utcIso)
{
    if (!utcIso.isValid() || utcIso.isNull())
        return {};
    const QString text = utcIso.toString();
    if (text.isEmpty())
        return {};

    QDateTime dt = QDateTime::fromString(text, Qt::ISODateWithMs);
    if (!dt.isValid())
        dt = QDateTime::fromString(text, Qt::ISODate);
    if (!dt.isValid())
        return {};
    if (dt.timeSpec() == Qt::LocalTime)
        dt.setTimeZone(QTimeZone::UTC);
    return dt.toLocalTime();
}

QString Formatters::localTime(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    return dt.isValid() ? dt.toString(QStringLiteral("HH:mm:ss")) : dash();
}

QString Formatters::localDate(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    return dt.isValid() ? dt.toString(QStringLiteral("yyyy-MM-dd")) : dash();
}

QString Formatters::localDateTime(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    return dt.isValid() ? dt.toString(QStringLiteral("yyyy-MM-dd HH:mm:ss")) : dash();
}

QString Formatters::dateWithWeekday(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    if (!dt.isValid())
        return dash();
    static const char *kWeekdays[] = {
        "周一", "周二", "周三", "周四",
        "周五", "周六", "周日"
    };
    const int index = dt.date().dayOfWeek() - 1; // 1..7 -> 0..6
    const QString weekday = (index >= 0 && index < 7)
                                ? QString::fromUtf8(kWeekdays[index])
                                : QString();
    return dt.toString(QStringLiteral("yyyy-MM-dd")) + QLatin1Char(' ') + weekday;
}

QString Formatters::duration(const QVariant &milliseconds)
{
    if (!milliseconds.isValid() || milliseconds.isNull())
        return dash();
    bool okConversion = false;
    const double ms = milliseconds.toDouble(&okConversion);
    if (!okConversion || ms < 0)
        return dash();

    const qint64 totalSeconds = static_cast<qint64>(ms / 1000.0);
    const qint64 hours = totalSeconds / 3600;
    const qint64 minutes = (totalSeconds % 3600) / 60;
    const qint64 seconds = totalSeconds % 60;

    if (hours > 0) {
        return QStringLiteral("%1:%2:%3")
            .arg(hours)
            .arg(minutes, 2, 10, QLatin1Char('0'))
            .arg(seconds, 2, 10, QLatin1Char('0'));
    }
    return QStringLiteral("%1:%2")
        .arg(totalSeconds / 60, 2, 10, QLatin1Char('0'))
        .arg(seconds, 2, 10, QLatin1Char('0'));
}

QString Formatters::percent(const QVariant &ratio, int decimals)
{
    if (!ratio.isValid() || ratio.isNull())
        return dash();
    bool okConversion = false;
    const double value = ratio.toDouble(&okConversion);
    if (!okConversion)
        return dash();
    return QString::number(value * 100.0, 'f', decimals) + QLatin1Char('%');
}

QString Formatters::count(const QVariant &value)
{
    if (!value.isValid() || value.isNull())
        return dash();
    bool okConversion = false;
    const qlonglong number = value.toLongLong(&okConversion);
    if (!okConversion)
        return dash();
    return QString::number(number);
}

QString Formatters::gameVersionLabel(const QVariant &build)
{
    if (!build.isValid() || build.isNull())
        return dash();
    const QString text = build.toString().trimmed();
    if (text.isEmpty())
        return dash();
    // 2026.09.01.0000.0000 -> 2026.09.01. Anything else is shown as it is: a
    // version this build does not recognise is still the truth, and inventing
    // an em dash for it would hide the one fact the player asked for.
    const QStringList parts = text.split(QLatin1Char('.'));
    if (parts.size() < 4)
        return text;
    for (int index = 0; index < 3; ++index) {
        bool numeric = false;
        parts.at(index).toInt(&numeric);
        if (!numeric || parts.at(index).isEmpty())
            return text;
    }
    return parts.mid(0, 3).join(QLatin1Char('.'));
}

QString Formatters::resultLabel(const QString &code)
{
    if (code == QLatin1String("COMPLETED"))              return QString::fromUtf8("通关");
    if (code == QLatin1String("LEFT_OR_ABANDONED"))      return QString::fromUtf8("退出/放弃");
    if (code == QLatin1String("CANCELLED_BEFORE_ENTRY")) return QString::fromUtf8("进本前取消");
    if (code == QLatin1String("DISCONNECTED"))           return QString::fromUtf8("断线");
    if (code == QLatin1String("INTERRUPTED"))            return QString::fromUtf8("中断");
    return QString::fromUtf8("未知");
}

bool Formatters::runInProgress(const QVariantMap &run)
{
    const QString source = run.value(QStringLiteral("source")).toString();
    return (source.isEmpty() || source == QLatin1String("AUTO_NETWORK"))
        && run.value(QStringLiteral("result"), QStringLiteral("UNKNOWN")).toString() == QLatin1String("UNKNOWN")
        && run.value(QStringLiteral("ended_at_utc")).toString().isEmpty()
        // An unfinished run crash recovery handed over for review is over, not in progress.
        && !run.value(QStringLiteral("pending_review")).toBool()
        && (!run.value(QStringLiteral("entered_at_utc")).toString().isEmpty()
            || !run.value(QStringLiteral("matched_at_utc")).toString().isEmpty());
}

QString Formatters::runResultLabel(const QVariantMap &run)
{
    if (runInProgress(run))
        return QString::fromUtf8("进行中");
    return resultLabel(run.value(QStringLiteral("result"), QStringLiteral("UNKNOWN")).toString());
}

QString Formatters::resultColorToken(const QString &code)
{
    if (code == QLatin1String("COMPLETED"))              return QStringLiteral("green");
    if (code == QLatin1String("LEFT_OR_ABANDONED"))      return QStringLiteral("orange");
    if (code == QLatin1String("CANCELLED_BEFORE_ENTRY")) return QStringLiteral("yellow");
    if (code == QLatin1String("DISCONNECTED"))           return QStringLiteral("red");
    if (code == QLatin1String("INTERRUPTED"))            return QStringLiteral("neutral500");
    return QStringLiteral("neutral400");
}

QString Formatters::resultTagVariant(const QString &code)
{
    if (code == QLatin1String("COMPLETED"))              return QStringLiteral("ink");
    if (code == QLatin1String("LEFT_OR_ABANDONED"))      return QStringLiteral("accent");
    if (code == QLatin1String("CANCELLED_BEFORE_ENTRY")) return QStringLiteral("outline");
    return QStringLiteral("neutral");
}

QString Formatters::sourceLabel(const QString &code)
{
    if (code == QLatin1String("MANUAL")) return QString::fromUtf8("手动");
    if (code == QLatin1String("IMPORT")) return QString::fromUtf8("导入");
    return QString::fromUtf8("自动");
}

QString Formatters::stateLabel(const QString &code)
{
    if (code == QLatin1String("IDLE"))           return QString::fromUtf8("空闲 · 等待匹配");
    if (code == QLatin1String("MENTOR_MATCHED")) return QString::fromUtf8("已匹配导随");
    if (code == QLatin1String("ENTERED_DUTY"))   return QString::fromUtf8("已进入副本");
    if (code == QLatin1String("INTERRUPTED_PENDING_REVIEW"))
        return QString::fromUtf8("中断 · 待复核");
    // The final states share the RunResult vocabulary; UNKNOWN_FINAL_STATE and
    // anything newer read 未知 rather than the raw token.
    return resultLabel(code);
}

QString Formatters::runConfidenceLabel(const QVariantMap &run)
{
    if (runInProgress(run))
        return QString::fromUtf8("结束时评定");
    return confidenceLabel(run.value(QStringLiteral("detection_confidence")).toString());
}

QString Formatters::confidenceLabel(const QString &code)
{
    if (code.isEmpty())                  return dash();
    if (code == QLatin1String("HIGH"))   return QString::fromUtf8("高");
    if (code == QLatin1String("MEDIUM")) return QString::fromUtf8("中");
    if (code == QLatin1String("LOW"))    return QString::fromUtf8("低");
    if (code == QLatin1String("NONE"))   return QString::fromUtf8("无");
    return QString::fromUtf8("未知");
}

QString Formatters::roleLabel(const QString &code)
{
    if (code == QLatin1String("TANK"))   return QString::fromUtf8("坦克");
    if (code == QLatin1String("HEALER")) return QString::fromUtf8("治疗");
    if (code == QLatin1String("DPS"))    return QString::fromUtf8("输出");
    return QString::fromUtf8("未知");
}

QString Formatters::roleColorToken(const QString &code)
{
    if (code == QLatin1String("TANK"))   return QStringLiteral("blue");
    if (code == QLatin1String("HEALER")) return QStringLiteral("green");
    if (code == QLatin1String("DPS"))    return QStringLiteral("red");
    return QStringLiteral("neutral400");
}

namespace {
/// The capture failures a player can actually do something about. Anything
/// missing from this table is deliberately answered with a generic sentence
/// rather than with the raw token: an ERR_ code is not an instruction.
const QHash<QString, QString> &captureErrorTable()
{
    static const QHash<QString, QString> kTable{
        {QStringLiteral("UNAVAILABLE"),
         QString::fromUtf8("本机当前不具备开始监听的条件（通常是缺少 Npcap 驱动或网卡不可用）。")},
        {QStringLiteral("ERR_NPCAP_MISSING"),
         QString::fromUtf8("没有检测到 Npcap 驱动，装好之后回到捕获诊断页点“重新检测”。")},
        {QStringLiteral("ERR_FFXIV_NOT_RUNNING"),
         QString::fromUtf8("没有检测到正在运行的游戏。")},
        {QStringLiteral("ERR_CAPTURE_ALREADY_RUNNING"),
         QString::fromUtf8("已经有一路监听在跑，先停下再重新开始。")},
        {QStringLiteral("ERR_CAPTURE_NOT_RUNNING"),
         QString::fromUtf8("监听没有在运行。")},
        {QStringLiteral("ERR_PROFILE_UNSUPPORTED"),
         QString::fromUtf8("当前游戏版本还没有可用的协议档案。")},
        {QStringLiteral("ERR_DB_BUSY"),
         QString::fromUtf8("本地数据库正忙，稍等一下会自动重试。")},
        {QStringLiteral("ERR_DB_INTEGRITY"),
         QString::fromUtf8("本地数据库校验没通过，请到设置里备份后再排查。")},
        {QStringLiteral("ERR_INTERNAL"),
         QString::fromUtf8("采集服务内部出错，已停止本次监听。")},
    };
    return kTable;
}
} // namespace

bool Formatters::captureErrorKnown(const QString &code)
{
    return captureErrorTable().contains(code);
}

QString Formatters::captureErrorLabel(const QString &code)
{
    return captureErrorTable().value(
        code, QString::fromUtf8("采集服务报告了一个本版本还不认识的问题。"));
}

QString Formatters::eventKindLabel(const QString &code)
{
    // ipc-v1 $defs/CaptureStatus.last_valid_event_kind. ZONE_INITIALIZATION is
    // every zone load, not only a duty entry, so it is named for what it is.
    // MATCH_ANNOUNCED joined the enum on 2026-09-19 (contracts/CHANGELOG.md):
    // it is the server announcing that the match is made, not the finder message
    // CONTENT_FINDER_POP stands for, so it is named apart from it. Without a name
    // of its own the capture page would show the time alone (2026-09-21 audit,
    // finding 20).
    static const QHash<QString, QString> kLabels{
        {QStringLiteral("CONTENT_FINDER_POP"), QString::fromUtf8("匹配成功")},
        {QStringLiteral("ZONE_INITIALIZATION"), QString::fromUtf8("进入区域")},
        {QStringLiteral("ZONE_TERRITORY"), QString::fromUtf8("识别所在区域")},
        {QStringLiteral("DUTY_RESULT"), QString::fromUtf8("副本结算")},
        {QStringLiteral("PLAYER_JOB"), QString::fromUtf8("识别职业")},
        {QStringLiteral("ZONE_LEFT"), QString::fromUtf8("离开副本区域")},
        {QStringLiteral("INSTANCE_LEFT"), QString::fromUtf8("退出副本")},
        {QStringLiteral("MATCH_CANCELLED"), QString::fromUtf8("匹配取消")},
        {QStringLiteral("MATCH_ANNOUNCED"), QString::fromUtf8("匹配成功通知")},
    };
    return kLabels.value(code);
}

QString Formatters::parserErrorLabel(const QString &code)
{
    // ipc-v1 $defs/ParserErrorEntry.code. Every refusal ends the same way for
    // the player: the message is dropped and nothing is recorded from it.
    static const QHash<QString, QString> kLabels{
        {QStringLiteral("E_UNKNOWN_OPCODE"), QString::fromUtf8("档案里没有这种报文，已忽略")},
        {QStringLiteral("E_LEN_MISMATCH"), QString::fromUtf8("报文长度与档案不符，已忽略")},
        {QStringLiteral("E_OFFSET_OOB"), QString::fromUtf8("字段位置超出报文范围，已忽略")},
        {QStringLiteral("E_FIELD_CONSTRAINT"), QString::fromUtf8("字段取值超出档案允许的范围，已忽略")},
        {QStringLiteral("E_PROFILE_UNSUPPORTED"), QString::fromUtf8("当前游戏版本没有可用档案，未解析")},
        {QStringLiteral("E_INTERNAL"), QString::fromUtf8("解析时出现内部错误，已忽略")},
    };
    return kLabels.value(code, QString::fromUtf8("这条游戏数据没能解析，已忽略"));
}

QString Formatters::foldUserPath(const QString &path)
{
    if (path.isEmpty())
        return path;

    QString folded = QDir::fromNativeSeparators(path);
    const QString home =
        QDir::fromNativeSeparators(QStandardPaths::writableLocation(QStandardPaths::HomeLocation));
    if (!home.isEmpty() && folded.startsWith(home, Qt::CaseInsensitive)) {
        folded = QStringLiteral("%USERPROFILE%") + folded.mid(home.size());
    } else {
        // The profile root of *another* account, or of this one under a name
        // QStandardPaths does not report (a redirected profile). Fold the user
        // name out of the well-known Windows layout rather than printing it.
        static const QRegularExpression users(
            QStringLiteral("^([A-Za-z]:)?/Users/[^/]+"),
            QRegularExpression::CaseInsensitiveOption);
        folded.replace(users, QStringLiteral("%USERPROFILE%"));
    }
    return QDir::toNativeSeparators(folded);
}

QString Formatters::fieldLabel(const QString &field)
{
    static const QHash<QString, QString> kLabels{
        {QStringLiteral("result"), QString::fromUtf8("结果")},
        {QStringLiteral("duty_name"), QString::fromUtf8("副本")},
        {QStringLiteral("duty_category"), QString::fromUtf8("类型")},
        {QStringLiteral("content_id"), QStringLiteral("content_id")},
        {QStringLiteral("job_id"), QString::fromUtf8("职业")},
        {QStringLiteral("matched_at_utc"), QString::fromUtf8("匹配时间")},
        {QStringLiteral("entered_at_utc"), QString::fromUtf8("进本时间")},
        {QStringLiteral("ended_at_utc"), QString::fromUtf8("结束时间")},
        {QStringLiteral("duration_ms"), QString::fromUtf8("耗时")},
        {QStringLiteral("contributes_to_goal"), QString::fromUtf8("计入进度")},
        {QStringLiteral("note"), QString::fromUtf8("备注")},
        {QStringLiteral("soft_deleted"), QString::fromUtf8("软删除")},
        {QStringLiteral("pending_review"), QString::fromUtf8("待复核")},
    };
    return kLabels.value(field, field);
}

QString Formatters::revisionValue(const QVariant &value)
{
    if (!value.isValid() || value.isNull())
        return QString::fromUtf8("空");
    if (value.typeId() == QMetaType::Bool)
        return value.toBool() ? QString::fromUtf8("是") : QString::fromUtf8("否");
    const QString text = value.toString();
    if (text.isEmpty())
        return QString::fromUtf8("空");
    // Timestamps are stored as UTC ISO-8601 and read in local time everywhere
    // else in this UI; the audit trail must not be the one exception.
    if (text.size() >= 20 && text.contains(QLatin1Char('T'))
        && text.endsWith(QLatin1Char('Z'))) {
        const QString local = localDateTime(text);
        if (local != dash())
            return local;
    }
    return text;
}

QStringList Formatters::resultCodes()
{
    return {
        QStringLiteral("COMPLETED"),
        QStringLiteral("LEFT_OR_ABANDONED"),
        QStringLiteral("CANCELLED_BEFORE_ENTRY"),
        QStringLiteral("DISCONNECTED"),
        QStringLiteral("INTERRUPTED"),
        QStringLiteral("UNKNOWN"),
    };
}

} // namespace mr
