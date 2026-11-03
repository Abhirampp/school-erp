using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SchoolErp.Api.Modules.Admin;
using SchoolErp.Api.Modules.Exams;
using SchoolErp.Api.Modules.Fees;
using SchoolErp.Api.Modules.Payroll;
using SchoolErp.Api.Modules.Students;

namespace SchoolErp.Api.Shared;

/// <summary>PDF documents (QuestPDF). Layout mirrors for right-to-left languages.</summary>
public static class Pdf
{
    private static readonly Dictionary<string, Dictionary<string, string>> Labels = new()
    {
        ["en"] = new()
        {
            ["report"] = "Report card", ["student"] = "Student", ["admission"] = "Admission no.", ["class"] = "Class", ["roll"] = "Roll no.", ["year"] = "Academic year", ["term"] = "Term",
            ["subject"] = "Subject", ["percent"] = "%", ["grade"] = "Grade", ["points"] = "Points", ["remark"] = "Remark", ["overall"] = "Overall", ["gpa"] = "GPA",
            ["attendance"] = "Attendance", ["teacher"] = "Class teacher", ["principal"] = "Principal", ["absent"] = "Abs", ["pending"] = "No approved results for this term yet."
        },
        ["ar"] = new()
        {
            ["report"] = "بطاقة التقرير", ["student"] = "الطالب", ["admission"] = "رقم القبول", ["class"] = "الصف", ["roll"] = "الرقم", ["year"] = "العام الدراسي", ["term"] = "الفصل",
            ["subject"] = "المادة", ["percent"] = "٪", ["grade"] = "التقدير", ["points"] = "النقاط", ["remark"] = "ملاحظة", ["overall"] = "المجموع", ["gpa"] = "المعدل",
            ["attendance"] = "الحضور", ["teacher"] = "معلم الصف", ["principal"] = "المدير", ["absent"] = "غائب", ["pending"] = "لا توجد نتائج معتمدة لهذا الفصل بعد."
        },
        ["hi"] = new()
        {
            ["report"] = "रिपोर्ट कार्ड", ["student"] = "छात्र", ["admission"] = "प्रवेश संख्या", ["class"] = "कक्षा", ["roll"] = "अनुक्रमांक", ["year"] = "शैक्षणिक वर्ष", ["term"] = "सत्र",
            ["subject"] = "विषय", ["percent"] = "%", ["grade"] = "ग्रेड", ["points"] = "अंक", ["remark"] = "टिप्पणी", ["overall"] = "कुल", ["gpa"] = "जीपीए",
            ["attendance"] = "उपस्थिति", ["teacher"] = "कक्षा अध्यापक", ["principal"] = "प्रधानाचार्य", ["absent"] = "अनु.", ["pending"] = "इस सत्र के लिए अभी कोई स्वीकृत परिणाम नहीं है।"
        }
    };

    /// <summary>
    /// Fallback chain covering Latin, Arabic and Devanagari. QuestPDF refuses to render with a family that is
    /// not installed, so each candidate is probed once and only the ones present on this machine are used.
    /// Linux containers should install the Noto fonts (or drop .ttf files next to the app).
    /// </summary>
    private static readonly Lazy<string[]> Fonts = new(() =>
    {
        QuestPDF.Settings.UseSystemFonts = true;
        string[] candidates = ["Segoe UI", "Arial", "Noto Sans", "Noto Sans Arabic", "Noto Sans Devanagari", "Nirmala UI", "DejaVu Sans"];
        var available = candidates.Where(f =>
        {
            try { Document.Create(d => d.Page(p => p.Content().Text("a").FontFamily(f))).GeneratePdf(); return true; }
            catch (Exception) { return false; }
        }).ToList();
        available.Add("Lato");
        return [.. available];
    });

    private static void Setup(PageDescriptor p, bool rtl)
    {
        p.Size(PageSizes.A4);
        p.Margin(36);
        p.DefaultTextStyle(t => t.FontSize(10).FontFamily(Fonts.Value));
        if (rtl) p.ContentFromRightToLeft();
    }

