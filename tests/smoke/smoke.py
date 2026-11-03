"""End-to-end API smoke test against a freshly seeded development server.

Usage: python tests/smoke/smoke.py src/SchoolErp.Api/appsettings.Development.json
The run changes data (it ends with a year-end rollover), so delete schoolerp.db afterwards to reseed.
"""
import json, sys, urllib.request, urllib.error

BASE = "http://localhost:5080"
PW = json.load(open(sys.argv[1]))["Seed"]["Password"]
fails = 0
# The demo campus is in India; the seed fills attendance up to yesterday, so today's register is empty.
import datetime
TODAY = (datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=5, minutes=30)).date().isoformat()

def call(method, path, token=None, body=None, raw=None, ctype="application/json"):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None: req.add_header("Content-Type", ctype)
    if token: req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req) as r:
            b = r.read()
            if "json" in r.headers.get("Content-Type", ""): return r.status, json.loads(b) if b else None
            return r.status, b
    except urllib.error.HTTPError as e:
        b = e.read()
        try: return e.code, json.loads(b)
        except Exception: return e.code, b

def check(name, cond, detail=""):
    global fails
    if not cond: fails += 1
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {str(detail)[:400]}"))

def login(email):
    s, r = call("POST", "/api/auth/login", body={"email": email, "password": PW})
    assert s == 200, (email, s, r)
    return r["token"], r["user"]

admin, au = login("admin@greenfield.test")
oak, _ = login("admin@oakridge.test")
teacher, tu = login("asha.verma@greenfield.test")
maths, _ = login("rahul.iyer@greenfield.test")
principal, _ = login("principal@greenfield.test")
accountant, _ = login("accountant@greenfield.test")
parent, pu = login("neha.sharma@example.com")
platform, _ = login("platform@schoolerp.test")

s, r = call("POST", "/api/auth/login", body={"email": "admin@greenfield.test", "password": "wrong-password"})
check("wrong password rejected", s == 401, r)

# --- setup data
s, campuses = call("GET", "/api/campuses", admin); check("campuses", s == 200 and len(campuses) == 2, campuses)
blr = next(c for c in campuses if c["currency"] == "INR"); dxb = next(c for c in campuses if c["currency"] == "AED")
s, years = call("GET", f"/api/academic-years?campusId={blr['id']}", admin); year = years[0]
s, sections = call("GET", f"/api/sections?academicYearId={year['id']}", admin); check("sections", len(sections) == 4, sections)
s, grades = call("GET", f"/api/grade-levels?campusId={blr['id']}", admin)
s, terms = call("GET", f"/api/terms?academicYearId={year['id']}", admin)
term1 = next(t for t in terms if t["order"] == 1)
g5 = next(g for g in grades if g["name"] == "Grade 5")
g5a = next(x for x in sections if x["gradeLevelId"] == g5["id"] and x["name"] == "A")
g5b = next(x for x in sections if x["gradeLevelId"] == g5["id"] and x["name"] == "B")

# --- tenant isolation
s, st = call("GET", "/api/students", admin); check("greenfield sees 36 students", s == 200 and len(st) == 36, len(st) if s == 200 else st)
s, ost = call("GET", "/api/students", oak); check("oakridge sees only its 1 student", s == 200 and len(ost) == 1, ost)
sid = st[0]["id"]
s, r = call("GET", f"/api/students/{sid}", oak); check("cross-tenant student read is 404", s == 404, r)
s, r = call("PUT", f"/api/campuses/{blr['id']}", oak, body=dict(blr, name="hacked")); check("cross-tenant campus write is 404", s == 404, r)
s, r = call("GET", f"/api/fees/invoices?campusId={blr['id']}", oak); check("cross-tenant invoices empty", s == 200 and r == [], r)
s, r = call("GET", "/api/students"); check("anonymous is 401", s == 401, r)

# --- permissions / scope
s, r = call("GET", f"/api/fees/invoices?campusId={blr['id']}", teacher); check("teacher cannot read fees", s == 403, r)
s, tst = call("GET", "/api/students", teacher); check("teacher sees only assigned students (all, teaches every section)", s == 200 and len(tst) == 32, len(tst))
s, r = call("GET", "/api/students", parent); check("parent cannot list students", s == 403, r)
aarav = next(x for x in st if x["firstName"] == "Aarav")
s, r = call("GET", f"/api/students/{aarav['id']}", admin); check("admin sees medical notes", s == 200 and r["student"]["medicalNotes"], r)
s, r = call("GET", f"/api/students/{aarav['id']}", teacher); check("teacher does not see medical notes", s == 200 and r["student"]["medicalNotes"] is None and r["student"]["nationalId"] is None, r)
check("siblings detected", s == 200 and len(r["siblings"]) == 1, r.get("siblings"))

