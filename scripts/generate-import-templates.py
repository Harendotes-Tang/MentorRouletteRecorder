"""Build deterministic embedded import templates with Python's standard library.

Run from any directory. Data fields are projections of RunImportSourceParser's
existing aliases; production parser tests guard against contract drift.
"""
from pathlib import Path
import json
import zipfile
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src/Desktop/resources/import-templates"
FIELDS = [
    ("副本", "duty_name", "副本名称；可留空，之后在预览中补充。"),
    ("职业", "job_name", "中文职业名、英文名或缩写；不确定时留空。"),
    ("开始时间", "entered_at_utc", "实际进本时间。填写完整日期和时分，例如 2026-10-10 20:00；未知时留空。"),
    ("结束时间", "ended_at_utc", "实际结束时间。格式同开始时间，不能早于开始时间；未知时留空。"),
    ("结果", "result", "通关、未知、离开、放弃、取消、中断、掉线。留空会按通关预填，导入前必须核对。"),
    ("耗时", "duration_ms", "非负整数，单位毫秒。例如 20 分钟填 1200000；不能填 20分钟 或 20:00。未知时留空。"),
    ("心得", "reflection_text", "填写自己的心得正文；可以换行。最多 2000 字符。"),
    ("心情", "reflection_mood", "good（顺利）、ok（一般）、bad（糟糕）、unknown（未记录）；只填写英文值。"),
    ("记录时间", "source_recorded_at", "原站或原表的记录时间；与实际进本/结束时间独立，不会代替它们。"),
    ("备注", "note", "记录备注，可留空；最多 1000 字符。"),
]
RULES = [
    ("填写和导入", "Excel 只填写第一张“记录”工作表，从第 2 行开始，每行一条。请保留表头；说明页不会被导入。保存后在“历史 → 导入记录”选择文件、核对并勾选本人记录。"),
    ("空白模板", "模板没有示例记录或固定记录编号。未知事实请留空，不要为了计入进度而补造时间。至少填写副本、职业、游戏时间、来源时间、心得或备注中的一项。"),
    ("时间与统计", "在导入窗口选择原记录的时区（中国时间为 +08:00）。填写实际游戏时间时须有完整日期和时分；未知时间留空。确认本人导随及通关结果后，缺少进本或结束时间仅显示为待补充，仍计入完成次数与成就；未知耗时不计入平均耗时。"),
    ("工作簿规则", "支持 .xlsx 和真实 Excel 97–2003 二进制 .xls，读取第一个可见工作表。公式须粘贴为值；含宏或加密文件不支持。HTML/XML 改后缀的伪 .xls 不支持。"),
    ("JSON 规则", "保留最外层数组和英文字段名，一条对象是一条记录。按需要复制对象并用逗号隔开；未知时间或耗时保留 null，正文写在双引号内。JSON 不支持注释或尾随逗号，文件保存为 UTF-8。"),
    ("JSON 时间", "可填写带时区的时间，例如 2026-10-10T20:00:00+08:00（中国时间）或 2026-10-10T12:00:00Z（UTC）。没有时区的本地时间按导入窗口选择的来源时区处理。"),
    ("JSON 结果", "result 可填中文结果，也可填 COMPLETED、UNKNOWN、LEFT_OR_ABANDONED、CANCELLED_BEFORE_ENTRY、INTERRUPTED、DISCONNECTED。"),
    ("保存前核对", "原站时间不能当作实际游戏时间。来源没有结果时会默认通关；导入前请核对结果、职业和心得，并确认记录属于自己。重复记录跳过，冲突记录保留本地。"),
    ("数量限制", "单个文件最多 20 MiB，一次最多 5000 条数据、200 列；不要在文件中预先铺满空白格式行。"),
]


def cell(column: int, row: int, text: str, style: int = 0) -> str:
    name = chr(65 + column)
    return f'<c r="{name}{row}" t="inlineStr" s="{style}"><is><t xml:space="preserve">{escape(text)}</t></is></c>'