    private static string Num(decimal? v) => v is null ? "–" : v.Value.ToString("0.##");

    public static byte[] ReportCards(List<ReportCard> cards, string locale)
    {
        var lang = locale.Split('-')[0].ToLowerInvariant();
        var L = Labels.GetValueOrDefault(lang, Labels["en"]);
        var rtl = lang is "ar" or "he" or "ur" or "fa";
        return Document.Create(doc =>
        {
            foreach (var card in cards)
                doc.Page(p =>
                {
                    Setup(p, rtl);
                    p.Header().Column(c =>
                    {
                        c.Item().Text(card.School).FontSize(18).Bold();
                        c.Item().Text($"{card.Campus} · {card.Curriculum}").FontColor(Colors.Grey.Darken1);
                        c.Item().PaddingTop(8).Text($"{L["report"]} · {card.Year} · {card.Term}").FontSize(13).SemiBold();
                        c.Item().PaddingVertical(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                    });
                    p.Content().Column(c =>
                    {
                        c.Spacing(10);
                        c.Item().Table(t =>
                        {
                            t.ColumnsDefinition(d => { d.ConstantColumn(90); d.RelativeColumn(); d.ConstantColumn(90); d.RelativeColumn(); });
                            void Row(string k1, string v1, string k2, string v2)
                            {
                                t.Cell().Text(k1).FontColor(Colors.Grey.Darken1); t.Cell().Text(v1).SemiBold();
                                t.Cell().Text(k2).FontColor(Colors.Grey.Darken1); t.Cell().Text(v2).SemiBold();
                            }
                            Row(L["student"], string.IsNullOrWhiteSpace(card.NativeName) ? card.Student : $"{card.Student} ({card.NativeName})", L["admission"], card.AdmissionNumber);
                            Row(L["class"], card.Section, L["roll"], card.RollNumber.ToString());
                        });

                        if (card.Subjects.Count == 0) c.Item().PaddingTop(20).Text(L["pending"]).Italic();
                        else c.Item().Table(t =>
                        {
                            var names = card.Subjects.SelectMany(s => s.Components.Select(x => x.Name)).Distinct().Take(5).ToList();
                            t.ColumnsDefinition(d =>
                            {
                                d.RelativeColumn(3);
                                foreach (var _ in names) d.RelativeColumn(1.4f);
                                d.RelativeColumn(1); d.RelativeColumn(1); d.RelativeColumn(1); d.RelativeColumn(2);
                            });
                            IContainer Head(IContainer x) => x.Background(Colors.Grey.Lighten3).BorderBottom(1).BorderColor(Colors.Grey.Medium).Padding(4);
                            IContainer Cell(IContainer x) => x.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
                            t.Header(h =>
                            {
                                h.Cell().Element(Head).Text(L["subject"]).SemiBold();
                                foreach (var n in names) h.Cell().Element(Head).AlignCenter().Text(n).SemiBold();
                                h.Cell().Element(Head).AlignCenter().Text(L["percent"]).SemiBold();
                                h.Cell().Element(Head).AlignCenter().Text(L["grade"]).SemiBold();
                                h.Cell().Element(Head).AlignCenter().Text(L["points"]).SemiBold();
                                h.Cell().Element(Head).Text(L["remark"]).SemiBold();
                            });
                            foreach (var s in card.Subjects)
                            {
                                t.Cell().Element(Cell).Text(s.Subject);
                                foreach (var n in names)
                                {
                                    var comp = s.Components.FirstOrDefault(x => x.Name == n);
                                    t.Cell().Element(Cell).AlignCenter().Text(comp is null ? "" : comp.Absent ? L["absent"] : $"{Num(comp.Marks)}/{Num(comp.MaxMarks)}");
                                }
                                t.Cell().Element(Cell).AlignCenter().Text(Num(s.Percent));
                                t.Cell().Element(Cell).AlignCenter().Text(s.Grade ?? "–").Bold();
                                t.Cell().Element(Cell).AlignCenter().Text(Num(s.Points));
                                t.Cell().Element(Cell).Text(s.Descriptor ?? "");
                            }
                        });

                        c.Item().PaddingTop(6).Row(r =>
                        {
                            r.RelativeItem().Text(t => { t.Span($"{L["overall"]}: ").FontColor(Colors.Grey.Darken1); t.Span($"{Num(card.OverallPercent)}%").Bold(); });
                            if (card.Gpa is not null) r.RelativeItem().Text(t => { t.Span($"{L["gpa"]}: ").FontColor(Colors.Grey.Darken1); t.Span(Num(card.Gpa)).Bold(); });
                            r.RelativeItem().Text(t => { t.Span($"{L["attendance"]}: ").FontColor(Colors.Grey.Darken1); t.Span(card.AttendanceTotal == 0 ? "–" : $"{card.AttendanceAttended}/{card.AttendanceTotal}").Bold(); });
                        });
                    });
                    p.Footer().PaddingTop(40).Row(r =>
                    {
                        r.RelativeItem().Column(c => { c.Item().Width(140).LineHorizontal(0.7f); c.Item().Text(L["teacher"]).FontColor(Colors.Grey.Darken1); });
                        r.RelativeItem().AlignRight().Column(c => { c.Item().Width(140).LineHorizontal(0.7f); c.Item().Text(L["principal"]).FontColor(Colors.Grey.Darken1); });
                    });
                });
        }).GeneratePdf();
    }

    public static byte[] TransferCertificate(string school, Campus campus, Student s, string? lastClass) =>
        Document.Create(doc => doc.Page(p =>
        {
            Setup(p, false);
            p.Header().Column(c =>
            {
                c.Item().AlignCenter().Text(school).FontSize(20).Bold();
                c.Item().AlignCenter().Text(campus.Name).FontColor(Colors.Grey.Darken1);
                c.Item().PaddingTop(14).AlignCenter().Text("Transfer certificate").FontSize(15).SemiBold();
                c.Item().PaddingVertical(8).LineHorizontal(1);
            });
            p.Content().PaddingTop(10).Table(t =>
            {
                t.ColumnsDefinition(d => { d.ConstantColumn(190); d.RelativeColumn(); });
                void Row(string k, string? v) { t.Cell().PaddingVertical(6).Text(k).FontColor(Colors.Grey.Darken1); t.Cell().PaddingVertical(6).Text(string.IsNullOrWhiteSpace(v) ? "–" : v).SemiBold(); }
                Row("Admission number", s.AdmissionNumber);
                Row("Name of student", string.IsNullOrWhiteSpace(s.NativeName) ? s.FullName() : $"{s.FullName()} ({s.NativeName})");
                Row("Date of birth", s.DateOfBirth?.ToString("d MMMM yyyy"));
                Row("Class last attended", lastClass);
                Row("Date of leaving", s.LeftOn?.ToString("d MMMM yyyy"));
                Row("Reason for leaving", s.LeavingReason);
                Row("Date of issue", DateTime.UtcNow.ToString("d MMMM yyyy"));
            });
            p.Footer().PaddingTop(50).AlignRight().Column(c => { c.Item().Width(160).LineHorizontal(0.7f); c.Item().Text("Principal").FontColor(Colors.Grey.Darken1); });
        })).GeneratePdf();

    public static byte[] Payslip(string school, Campus campus, Payslip p, string currency, List<PayLine> lines) =>
        Document.Create(doc => doc.Page(page =>
        {
            Setup(page, false);
            page.Header().Column(c =>
            {
                c.Item().Text(school).FontSize(16).Bold();
                c.Item().Text(campus.Name).FontColor(Colors.Grey.Darken1);
                c.Item().PaddingTop(10).Text($"Payslip · {DateOnly.ParseExact(p.Month + "-01", "yyyy-MM-dd"):MMMM yyyy}").FontSize(13).SemiBold();
                c.Item().PaddingVertical(6).LineHorizontal(1);
            });
            page.Content().Column(c =>
            {
                c.Spacing(12);
                c.Item().Table(t =>
                {
                    t.ColumnsDefinition(d => { d.ConstantColumn(110); d.RelativeColumn(); d.ConstantColumn(110); d.RelativeColumn(); });
                    void Row(string k1, string? v1, string k2, string? v2)
                    {
                        t.Cell().Text(k1).FontColor(Colors.Grey.Darken1); t.Cell().Text(v1 ?? "–").SemiBold();
                        t.Cell().Text(k2).FontColor(Colors.Grey.Darken1); t.Cell().Text(v2 ?? "–").SemiBold();
                    }
                    Row("Employee", p.StaffName, "Employee no.", p.EmployeeNumber);
                    Row("Designation", p.Designation, "Days in month", p.DaysInMonth.ToString());
                    Row("Unpaid leave", $"{p.UnpaidLeaveDays:0.#} days", "Currency", currency);
                });
                c.Item().Row(r =>
                {
                    void Column(RowDescriptor row, string title, string kind) => row.RelativeItem().Column(col =>
                    {
                        col.Item().BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingBottom(4).Text(title).SemiBold();
                        foreach (var l in lines.Where(x => x.Kind == kind))
                            col.Item().PaddingVertical(3).Row(x => { x.RelativeItem().Text(l.Name); x.AutoItem().Text($"{l.Amount:N2}"); });
                    });
                    Column(r, "Earnings", "earning");
                    r.ConstantItem(24);
                    Column(r, "Deductions", "deduction");
                });
                c.Item().LineHorizontal(0.7f);
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text(t => { t.Span("Gross ").FontColor(Colors.Grey.Darken1); t.Span($"{p.Gross:N2}").SemiBold(); });
                    r.RelativeItem().Text(t => { t.Span("Deductions ").FontColor(Colors.Grey.Darken1); t.Span($"{p.Deductions:N2}").SemiBold(); });
                    r.RelativeItem().AlignRight().Text(t => { t.Span("Net pay ").FontColor(Colors.Grey.Darken1); t.Span($"{p.Net:N2} {currency}").FontSize(13).Bold(); });
                });
            });
            page.Footer().AlignCenter().Text("This is a computer-generated payslip.").FontSize(8).FontColor(Colors.Grey.Medium);
        })).GeneratePdf();

    public static byte[] Receipt(string school, Campus campus, Student s, Invoice invoice, Payment payment) =>
        Document.Create(doc => doc.Page(p =>
        {
            Setup(p, false);
            p.Size(PageSizes.A5);
            p.Header().Column(c =>
            {
                c.Item().Text(school).FontSize(16).Bold();
                c.Item().Text(campus.Name).FontColor(Colors.Grey.Darken1);
                c.Item().PaddingTop(10).Text($"Receipt {payment.ReceiptNumber}").FontSize(13).SemiBold();
                c.Item().PaddingVertical(6).LineHorizontal(1);
            });
            p.Content().Table(t =>
            {
                t.ColumnsDefinition(d => { d.ConstantColumn(130); d.RelativeColumn(); });
                void Row(string k, string? v) { t.Cell().PaddingVertical(4).Text(k).FontColor(Colors.Grey.Darken1); t.Cell().PaddingVertical(4).Text(v ?? "–").SemiBold(); }
                Row("Received from", $"{s.FullName()} ({s.AdmissionNumber})");
                Row("Invoice", $"{invoice.Number} · {invoice.Title}");
                Row("Amount", $"{payment.Amount:0.00} {payment.Currency}");
                Row("Method", payment.Method);
                Row("Date", payment.PaidAt?.ToString("d MMMM yyyy HH:mm 'UTC'"));
                Row("Balance remaining", $"{invoice.Total - invoice.PaidTotal:0.00} {invoice.Currency}");
            });
        })).GeneratePdf();
}