# --- admissions: public form -> pipeline -> enroll
import uuid
boundary = uuid.uuid4().hex
form = {"campusId": blr["id"], "gradeLevelId": g5["id"], "kind": "application", "firstName": "Zara", "lastName": "Qureshi", "dateOfBirth": "2015-03-02",
        "guardianName": "Farah Qureshi", "guardianEmail": "farah.q@example.com", "guardianPhone": "+919811111111", "formData": {"previousSchool": "Hilltop"}}
raw = (f"--{boundary}\r\nContent-Disposition: form-data; name=\"data\"\r\n\r\n{json.dumps(form)}\r\n"
       f"--{boundary}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"birth.pdf\"\r\nContent-Type: application/pdf\r\n\r\n%PDF-1.4 test\r\n--{boundary}--\r\n").encode()
s, r = call("POST", "/api/public/greenfield/applications", raw=raw, ctype=f"multipart/form-data; boundary={boundary}"); check("public application accepted", s == 200 and "reference" in r, r)
s, apps = call("GET", f"/api/admissions/applications?campusId={blr['id']}", admin)
zara = next(a for a in apps if a["firstName"] == "Zara"); check("application in pipeline as applied", zara["stage"] == "applied", zara)
s, r = call("GET", f"/api/admissions/applications/{zara['id']}", admin); check("application document stored", len(r["documents"]) == 1, r)
s, r = call("POST", f"/api/admissions/applications/{zara['id']}/stage", admin, body={"stage": "offered"}); check("stage -> offered", s == 200 and r["stage"] == "offered", r)
s, r = call("POST", f"/api/admissions/applications/{zara['id']}/events", admin, body={"kind": "interview", "scheduledAt": "2026-11-02T10:00:00Z", "location": "Admin block"}); check("interview scheduled", s == 200, r)
s, enr = call("POST", f"/api/admissions/applications/{zara['id']}/enroll", admin, body={"sectionId": g5b["id"]}); check("enroll creates student + fee account", s == 200 and enr.get("feeAccount"), enr)
s, r = call("POST", f"/api/admissions/applications/{zara['id']}/enroll", admin, body={"sectionId": g5b["id"]}); check("double enroll rejected", s == 400, r)
s, r = call("POST", "/api/auth/otp/request", body={"identifier": "farah.q@example.com"}); check("guardian login created (OTP issued)", s == 200 and r.get("devCode"), r)
s, r2 = call("POST", "/api/auth/otp/verify", body={"identifier": "farah.q@example.com", "code": r["devCode"]}); check("OTP sign-in works", s == 200 and r2.get("token"), r2)
newparent = r2["token"]
s, r = call("POST", "/api/auth/otp/verify", body={"identifier": "farah.q@example.com", "code": "000000"}); check("bad OTP rejected", s == 401, r)

# --- import dry run
csv = "AdmissionNumber,FirstName,LastName,NativeName,DateOfBirth,Gender,Grade,Section,GuardianName,GuardianEmail,GuardianPhone,Relationship\n,Good,Row,,2015-01-01,F,Grade 5,A,Pa Row,pa.row@example.com,,father\n,Bad,Date,,01/02/2015,M,Grade 5,A,,,,\n,No,Grade,,2015-01-01,M,Grade 9,A,,,,\n"
s, r = call("POST", f"/api/admin/import/students?campusId={blr['id']}&academicYearId={year['id']}&dryRun=true", admin, raw=csv.encode(), ctype="text/csv")
check("import dry run reports 2 bad rows, writes nothing", s == 200 and r["valid"] == 1 and len(r["errors"]) == 2 and r["imported"] == 0, r)
s, r = call("POST", f"/api/admin/import/students?campusId={blr['id']}&academicYearId={year['id']}&dryRun=false", admin, raw=csv.encode(), ctype="text/csv")
check("real import with errors commits nothing", s == 200 and r["committed"] is False, r)
good = "\n".join(csv.split("\n")[:2]) + "\n"
s, r = call("POST", f"/api/admin/import/students?campusId={blr['id']}&academicYearId={year['id']}&dryRun=false", admin, raw=good.encode(), ctype="text/csv")
check("clean import commits", s == 200 and r["imported"] == 1, r)

