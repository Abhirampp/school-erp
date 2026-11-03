import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { Auth } from './core';
import { AttendancePage, TimetablePage } from './pages/academics';
import { DashboardPage, ImportPage, LoginPage, MockPayPage, SetupPage, UsersPage } from './pages/basics';
import { CommunicationPage, MessagesPage } from './pages/comms';
import { ExamsPage } from './pages/exams';
import { FeesPage } from './pages/fees';
import { AdmissionsPage, ApplyPage, StudentDetailPage, StudentsPage } from './pages/people';
import { PortalPage } from './pages/portal';
import { ClassDetailPage, ClassesPage } from './pages/classes';
import { PayrollPage } from './pages/payroll';
import { StaffDetailPage, StaffPage } from './pages/staff';
import { PlatformPage, RolloverPage, SecurityPage } from './pages/security';

const home = (auth: Auth) => (auth.isPlatform() ? '/platform' : auth.isPortal() ? '/portal' : '/dashboard');

/** Hides screens the user has no permission for. The API enforces the same rules; this is only navigation. */
const allow = (...perms: string[]): CanActivateFn => () => {
  const auth = inject(Auth);
  const router = inject(Router);
  if (!auth.token()) return router.parseUrl('/login');
  return perms.length === 0 || auth.can(...perms) ? true : router.parseUrl(home(auth));
};
const staffOnly: CanActivateFn = () => {
  const auth = inject(Auth);
  const router = inject(Router);
  if (!auth.token()) return router.parseUrl('/login');
  return auth.isPortal() || auth.isPlatform() ? router.parseUrl(home(auth)) : true;
};

export const routes: Routes = [
  { path: 'login', component: LoginPage, title: 'Sign in' },
  { path: 'apply/:slug', component: ApplyPage, title: 'Admissions' },
  { path: 'pay/mock/:reference', component: MockPayPage, title: 'Payment' },
  { path: 'dashboard', component: DashboardPage, canActivate: [staffOnly], title: 'Dashboard' },
  { path: 'admissions', component: AdmissionsPage, canActivate: [allow('admissions.manage')], title: 'Admissions' },
  { path: 'students', component: StudentsPage, canActivate: [allow('students.read')], title: 'Students' },
  { path: 'students/:id', component: StudentDetailPage, canActivate: [allow('students.read')], title: 'Student' },
  { path: 'staff', component: StaffPage, canActivate: [staffOnly], title: 'Staff' },
  { path: 'staff/:id', component: StaffDetailPage, canActivate: [staffOnly], title: 'Staff member' },
  { path: 'classes', component: ClassesPage, canActivate: [staffOnly], title: 'Classes' },
  { path: 'classes/:id', component: ClassDetailPage, canActivate: [staffOnly], title: 'Class' },
  { path: 'payroll', component: PayrollPage, canActivate: [staffOnly], title: 'Payroll' },
  { path: 'timetable', component: TimetablePage, canActivate: [allow('timetable.read', 'academics.manage')], title: 'Timetable' },
  { path: 'attendance', component: AttendancePage, canActivate: [staffOnly], title: 'Attendance' },
  { path: 'exams', component: ExamsPage, canActivate: [allow('exams.manage', 'exams.marks', 'exams.approve')], title: 'Exams' },
  { path: 'fees', component: FeesPage, canActivate: [allow('fees.read', 'fees.manage')], title: 'Fees' },
  { path: 'communication', component: CommunicationPage, canActivate: [allow('comms.announce', 'consent.manage')], title: 'Communication' },
  { path: 'messages', component: MessagesPage, canActivate: [allow('comms.message')], title: 'Messages' },
  { path: 'portal', component: PortalPage, canActivate: [allow('portal.parent', 'portal.student')], title: 'Portal' },
  { path: 'setup', component: SetupPage, canActivate: [allow('admin.settings')], title: 'School setup' },
  { path: 'users', component: UsersPage, canActivate: [allow('admin.users')], title: 'Users and roles' },
  { path: 'import', component: ImportPage, canActivate: [allow('admin.import')], title: 'Import' },
  { path: 'security', component: SecurityPage, canActivate: [staffOnly], title: 'Security and audit' },
  { path: 'rollover', component: RolloverPage, canActivate: [allow('rollover.run')], title: 'Year-end rollover' },
  { path: 'platform', component: PlatformPage, canActivate: [allow()], title: 'Platform' },
  { path: '**', canActivate: [() => { const auth = inject(Auth); return inject(Router).parseUrl(auth.token() ? home(auth) : '/login'); }], component: LoginPage },
];
