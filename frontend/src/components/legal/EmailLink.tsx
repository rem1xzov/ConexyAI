import { OPERATOR } from './operator';

// LEGAL_DOCS: добавлено 2026-09-25
/** Ссылка-почта оператора: общий адрес для обращений во всех документах. */
export function EmailLink() {
  return <a href={`mailto:${OPERATOR.email}`}>{OPERATOR.email}</a>;
}