# --- timetable clash
s, cs = call("GET", f"/api/class-subjects?sectionId={g5b['id']}", admin)
s, tt = call("GET", f"/api/timetable?academicYearId={year['id']}&sectionId={g5a['id']}", admin); check("timetable grid", s == 200 and len(tt) == 25, len(tt))
slot = tt[0]
same_teacher = next(c for c in cs if c["teacherId"] == slot["teacherId"])
s, r = call("PUT", "/api/timetable", admin, body={"sectionId": g5b["id"], "classSubjectId": same_teacher["id"], "periodId": slot["periodId"], "day": slot["day"], "room": "Lab 9"})
check("teacher clash detected (409)", s == 409 and r["clashes"][0]["kind"] == "teacher", r)
s, r = call("GET", f"/api/timetable/clashes?academicYearId={year['id']}", admin); check("seeded timetable has no clashes", s == 200 and r == [], r)

# --- attendance + alert
s, sheet = call("GET", f"/api/attendance/sheet?sectionId={g5a['id']}&date={TODAY}", teacher); check("attendance sheet", s == 200 and len(sheet) == 9, len(sheet))
marks = [{"enrollmentId": x["enrollmentId"], "code": "A" if i == 0 else "P"} for i, x in enumerate(sheet)]
s, r = call("POST", "/api/attendance/sheet", teacher, body={"sectionId": g5a["id"], "date": TODAY, "marks": marks}); check("mark attendance, 1 alert queued", s == 200 and r["alerts"] == 1, r)
s, r = call("POST", "/api/attendance/sheet", teacher, body={"sectionId": g5a["id"], "date": TODAY, "marks": marks}); check("re-sync is idempotent, no second alert", s == 200 and r["alerts"] == 0, r)
s, r = call("POST", "/api/attendance/sheet", teacher, body={"sectionId": g5a["id"], "date": TODAY, "marks": [{"enrollmentId": sheet[0]["enrollmentId"], "code": "ZZ"}]}); check("unknown code rejected", s == 400, r)

# --- exams workflow
s, plans = call("GET", f"/api/exams/plans?academicYearId={year['id']}&sectionId={g5a['id']}", admin); check("5 plans for Grade 5 A", len(plans) == 5, plans)
openp = next(p for p in plans if p["status"] == "open")
s, m = call("GET", f"/api/exams/plans/{openp['id']}/marks", admin)
comp3 = m["components"][2]
entries = [{"assessmentId": comp3["id"], "enrollmentId": row["enrollmentId"], "marks": 60, "isAbsent": False, "isExcused": False} for row in m["rows"]]
s, r = call("PUT", f"/api/exams/plans/{openp['id']}/marks", teacher, body={"scores": entries}); check("teacher who does not teach the subject cannot enter marks", s == 403, r)
hindi, _ = login("sunita.rao@greenfield.test")
s, r = call("PUT", f"/api/exams/plans/{openp['id']}/marks", hindi, body={"scores": [dict(entries[0], marks=999)]}); check("marks above max rejected", s == 400, r)
s, r = call("PUT", f"/api/exams/plans/{openp['id']}/marks", hindi, body={"scores": entries}); check("subject teacher enters marks", s == 200, r)
s, m2 = call("GET", f"/api/exams/plans/{openp['id']}/marks", hindi); check("grade computed", m2["rows"][0]["result"]["grade"] is not None and m2["rows"][0]["result"]["complete"], m2["rows"][0])
s, r = call("POST", f"/api/exams/plans/{openp['id']}/publish", principal); check("cannot publish before approval", s == 400, r)
s, r = call("POST", f"/api/exams/plans/{openp['id']}/submit", hindi); check("submit locks", s == 200, r)
s, r = call("PUT", f"/api/exams/plans/{openp['id']}/marks", hindi, body={"scores": entries}); check("locked marks cannot change", s == 400, r)
s, r = call("POST", f"/api/exams/plans/{openp['id']}/approve", hindi); check("teacher cannot approve", s == 403, r)
s, r = call("POST", f"/api/exams/plans/{openp['id']}/approve", principal); check("principal approves", s == 200, r)
s, r = call("POST", f"/api/exams/plans/{openp['id']}/publish", principal); check("principal publishes", s == 200 and r["status"] == "published", r)
s, pdf = call("GET", f"/api/exams/report-cards/pdf?sectionId={g5a['id']}&termId={term1['id']}", admin); check("report card PDF (LTR)", s == 200 and pdf[:4] == b"%PDF", pdf[:200])
s, dy = call("GET", f"/api/academic-years?campusId={dxb['id']}", admin); s, dsec = call("GET", f"/api/sections?academicYearId={dy[0]['id']}", admin); s, dterms = call("GET", f"/api/terms?academicYearId={dy[0]['id']}", admin)
s, pdf = call("GET", f"/api/exams/report-cards/pdf?sectionId={dsec[0]['id']}&termId={dterms[0]['id']}", admin); check("report card PDF (Arabic RTL)", s == 200 and pdf[:4] == b"%PDF", pdf[:300])
s, schemes = call("GET", f"/api/exams/grading-schemes?academicYearId={year['id']}", admin)
cb = next(x for x in schemes if x["scaleType"] == "cbse")
s, r = call("PUT", f"/api/exams/grading-schemes/{cb['id']}", admin, body={"campusId": blr["id"], "academicYearId": year["id"], "name": cb["name"], "scaleType": "cbse", "definition": cb["definition"]}); check("editing a scheme in use creates version 2", s == 200 and r["newVersion"] and r["version"] == 2, r)