def make_workbook() -> dict[str, str]:
    ns = 'xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"'
    widths = [38, 20, 25, 25, 16, 18, 55, 16, 25, 45]
    columns = ''.join(f'<col min="{i+1}" max="{i+1}" width="{width}" customWidth="1" style="2"/>' for i, width in enumerate(widths))
    header = ''.join(cell(i, 1, field[0], 1) for i, field in enumerate(FIELDS))
    # Only one physical blank row: validation ranges do not allocate 5000 rows.
    blank = ''.join(cell(i, 2, '', 2) for i in range(len(FIELDS)))
    validations = ''.join(
        f'<dataValidation type="list" allowBlank="1" showErrorMessage="1" errorTitle="请使用支持的值" error="请选择列表中的值" sqref="{col}2:{col}5001"><formula1>"{choices}"</formula1></dataValidation>'
        for col, choices in [('E', '通关,未知,离开,放弃,取消,中断,掉线'), ('H', 'good,ok,bad,unknown')]
    )
    data = (f'<worksheet {ns}><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>'
            f'<sheetFormatPr defaultRowHeight="24"/><cols>{columns}</cols><sheetData><row r="1" ht="30" customHeight="1">{header}</row><row r="2" ht="42" customHeight="1">{blank}</row></sheetData>'
            f'<autoFilter ref="A1:J1"/><dataValidations count="2">{validations}</dataValidations></worksheet>')
    notes = [('填写说明', '请先阅读；第一张“记录”表才是填写区。时间和副本示例仅出现在说明中，不作为记录导入。')]
    notes += RULES
    notes += [('Excel 表头 / JSON 字段', '含义及填写规则')]
    notes += [(f'{label} / {key}', description) for label, key, description in FIELDS]
    rows = ''.join(f'<row r="{i}" ht="70" customHeight="1">{cell(0,i,label,1)}{cell(1,i,text,2)}</row>' for i, (label, text) in enumerate(notes, 1))
    instructions = (f'<worksheet {ns}><sheetViews><sheetView workbookViewId="0"/></sheetViews><cols><col min="1" max="1" width="32" customWidth="1"/><col min="2" max="2" width="100" customWidth="1"/></cols><sheetData>{rows}</sheetData></worksheet>')
    return {
        '[Content_Types].xml': '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
        '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml': f'<workbook {ns} xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><bookViews><workbookView activeTab="0"/></bookViews><sheets><sheet name="记录" sheetId="1" r:id="rId1"/><sheet name="填写说明" sheetId="2" r:id="rId2"/></sheets></workbook>',
        'xl/_rels/workbook.xml.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>',
        'xl/styles.xml': f'<styleSheet {ns}><fonts count="2"><font><sz val="11"/><name val="Microsoft YaHei"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Microsoft YaHei"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF334155"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="49" fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf><xf numFmtId="49" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>',
        'xl/worksheets/sheet1.xml': data,
        'xl/worksheets/sheet2.xml': instructions,
    }


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(OUTPUT / 'personal-records.xlsx', 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for name, body in make_workbook().items():
            info = zipfile.ZipInfo(name, (2026, 10, 10, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' + body)
    record = {key: (None if key in {'entered_at_utc', 'ended_at_utc', 'duration_ms', 'source_recorded_at'} else 'unknown' if key == 'reflection_mood' else '') for _, key, _ in FIELDS}
    (OUTPUT / 'personal-records.json').write_text(json.dumps([record], ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    help_text = '\n\n'.join(f'{label}\n{text}' for label, text in RULES)
    help_text += '\n\n字段对照\n' + '\n'.join(f'{label} → {key}：{text}' for label, key, text in FIELDS) + '\n'
    (OUTPUT / 'instructions.txt').write_text(help_text, encoding='utf-8', newline='\n')
    print(f'Generated XLSX, JSON and Chinese instructions in {OUTPUT}')


if __name__ == '__main__':
    main()