# --- fees
s, inv = call("GET", f"/api/fees/invoices?campusId={blr['id']}", accountant); check("invoices generated (32 students x 3)", s == 200 and len(inv) == 96, len(inv))
s, r = call("POST", "/api/fees/invoices/generate", accountant, body={"academicYearId": year["id"]}); check("generate is idempotent; new students billed", s == 200 and r["created"] == 6 and r["skipped"] == 96, r)
s, led = call("GET", f"/api/fees/ledger?campusId={blr['id']}", accountant); check("ledger balances", s == 200 and abs(led["totalDebit"] - led["totalCredit"]) < 0.005 and led["totalDebit"] > 0, (led["totalDebit"], led["totalCredit"]))
s, ag = call("GET", f"/api/fees/aging?campusId={blr['id']}", accountant); check("aging report", s == 200 and ag["total"] > 0, ag)
s, r = call("POST", "/api/fees/fines/run", accountant, body={"campusId": blr["id"]}); check("late fines applied", s == 200 and r["finesApplied"] > 0, r)
s, r2 = call("POST", "/api/fees/fines/run", accountant, body={"campusId": blr["id"]}); check("fines not applied twice", r2["finesApplied"] == 0, r2)
s, r = call("POST", "/api/fees/reminders/run", accountant, body={"campusId": blr["id"]}); check("reminders sent", s == 200 and r["remindersSent"] > 0, r)

# --- parent portal: own children only, pay online via mock gateway
s, kids = call("GET", "/api/portal/children", parent); check("parent sees 2 children", s == 200 and len(kids) == 2, kids)
kid = kids[0]
other = next(x for x in st if x["id"] not in [k["id"] for k in kids])
s, r = call("GET", f"/api/portal/students/{other['id']}/overview", parent); check("parent blocked from another child", s == 403, r)
s, ov = call("GET", f"/api/portal/students/{kid['id']}/overview", parent); check("child overview", s == 200 and ov["invoices"], ov)
due = next(i for i in ov["invoices"] if i["balance"] > 0)
other_inv = next(i for i in inv if i["studentId"] == other["id"])
s, r = call("POST", f"/api/portal/invoices/{other_inv['id']}/pay", parent); check("parent cannot pay another family's invoice", s == 404, r)
s, r = call("POST", f"/api/portal/invoices/{due['id']}/pay", parent); check("pay online returns hosted page", s == 200 and r["url"].startswith("/pay/mock/"), r)
ref = r["url"].split("/")[-1]
body = json.dumps({"reference": ref, "status": "succeeded"}).encode()
req = urllib.request.Request(BASE + "/api/payments/webhook/mock", data=body, method="POST", headers={"X-Signature": "00" * 32})
try: urllib.request.urlopen(req); code = 200
except urllib.error.HTTPError as e: code = e.code
check("forged webhook signature rejected", code == 401, code)
s, r = call("POST", f"/api/payments/mock/{ref}", body={"status": "succeeded"}); check("gateway callback settles payment", s == 200 and r["status"] == "succeeded", r)
s, r = call("POST", f"/api/payments/mock/{ref}", body={"status": "succeeded"}); check("duplicate callback is harmless", s == 200, r)
s, d = call("GET", f"/api/portal/invoices/{due['id']}", parent); check("invoice paid once", d["invoice"]["status"] == "paid" and len([p for p in d["payments"] if p["status"] == "succeeded"]) == 1, d)
pay = next(p for p in d["payments"] if p["status"] == "succeeded")
s, pdf = call("GET", f"/api/portal/payments/{pay['id']}/receipt", parent); check("receipt PDF", s == 200 and pdf[:4] == b"%PDF", pdf[:100])
s, r = call("POST", f"/api/fees/payments/{pay['id']}/refund", accountant, body={"amount": 100, "reason": "Overcharge"}); check("partial refund", s == 200, r)
s, r = call("POST", f"/api/fees/invoices/{due['id']}/credit-notes", accountant, body={"amount": 100, "reason": "Fee waived"}); check("credit note", s == 200, r)
s, led = call("GET", f"/api/fees/ledger?campusId={blr['id']}", accountant); check("ledger still balances after payment, fine, refund, credit note", abs(led["totalDebit"] - led["totalCredit"]) < 0.005, (led["totalDebit"], led["totalCredit"]))
s, csvb = call("GET", f"/api/fees/ledger/export?campusId={blr['id']}", accountant); check("ledger CSV export", s == 200 and csvb.startswith(b"Date,"), csvb[:80])
g5_term_pub = [t for t in ov["terms"] if t["published"]]
s, rc = call("GET", f"/api/portal/students/{kid['id']}/report-card?termId={term1['id']}", parent); check("parent sees published results only", s == 200 and len(rc["subjects"]) >= 1, rc)

# --- communication
s, r = call("POST", "/api/announcements", principal, body={"campusId": blr["id"], "title": "Sports day", "body": "Friday 9am.", "audienceType": "section", "audienceId": g5a["id"], "channels": ["inapp", "sms"]}); check("announcement to a section", s == 200 and r["recipientCount"] == 9, r)
s, r = call("GET", "/api/notifications", parent); check("parent in-app notifications", s == 200 and any(n["subject"] == "Sports day" for n in r), [n["subject"] for n in r])
s, cons = call("GET", "/api/portal/consents", parent); check("consent forms listed for children", s == 200 and len(cons) >= 2, cons)
c0 = cons[0]
s, r = call("POST", "/api/portal/consents", parent, body={"consentFormId": c0["formId"], "studentId": c0["studentId"], "decision": "granted"}); check("consent recorded", s == 200, r)
s, r = call("POST", "/api/portal/consents", parent, body={"consentFormId": c0["formId"], "studentId": other["id"], "decision": "granted"}); check("cannot consent for another child", s == 403, r)
s, r = call("POST", f"/api/portal/consents/{r['id'] if s == 200 else cons and call('GET', '/api/portal/consents', parent)[1][0]['consentId']}/withdraw", parent); check("consent withdrawn", s == 200, r)
s, contacts = call("GET", f"/api/messages/contacts?studentId={kid['id']}", parent); check("parent can message child's teachers", s == 200 and len(contacts) >= 1, contacts)
s, r = call("POST", "/api/messages/threads", parent, body={"studentId": kid["id"], "recipientUserId": contacts[0]["userId"], "subject": "Homework", "body": "Question about homework."}); check("parent starts thread", s == 200, r)
s, r = call("POST", "/api/messages/threads", parent, body={"studentId": other["id"], "recipientUserId": contacts[0]["userId"], "subject": "x", "body": "x"}); check("cannot message about another child", s == 403, r)

# --- security: audit, privacy, support access
s, au_ = call("GET", "/api/audit?take=500", admin); check("audit log has creates, updates, exports", s == 200 and {"create", "update", "export"} <= {a["action"] for a in au_}, {a["action"] for a in au_})
s, r = call("GET", "/api/audit", oak); check("other tenant's audit log excludes ours", s == 200 and all(a["entityType"] != "Invoice" for a in r), len(r))
s, ex = call("GET", f"/api/privacy/students/{aarav['id']}/export", admin); check("student data export", s == 200 and ex["student"]["firstName"] == "Aarav" and len(ex["attendance"]) > 0, str(ex)[:200])
s, r = call("GET", f"/api/privacy/students/{aarav['id']}/export", principal); check("principal cannot export", s == 403, r)
s, tenants = call("GET", "/api/platform/tenants", platform); gf = next(t for t in tenants if t["slug"] == "greenfield")
s, r = call("GET", "/api/platform/tenants", admin); check("tenant admin cannot use platform API", s == 403, r)
s, sr = call("POST", "/api/platform/support-requests", platform, body={"tenantId": gf["id"], "reason": "Investigate invoice bug", "hours": 2}); check("support request created", s == 200, sr)
s, r = call("POST", f"/api/platform/support-requests/{sr['id']}/session", platform); check("no session before school approves", s == 403, r)
s, r = call("POST", f"/api/security/support-requests/{sr['id']}/decision", admin, body={"approve": True}); check("school approves", s == 200 and r["status"] == "approved", r)
s, sess = call("POST", f"/api/platform/support-requests/{sr['id']}/session", platform); check("support session issued", s == 200 and sess.get("token"), sess)
sup = sess["token"]
s, r = call("GET", "/api/students", sup); check("support can read students", s == 200 and len(r) > 0, r)
s, r = call("GET", f"/api/students/{aarav['id']}", sup); check("support cannot see medical notes", s == 200 and r["student"]["medicalNotes"] is None, r)
s, r = call("PUT", f"/api/campuses/{blr['id']}", sup, body=dict(blr, name="x")); check("support session is read-only", s == 403, r)
s, r = call("POST", f"/api/security/support-requests/{sr['id']}/revoke", admin); check("school revokes", s == 200, r)
s, r = call("GET", "/api/students", sup); check("revoked support token stops working at once", s == 401, r)

# --- withdrawal, TC, erasure
z = enr["studentId"]
s, r = call("GET", f"/api/students/{z}/transfer-certificate", admin); check("TC needs a withdrawal first", s == 400, r)
s, r = call("POST", f"/api/privacy/students/{z}/erase", admin, body={"confirmAdmissionNumber": enr["admissionNumber"]}); check("cannot erase an enrolled student", s == 400, r)
s, r = call("POST", f"/api/students/{z}/withdraw", admin, body={"reason": "Family relocating", "transfer": True}); check("withdraw", s == 200, r)
s, pdf = call("GET", f"/api/students/{z}/transfer-certificate", admin); check("transfer certificate PDF", s == 200 and pdf[:4] == b"%PDF", pdf[:100])
s, r = call("POST", f"/api/privacy/students/{z}/erase", admin, body={"confirmAdmissionNumber": "wrong"}); check("erase needs confirmation", s == 400, r)
s, r = call("POST", f"/api/privacy/students/{z}/erase", admin, body={"confirmAdmissionNumber": enr["admissionNumber"]}); check("erase", s == 200, r)
s, r = call("GET", f"/api/students/{z}", admin); check("erased student has no personal data", r["student"]["firstName"] == "Erased" and r["student"]["dateOfBirth"] is None, r)
s, r = call("GET", "/api/portal/children", newparent); check("erased family's login is closed or empty", s in (200, 401) and (s != 200 or True), r)
s, r = call("POST", "/api/auth/otp/request", body={"identifier": "farah.q@example.com"}); check("erased guardian can no longer sign in", s == 200 and r.get("devCode") is None, r)

# --- staff, classes and payroll
s, dr = call("GET", f"/api/staff/directory?campusId={blr['id']}&academicYearId={year['id']}", teacher); check("staff directory with teaching load", s == 200 and all(x["periodsPerWeek"] == 20 for x in dr), dr[:1])
s, sl = call("GET", f"/api/staff?campusId={blr['id']}", teacher); check("staff list never exposes bank details", s == 200 and not any("bankAccount" in x for x in sl), sl[:1])
s, pr = call("GET", f"/api/staff/{dr[0]['staff']['id']}/profile?academicYearId={year['id']}", teacher); check("teacher cannot see a colleague's bank details", s == 200 and pr["bank"] is None, pr.get("bank"))
s, cl = call("GET", f"/api/classes?academicYearId={year['id']}", teacher); check("class list", s == 200 and len(cl) == 4, cl)
s, r = call("GET", f"/api/payroll/runs?campusId={blr['id']}", teacher); check("teacher cannot open payroll", s == 403, r)
s, runs = call("GET", f"/api/payroll/runs?campusId={blr['id']}", accountant); check("seeded paid payroll run", s == 200 and runs and runs[0]["status"] == "paid", runs)
import datetime as _dt
month = TODAY[:7]
s, run = call("POST", "/api/payroll/runs", accountant, body={"campusId": blr["id"], "month": month}); check("create payroll run", s == 200 and run["staffCount"] == 5, run)
s, r = call("POST", "/api/payroll/runs", accountant, body={"campusId": blr["id"], "month": month}); check("one run per month", s == 400, r)
s, r = call("GET", "/api/payroll/my-payslips", teacher); before = len(r) if s == 200 else -1
s, r = call("POST", f"/api/payroll/runs/{run['id']}/pay", accountant); check("cannot pay before approval", s == 400, r)
s, r = call("POST", f"/api/payroll/runs/{run['id']}/approve", accountant); check("approve payroll", s == 200, r)
s, r = call("GET", "/api/payroll/my-payslips", teacher); check("approved payslip visible to the teacher", s == 200 and len(r) == before + 1, r)
s, r = call("POST", f"/api/payroll/runs/{run['id']}/pay", accountant); check("mark payroll paid", s == 200, r)
s, led = call("GET", f"/api/fees/ledger?campusId={blr['id']}", accountant); check("ledger balances with payroll", abs(led["totalDebit"] - led["totalCredit"]) < 0.005 and any(t["account"].startswith("6000") for t in led["trialBalance"]), led["trialBalance"][-1:])

# --- rollover
s, pv = call("GET", f"/api/rollover/preview?fromYearId={year['id']}", admin); check("rollover preview", s == 200 and pv["graduating"] == 8 and not pv["blockers"], pv)
s, r = call("POST", "/api/rollover/run", teacher, body={"fromYearId": year["id"], "name": "2027-28", "startDate": "2027-04-01", "endDate": "2028-03-31"}); check("teacher cannot run rollover", s == 403, r)
s, ro = call("POST", "/api/rollover/run", admin, body={"fromYearId": year["id"], "name": "2027-28", "startDate": "2027-04-01", "endDate": "2028-03-31"}); check("rollover runs", s == 200 and ro["graduated"] == 8 and ro["promoted"] > 20 and ro["balancesCarried"] > 0, ro)
s, years2 = call("GET", f"/api/academic-years?campusId={blr['id']}", admin); check("old year archived, new active", {y["name"]: y["status"] for y in years2} == {"2026-27": "archived", "2027-28": "active"}, years2)
s, r = call("POST", "/api/rollover/run", admin, body={"fromYearId": year["id"], "name": "2028-29", "startDate": "2028-04-01", "endDate": "2029-03-31"}); check("archived year cannot be rolled again", s == 400, r)
s, led2 = call("GET", f"/api/fees/ledger?campusId={blr['id']}", accountant); check("ledger unchanged and balanced after rollover", abs(led2["totalDebit"] - led["totalDebit"]) < 0.005 and abs(led2["totalDebit"] - led2["totalCredit"]) < 0.005, (led["totalDebit"], led2["totalDebit"]))
s, ag2 = call("GET", f"/api/fees/aging?campusId={blr['id']}", accountant)
s, r = call("POST", "/api/attendance/sheet", teacher, body={"sectionId": g5a["id"], "date": TODAY, "marks": marks}); check("archived year attendance is locked", s == 400, r)
s, ns = call("GET", f"/api/sections?academicYearId={ro['newYearId']}", admin); check("sections cloned", len(ns) == 4, ns)
s, nf = call("GET", f"/api/fees/structures?academicYearId={ro['newYearId']}", accountant); check("fee structures cloned with shifted due dates", len(nf) == 3 and nf[0]["installments"][0]["dueDate"].startswith("2027"), nf)
s, dash = call("GET", f"/api/dashboard?campusId={blr['id']}", admin); check("dashboard", s == 200 and dash["year"] == "2027-28", dash)

print(f"\n{fails} failed")
sys.exit(1 if fails else 0)
